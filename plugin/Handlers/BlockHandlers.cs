using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace OpenRhinoMcp.Handlers
{
    public static class BlockHandlers
    {
        public static void Register()
        {
            Registry.Add("blocks.list", List);
            Registry.Add("blocks.create", Create, MethodFlags.Edit);
            Registry.Add("blocks.insert", Insert, MethodFlags.Edit);
        }

        static JToken List(RhinoDoc doc, JObject p)
        {
            var counts = J.Bool(p, "include_counts", true);
            var arr = new JArray();
            foreach (var d in doc.InstanceDefinitions.GetList(true).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (d.IsDeleted) continue;
                var j = new JObject
                {
                    ["name"] = d.Name,
                    ["id"] = d.Id.ToString(),
                    ["index"] = d.Index,
                    ["description"] = d.Description ?? "",
                    ["object_count"] = d.ObjectCount,
                    ["is_linked"] = d.UpdateType != InstanceDefinitionUpdateType.Static
                };
                if (!string.IsNullOrEmpty(d.SourceArchive)) j["path"] = d.SourceArchive;
                if (counts) j["instance_count"] = d.GetReferences(0)?.Length ?? 0;
                arr.Add(j);
            }
            return new JObject { ["blocks"] = arr };
        }

        static InstanceDefinition Def(RhinoDoc doc, string name)
        {
            var d = doc.InstanceDefinitions.Find(name);
            if (d == null || d.IsDeleted) throw J.NotFound($"No block definition '{name}'. Use rhino_list_blocks.");
            return d;
        }

        static JToken Create(RhinoDoc doc, JObject p)
        {
            var name = J.ReqStr(p, "name");
            var existing = doc.InstanceDefinitions.Find(name);
            if (existing != null && !existing.IsDeleted) throw J.Failed($"A block named '{name}' already exists.");

            var objs = DocUtil.FindAll(doc, J.Ids(p, "ids"));
            var basePt = J.ReqPt(p, "base_point");
            var geoms = objs.Select(o => o.Geometry.Duplicate()).ToList();
            var attrs = objs.Select(o => o.Attributes.Duplicate()).ToList();

            var idx = doc.InstanceDefinitions.Add(name, J.Str(p, "description", ""), basePt, geoms, attrs);
            if (idx < 0) throw J.Failed($"Rhino couldn't create block '{name}' (invalid name or geometry).");

            var result = new JObject { ["definition"] = name };
            if (J.Bool(p, "replace_with_instance", true))
            {
                var att = doc.CreateDefaultAttributes();
                att.LayerIndex = objs[0].Attributes.LayerIndex;
                var iid = doc.Objects.AddInstanceObject(idx, Transform.Translation(basePt - Point3d.Origin), att);
                result["instance_id"] = iid == Guid.Empty ? null : iid.ToString();
            }
            if (J.Bool(p, "delete_input", true))
                foreach (var o in objs) doc.Objects.Delete(o.Id, true);
            return result;
        }

        static JToken Insert(RhinoDoc doc, JObject p)
        {
            var items = J.Arr(p, "items") ?? throw J.Bad("'items' is required.");
            var ids = new JArray();
            var errors = new JArray();
            for (int i = 0; i < items.Count; i++)
            {
                try
                {
                    var it = items[i] as JObject ?? throw J.Bad("Each item must be an object.");
                    var def = Def(doc, J.ReqStr(it, "name"));
                    var pt = J.ReqPt(it, "point");
                    var rot = RhinoMath.ToRadians(J.Num(it, "rotation_deg", 0));

                    Transform scale;
                    var s = J.Get(it, "scale");
                    if (s is JArray sa)
                        scale = Transform.Scale(Plane.WorldXY, sa[0].Value<double>(), sa[1].Value<double>(), sa.Count > 2 ? sa[2].Value<double>() : 1.0);
                    else
                        scale = Transform.Scale(Point3d.Origin, s == null ? 1.0 : s.Value<double>());

                    var xf = Transform.Translation(pt - Point3d.Origin) * Transform.Rotation(rot, Vector3d.ZAxis, Point3d.Origin) * scale;
                    var att = DocUtil.NewAttributes(doc, J.Obj(it, "attributes"));
                    var id = doc.Objects.AddInstanceObject(def.Index, xf, att);
                    if (id == Guid.Empty) throw J.Failed("Rhino rejected the block instance.");
                    ids.Add(id.ToString());
                }
                catch (Exception ex)
                {
                    ids.Add(JValue.CreateNull());
                    errors.Add(new JObject { ["index"] = i, ["message"] = ex.Message });
                }
            }
            return new JObject { ["ids"] = ids, ["errors"] = errors };
        }
    }
}
