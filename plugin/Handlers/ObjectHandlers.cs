using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace OpenRhinoMcp.Handlers
{
    public static class ObjectHandlers
    {
        public static void Register()
        {
            Registry.Add("objects.query", Query);
            Registry.Add("objects.get", Get);
            Registry.Add("objects.select", Select, MethodFlags.ReadOnly);
            Registry.Add("objects.delete", Delete, MethodFlags.Edit);
            Registry.Add("objects.set_attributes", SetAttributes, MethodFlags.Edit);
            Registry.Add("objects.transform", TransformObjects, MethodFlags.Edit);
        }

        static Regex Glob(string pattern) =>
            new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

        public static IEnumerable<RhinoObject> Filter(RhinoDoc doc, JObject p)
        {
            var settings = new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = J.Bool(p, "include_locked", true),
                HiddenObjects = J.Bool(p, "include_hidden", false),
                IncludeLights = false,
                IncludeGrips = false,
                DeletedObjects = false,
                ReferenceObjects = true,
                SelectedObjectsFilter = J.Bool(p, "selected_only", false)
            };

            IEnumerable<RhinoObject> objs;
            var ids = J.Ids(p, "ids", false);
            objs = ids != null ? ids.Select(id => doc.Objects.FindId(id)).Where(o => o != null) : doc.Objects.GetObjectList(settings);

            if (J.Arr(p, "types") is JArray types && types.Count > 0)
            {
                var set = new HashSet<string>(types.Select(t => t.ToString().ToLowerInvariant()));
                objs = objs.Where(o => set.Contains(Summaries.TypeName(o)));
            }

            var layer = J.Str(p, "layer");
            if (!string.IsNullOrEmpty(layer))
            {
                var root = DocUtil.RequireLayer(doc, layer);
                var rootPath = doc.Layers[root].FullPath;
                var indices = new HashSet<int> { root };
                if (J.Bool(p, "include_sublayers", true))
                    foreach (var l in doc.Layers)
                        if (!l.IsDeleted && l.FullPath.StartsWith(rootPath + DocUtil.PathSep, StringComparison.OrdinalIgnoreCase))
                            indices.Add(l.Index);
                objs = objs.Where(o => indices.Contains(o.Attributes.LayerIndex));
            }

            var name = J.Str(p, "name");
            if (!string.IsNullOrEmpty(name))
            {
                var rx = Glob(name);
                objs = objs.Where(o => rx.IsMatch(o.Attributes.Name ?? ""));
            }

            if (J.Obj(p, "user_text") is JObject ut)
            {
                foreach (var kv in ut)
                {
                    var key = kv.Key;
                    var want = kv.Value?.Type == JTokenType.Null ? "*" : kv.Value?.ToString() ?? "*";
                    if (want == "*")
                        objs = objs.Where(o => o.Attributes.GetUserString(key) != null);
                    else if (want.Contains("*") || want.Contains("?"))
                    {
                        var rx = Glob(want);
                        objs = objs.Where(o => { var v = o.Attributes.GetUserString(key); return v != null && rx.IsMatch(v); });
                    }
                    else
                        objs = objs.Where(o => string.Equals(o.Attributes.GetUserString(key), want, StringComparison.OrdinalIgnoreCase));
                }
            }

            var block = J.Str(p, "block");
            if (!string.IsNullOrEmpty(block))
            {
                var rx = Glob(block);
                objs = objs.Where(o => o is InstanceObject io && io.InstanceDefinition != null && rx.IsMatch(io.InstanceDefinition.Name));
            }

            if (J.Obj(p, "bbox") is JObject bb)
            {
                var box = new BoundingBox(J.ReqPt(bb, "min"), J.ReqPt(bb, "max"));
                objs = objs.Where(o =>
                {
                    var b = o.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
                    return b.IsValid && b.Min.X <= box.Max.X && b.Max.X >= box.Min.X
                        && b.Min.Y <= box.Max.Y && b.Max.Y >= box.Min.Y
                        && b.Min.Z <= box.Max.Z && b.Max.Z >= box.Min.Z;
                });
            }

            var textContains = J.Str(p, "text_contains");
            if (!string.IsNullOrEmpty(textContains))
                objs = objs.Where(o => (Summaries.TextOf(o.Geometry) ?? "").IndexOf(textContains, StringComparison.OrdinalIgnoreCase) >= 0);

            return objs;
        }

        static JToken Query(RhinoDoc doc, JObject p)
        {
            var limit = Math.Max(1, Math.Min(5000, J.Int(p, "limit", 200)));
            var offset = Math.Max(0, J.Int(p, "offset", 0));
            var all = Filter(doc, p).ToList();
            var page = all.Skip(offset).Take(limit).Select(o => Summaries.Summary(doc, o));
            return new JObject
            {
                ["total"] = all.Count,
                ["count"] = Math.Max(0, Math.Min(limit, all.Count - offset)),
                ["offset"] = offset,
                ["objects"] = new JArray(page)
            };
        }

        static JToken Get(RhinoDoc doc, JObject p)
        {
            var ids = J.Ids(p, "ids");
            var full = (J.Str(p, "detail", "full") ?? "full").Equals("full", StringComparison.OrdinalIgnoreCase);
            var arr = new JArray();
            foreach (var id in ids.Take(500))
            {
                var o = doc.Objects.FindId(id);
                if (o == null) arr.Add(new JObject { ["id"] = id.ToString(), ["error"] = "not found" });
                else arr.Add(full ? Summaries.Detail(doc, o) : Summaries.Summary(doc, o));
            }
            return new JObject { ["objects"] = arr };
        }

        static JToken Select(RhinoDoc doc, JObject p)
        {
            var mode = (J.Str(p, "mode", "replace") ?? "replace").ToLowerInvariant();
            var ids = J.Ids(p, "ids", false) ?? new List<Guid>();
            if (mode == "replace" || mode == "clear") doc.Objects.UnselectAll();
            var failed = new JArray();
            if (mode != "clear")
            {
                bool select = mode != "remove";
                foreach (var id in ids)
                    if (!doc.Objects.Select(id, select)) failed.Add(id.ToString());
            }
            doc.Views.Redraw();
            return new JObject
            {
                ["selected_count"] = doc.Objects.GetSelectedObjects(false, false).Count(),
                ["failed_ids"] = failed
            };
        }

        static JToken Delete(RhinoDoc doc, JObject p)
        {
            var failed = new JArray();
            foreach (var id in J.Ids(p, "ids"))
            {
                var o = doc.Objects.FindId(id);
                if (o == null) { failed.Add(id.ToString()); continue; }
                if (o.IsLocked) doc.Objects.Unlock(id, true);
                if (!doc.Objects.Delete(id, true)) failed.Add(id.ToString());
            }
            return new JObject { ["failed_ids"] = failed };
        }

        static JToken SetAttributes(RhinoDoc doc, JObject p)
        {
            var attrs = J.Obj(p, "attributes") ?? throw J.Bad("'attributes' is required.");
            var visible = J.OptBool(attrs, "visible");
            var locked = J.OptBool(attrs, "locked");
            var failed = new JArray();
            foreach (var o in DocUtil.FindAll(doc, J.Ids(p, "ids")))
            {
                var wasLocked = o.IsLocked;
                if (wasLocked) doc.Objects.Unlock(o.Id, true);
                var att = o.Attributes.Duplicate();
                DocUtil.ApplyAttributes(doc, att, attrs);
                if (!doc.Objects.ModifyAttributes(o.Id, att, true)) failed.Add(o.Id.ToString());

                if (visible == true) doc.Objects.Show(o.Id, true);
                if (visible == false) doc.Objects.Hide(o.Id, true);
                if (locked == true || (locked == null && wasLocked)) doc.Objects.Lock(o.Id, true);
            }
            return new JObject { ["failed_ids"] = failed };
        }

        static JToken TransformObjects(RhinoDoc doc, JObject p)
        {
            var objs = DocUtil.FindAll(doc, J.Ids(p, "ids"));
            var op = J.ReqStr(p, "op").ToLowerInvariant();
            var copy = J.Bool(p, "copy", false);
            var bbox = DocUtil.UnionBox(objs);
            var center = J.PtOr(p, "center", bbox.IsValid ? bbox.Center : Point3d.Origin);
            Transform xf;

            switch (op)
            {
                case "move":
                    xf = Transform.Translation(J.Vec(J.Get(p, "translation") ?? throw J.Bad("'translation' is required."), "translation"));
                    break;
                case "rotate":
                    var axis = J.VecOr(p, "axis", Vector3d.ZAxis);
                    xf = Transform.Rotation(RhinoMath.ToRadians(J.ReqNum(p, "angle_deg")), axis, center);
                    break;
                case "scale":
                    var f = J.Get(p, "factor") ?? throw J.Bad("'factor' is required.");
                    if (f is JArray fa)
                    {
                        var pl = Plane.WorldXY;
                        pl.Origin = center;
                        xf = Transform.Scale(pl, fa[0].Value<double>(), fa[1].Value<double>(), fa.Count > 2 ? fa[2].Value<double>() : 1.0);
                    }
                    else xf = Transform.Scale(center, f.Value<double>());
                    break;
                case "mirror":
                    Plane mp;
                    if (J.Has(p, "plane")) mp = J.ParsePlane(doc, p["plane"]);
                    else { mp = Plane.WorldYZ; mp.Origin = center; }
                    xf = Transform.Mirror(mp);
                    break;
                case "matrix":
                    var m = J.Arr(p, "matrix") ?? throw J.Bad("'matrix' is required (4x4 rows).");
                    xf = Transform.Identity;
                    for (int r = 0; r < 4; r++)
                        for (int c = 0; c < 4; c++)
                            xf[r, c] = m[r][c].Value<double>();
                    break;
                default:
                    throw J.Bad("op must be move, rotate, scale, mirror or matrix.");
            }

            var ids = new JArray();
            foreach (var o in objs)
            {
                var nid = doc.Objects.Transform(o.Id, xf, !copy);
                if (nid == Guid.Empty) throw J.Failed($"Couldn't transform {o.Id} (locked?).");
                ids.Add(nid.ToString());
            }
            return new JObject { ["ids"] = ids };
        }
    }
}
