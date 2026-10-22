using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace OpenRhinoMcp.Core
{
    /// <summary>Document helpers: layers by path, attributes, object lookup.</summary>
    public static class DocUtil
    {
        public const string PathSep = "::";

        public static double Tol(RhinoDoc doc) => doc.ModelAbsoluteTolerance;
        public static double AngTolRad(RhinoDoc doc) => doc.ModelAngleToleranceRadians;

        public static RhinoObject Find(RhinoDoc doc, Guid id, bool required = true)
        {
            var o = doc.Objects.FindId(id);
            if (o == null && required) throw J.NotFound($"No object with id {id}.");
            return o;
        }

        public static List<RhinoObject> FindAll(RhinoDoc doc, IEnumerable<Guid> ids)
        {
            var list = new List<RhinoObject>();
            var missing = new List<string>();
            foreach (var id in ids)
            {
                var o = doc.Objects.FindId(id);
                if (o == null) missing.Add(id.ToString());
                else list.Add(o);
            }
            if (missing.Count > 0)
                throw new RpcException("not_found", $"{missing.Count} id(s) not found.", new JObject { ["missing"] = new JArray(missing) });
            return list;
        }

        public static string LayerPath(RhinoDoc doc, int index)
        {
            if (index < 0 || index >= doc.Layers.Count) return "";
            return doc.Layers[index].FullPath;
        }

        public static int FindLayer(RhinoDoc doc, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return -1;
            return doc.Layers.FindByFullPath(path.Trim(), -1);
        }

        public static int RequireLayer(RhinoDoc doc, string path)
        {
            var idx = FindLayer(doc, path);
            if (idx < 0) throw J.NotFound($"No layer '{path}'. Use full paths like \"Parent::Child\".");
            return idx;
        }

        /// <summary>Finds a layer by full path, creating it (and any parents) if missing.</summary>
        public static int EnsureLayer(RhinoDoc doc, string path, out bool created)
        {
            created = false;
            path = path.Trim();
            var idx = FindLayer(doc, path);
            if (idx >= 0) return idx;

            var parts = path.Split(new[] { PathSep }, StringSplitOptions.None).Select(s => s.Trim()).ToArray();
            if (parts.Any(string.IsNullOrEmpty)) throw J.Bad($"Layer path '{path}' has an empty segment.");
            var parentId = Guid.Empty;
            for (int i = 0; i < parts.Length; i++)
            {
                var sub = string.Join(PathSep, parts.Take(i + 1));
                idx = FindLayer(doc, sub);
                if (idx < 0)
                {
                    var layer = new Layer { Name = parts[i] };
                    if (parentId != Guid.Empty) layer.ParentLayerId = parentId;
                    idx = doc.Layers.Add(layer);
                    if (idx < 0) throw J.Failed($"Couldn't create layer '{sub}' (invalid name?).");
                    created = true;
                }
                parentId = doc.Layers[idx].Id;
            }
            return idx;
        }

        public static int EnsureLayer(RhinoDoc doc, string path) => EnsureLayer(doc, path, out _);

        public static int RequireLinetype(RhinoDoc doc, string name)
        {
            var lt = doc.Linetypes.FindName(name);
            if (lt == null) throw J.NotFound($"No linetype '{name}'.");
            return lt.Index;
        }

        public static DimensionStyle DimStyle(RhinoDoc doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return doc.DimStyles.Current;
            var ds = doc.DimStyles.FindName(name);
            if (ds == null) throw J.NotFound($"No dimension style '{name}'.");
            return ds;
        }

        public static ObjectAttributes NewAttributes(RhinoDoc doc, JObject a)
        {
            var att = doc.CreateDefaultAttributes();
            ApplyAttributes(doc, att, a);
            if (a != null)
            {
                var visible = J.OptBool(a, "visible");
                if (visible.HasValue) att.Visible = visible.Value;
                var locked = J.OptBool(a, "locked");
                if (locked == true) att.Mode = ObjectMode.Locked;
            }
            return att;
        }

        /// <summary>Applies the Attributes shape (see PROTOCOL.md) except visible/locked, which callers handle.</summary>
        public static void ApplyAttributes(RhinoDoc doc, ObjectAttributes att, JObject a)
        {
            if (a == null) return;

            var layer = J.Str(a, "layer");
            if (!string.IsNullOrEmpty(layer)) att.LayerIndex = EnsureLayer(doc, layer);

            // Page-space placement: put the object on a layout instead of in model space.
            var layout = J.Str(a, "layout");
            if (layout != null)
            {
                if (layout.Length == 0 || layout.Equals("model", StringComparison.OrdinalIgnoreCase))
                {
                    att.Space = ActiveSpace.ModelSpace;
                }
                else
                {
                    var pv = FindView(doc, layout) as RhinoPageView;
                    if (pv == null) throw J.NotFound($"'{layout}' is not a layout.");
                    att.Space = ActiveSpace.PageSpace;
                    att.ViewportId = pv.MainViewport.Id;
                }
            }

            if (a["name"] != null) att.Name = J.Str(a, "name") ?? "";

            if (J.Has(a, "color"))
            {
                att.ObjectColor = J.ParseColor(a["color"]);
                att.ColorSource = ObjectColorSource.ColorFromObject;
            }
            var cs = J.Str(a, "color_source");
            if (cs != null)
            {
                switch (cs.ToLowerInvariant())
                {
                    case "layer": att.ColorSource = ObjectColorSource.ColorFromLayer; break;
                    case "object": att.ColorSource = ObjectColorSource.ColorFromObject; break;
                    case "material": att.ColorSource = ObjectColorSource.ColorFromMaterial; break;
                    case "parent": att.ColorSource = ObjectColorSource.ColorFromParent; break;
                    default: throw J.Bad("color_source must be layer, object, material or parent.");
                }
            }

            var lt = J.Str(a, "linetype");
            if (lt != null)
            {
                if (lt.Equals("bylayer", StringComparison.OrdinalIgnoreCase))
                    att.LinetypeSource = ObjectLinetypeSource.LinetypeFromLayer;
                else
                {
                    att.LinetypeIndex = RequireLinetype(doc, lt);
                    att.LinetypeSource = ObjectLinetypeSource.LinetypeFromObject;
                }
            }

            var pw = J.OptNum(a, "print_width");
            if (pw.HasValue)
            {
                att.PlotWeight = pw.Value;
                att.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject;
            }
            if (J.Has(a, "print_color"))
            {
                att.PlotColor = J.ParseColor(a["print_color"]);
                att.PlotColorSource = ObjectPlotColorSource.PlotColorFromObject;
            }

            if (J.Obj(a, "user_text") is JObject ut)
            {
                foreach (var kv in ut)
                {
                    if (kv.Value == null || kv.Value.Type == JTokenType.Null) att.DeleteUserString(kv.Key);
                    else att.SetUserString(kv.Key, kv.Value.Type == JTokenType.String ? (string)kv.Value : kv.Value.ToString());
                }
            }

            if (J.Arr(a, "groups") is JArray groups)
            {
                foreach (var g in groups)
                {
                    var name = g.ToString();
                    var gi = doc.Groups.Find(name);
                    if (gi < 0) gi = doc.Groups.Add(name);
                    if (gi >= 0) att.AddToGroup(gi);
                }
            }
        }

        public static RhinoView FindView(RhinoDoc doc, string name, bool required = true)
        {
            if (string.IsNullOrEmpty(name))
            {
                var av = doc.Views.ActiveView;
                if (av == null && required) throw J.Failed("No active view.");
                return av;
            }
            foreach (var v in doc.Views.GetViewList(true, true))
            {
                var vn = v is RhinoPageView pv ? pv.PageName : v.MainViewport.Name;
                if (string.Equals(vn, name, StringComparison.OrdinalIgnoreCase)) return v;
            }
            if (required) throw J.NotFound($"No view or layout named '{name}'. Use rhino_list_views.");
            return null;
        }

        public static string ViewName(RhinoView v) => v is RhinoPageView pv ? pv.PageName : v.MainViewport.Name;

        public static BoundingBox UnionBox(IEnumerable<RhinoObject> objs)
        {
            var box = BoundingBox.Empty;
            foreach (var o in objs)
            {
                var b = o.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
                if (b.IsValid) box.Union(b);
            }
            return box;
        }

        public static UnitSystem ParseUnits(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "in": case "inch": case "inches": return UnitSystem.Inches;
                case "ft": case "foot": case "feet": return UnitSystem.Feet;
                case "mm": case "millimeter": case "millimeters": return UnitSystem.Millimeters;
                case "cm": case "centimeter": case "centimeters": return UnitSystem.Centimeters;
                case "m": case "meter": case "meters": return UnitSystem.Meters;
                case "yd": case "yard": case "yards": return UnitSystem.Yards;
            }
            if (Enum.TryParse<UnitSystem>(s, true, out var u)) return u;
            throw J.Bad($"Unknown units '{s}'.");
        }
    }
}
