using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;
using Rhino;
using Rhino.DocObjects;

namespace OpenRhinoMcp.Handlers
{
    public static class LayerHandlers
    {
        public static void Register()
        {
            Registry.Add("layers.list", List);
            Registry.Add("layers.upsert", Upsert, MethodFlags.Edit);
            Registry.Add("layers.delete", Delete, MethodFlags.Edit);
        }

        public static JObject ToJ(RhinoDoc doc, Layer l, Dictionary<int, int> counts)
        {
            var parent = l.ParentLayerId != Guid.Empty ? doc.Layers.FindId(l.ParentLayerId) : null;
            string linetype = "Continuous";
            if (l.LinetypeIndex >= 0 && l.LinetypeIndex < doc.Linetypes.Count) linetype = doc.Linetypes[l.LinetypeIndex].Name;
            var j = new JObject
            {
                ["path"] = l.FullPath,
                ["name"] = l.Name,
                ["id"] = l.Id.ToString(),
                ["index"] = l.Index,
                ["parent"] = parent?.FullPath,
                ["color"] = J.Hex(l.Color),
                ["visible"] = l.IsVisible,
                ["locked"] = l.IsLocked,
                ["linetype"] = linetype,
                ["print_width"] = l.PlotWeight,
                ["print_color"] = J.Hex(l.PlotColor)
            };
            if (counts != null) j["object_count"] = counts.TryGetValue(l.Index, out var n) ? n : 0;
            return j;
        }

        static JToken List(RhinoDoc doc, JObject p)
        {
            Dictionary<int, int> counts = null;
            if (J.Bool(p, "include_counts", true))
            {
                counts = new Dictionary<int, int>();
                var s = new ObjectEnumeratorSettings { HiddenObjects = true, LockedObjects = true, NormalObjects = true, IncludeLights = true, IncludeGrips = false };
                foreach (var o in doc.Objects.GetObjectList(s))
                {
                    var li = o.Attributes.LayerIndex;
                    counts[li] = counts.TryGetValue(li, out var n) ? n + 1 : 1;
                }
            }
            var layers = doc.Layers.Where(l => !l.IsDeleted).OrderBy(l => l.SortIndex).Select(l => ToJ(doc, l, counts));
            return new JObject
            {
                ["current"] = doc.Layers.CurrentLayer?.FullPath,
                ["layers"] = new JArray(layers)
            };
        }

        static JToken Upsert(RhinoDoc doc, JObject p)
        {
            var path = J.ReqStr(p, "path");
            var idx = DocUtil.EnsureLayer(doc, path, out bool created);
            var layer = doc.Layers[idx];

            if (J.Has(p, "color")) layer.Color = J.ParseColor(p["color"]);
            var locked = J.OptBool(p, "locked");
            if (locked.HasValue) layer.IsLocked = locked.Value;
            var lt = J.Str(p, "linetype");
            if (lt != null) layer.LinetypeIndex = lt.Equals("continuous", StringComparison.OrdinalIgnoreCase) ? -1 : DocUtil.RequireLinetype(doc, lt);
            var pw = J.OptNum(p, "print_width");
            if (pw.HasValue) layer.PlotWeight = pw.Value;
            if (J.Has(p, "print_color")) layer.PlotColor = J.ParseColor(p["print_color"]);
            var rename = J.Str(p, "rename_to");
            if (!string.IsNullOrEmpty(rename))
            {
                if (rename.Contains(DocUtil.PathSep)) throw J.Bad("rename_to is the new leaf name only (no '::').");
                layer.Name = rename;
            }

            var visible = J.OptBool(p, "visible");
            if (visible.HasValue)
            {
                if (!visible.Value && doc.Layers.CurrentLayerIndex == idx)
                    throw J.Failed("Can't hide the current layer. Make another layer current first.");
                layer.IsVisible = visible.Value;
            }

            if (!doc.Layers.Modify(layer, idx, true)) throw J.Failed($"Rhino refused to modify layer '{path}'.");

            if (J.Bool(p, "make_current", false))
            {
                if (!doc.Layers.SetCurrentLayerIndex(idx, true)) throw J.Failed("Couldn't make the layer current (hidden or locked?).");
            }

            return new JObject { ["layer"] = ToJ(doc, doc.Layers[idx], null), ["created"] = created };
        }

        static JToken Delete(RhinoDoc doc, JObject p)
        {
            var path = J.ReqStr(p, "path");
            var idx = DocUtil.RequireLayer(doc, path);
            if (doc.Layers.CurrentLayerIndex == idx) throw J.Failed("Can't delete the current layer. Make another layer current first.");

            var layer = doc.Layers[idx];
            var objects = doc.Objects.FindByLayer(layer) ?? new RhinoObject[0];
            bool deleteObjects = J.Bool(p, "delete_objects", false);
            if (objects.Length > 0 && !deleteObjects)
                throw J.Failed($"Layer '{path}' has {objects.Length} object(s). Pass delete_objects: true to delete them too, or move them first.");

            bool ok = deleteObjects ? doc.Layers.Purge(idx, true) : doc.Layers.Delete(idx, true);
            if (!ok) throw J.Failed($"Rhino couldn't delete '{path}' (it may have sublayers or contain locked/hidden objects).");
            return new JObject { ["success"] = true };
        }
    }
}
