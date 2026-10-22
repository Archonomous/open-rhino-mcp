using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;
using Rhino;
using Rhino.Commands;
using Rhino.FileIO;

namespace OpenRhinoMcp.Handlers
{
    public static class DocHandlers
    {
        public static void Register()
        {
            Registry.Add("doc.info", Info);
            Registry.Add("doc.file", FileOp, MethodFlags.Mutating);
            Registry.Add("doc.undo", (doc, p) => Repeat(doc, "_Undo", J.Int(p, "steps", 1)), MethodFlags.Mutating);
            Registry.Add("doc.redo", (doc, p) => Repeat(doc, "_Redo", J.Int(p, "steps", 1)), MethodFlags.Mutating);
        }

        static JToken Info(RhinoDoc doc, JObject p)
        {
            var j = SystemHandlers.Status(doc);
            j["layers"] = doc.Layers.Count(l => !l.IsDeleted);
            j["layouts"] = new JArray(doc.Views.GetPageViews().OrderBy(v => v.PageNumber).Select(v => v.PageName));
            var named = new JArray();
            for (int i = 0; i < doc.NamedViews.Count; i++) named.Add(doc.NamedViews[i].Name);
            j["named_views"] = named;
            j["blocks"] = doc.InstanceDefinitions.GetList(true).Length;
            j["dimstyles"] = new JArray(doc.DimStyles.Where(d => !d.IsDeleted).Select(d => d.Name));
            j["current_dimstyle"] = doc.DimStyles.Current?.Name;
            j["linetypes"] = new JArray(doc.Linetypes.Where(l => !l.IsDeleted).Select(l => l.Name));
            j["hatch_patterns"] = new JArray(doc.HatchPatterns.Where(h => !h.IsDeleted).Select(h => h.Name));
            j["materials"] = doc.Materials.Count;
            j["groups"] = new JArray(doc.Groups.GroupNames(true) ?? new string[0]);
            j["document_user_text"] = DocUserText(doc);
            j["notes"] = doc.Notes ?? "";
            return j;
        }

        public static JObject DocUserText(RhinoDoc doc)
        {
            var o = new JObject();
            var n = doc.Strings.Count;
            for (int i = 0; i < n; i++)
            {
                var key = doc.Strings.GetKey(i);
                if (key != null) o[key] = doc.Strings.GetValue(i);
            }
            return o;
        }

        static string Q(string path) => "\"" + path.Replace("\"", "") + "\"";

        static JToken FileOp(RhinoDoc doc, JObject p)
        {
            if (Command.InCommand())
                throw new RpcException("rhino_busy", "A Rhino command is running. Finish or cancel it first.");

            var action = J.ReqStr(p, "action").ToLowerInvariant();
            var path = J.Str(p, "path");
            var discard = J.Bool(p, "discard_changes", false);
            bool ok;

            void RequirePath()
            {
                if (string.IsNullOrWhiteSpace(path)) throw J.Bad($"'{action}' needs a 'path'.");
                path = Path.GetFullPath(path);
            }

            void GuardModified()
            {
                if (doc.Modified && !discard)
                    throw J.Failed("The document has unsaved changes. Save first, or pass discard_changes: true if the user agreed to lose them.");
                if (discard) doc.Modified = false;
            }

            switch (action)
            {
                case "new":
                    GuardModified();
                    ok = RhinoApp.RunScript("_-New _None", false);
                    break;
                case "open":
                    RequirePath();
                    if (!File.Exists(path)) throw J.NotFound($"No file at {path}.");
                    GuardModified();
                    ok = RhinoApp.RunScript("_-Open " + Q(path), false);
                    break;
                case "save":
                    if (string.IsNullOrEmpty(doc.Path))
                    {
                        if (string.IsNullOrWhiteSpace(path)) throw J.Bad("The document has never been saved; pass a 'path' (or use save_as).");
                        goto case "save_as";
                    }
                    ok = RhinoApp.RunScript("_-Save _Enter", false);
                    path = doc.Path;
                    break;
                case "save_as":
                    RequirePath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    ok = RhinoApp.RunScript("_-SaveAs " + Q(path) + " _Enter", false);
                    break;
                case "save_copy":
                    RequirePath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    ok = doc.WriteFile(path, new FileWriteOptions());
                    break;
                case "import":
                    RequirePath();
                    if (!File.Exists(path)) throw J.NotFound($"No file at {path}.");
                    ok = RhinoApp.RunScript("_-Import " + Q(path) + " _Enter", false);
                    break;
                case "export_selected":
                    RequirePath();
                    var ids = J.Ids(p, "select_ids", false);
                    if (ids != null)
                    {
                        doc.Objects.UnselectAll();
                        foreach (var id in ids) doc.Objects.Select(id, true);
                    }
                    if (!doc.Objects.GetSelectedObjects(false, false).Any()) throw J.Bad("Nothing selected to export. Pass select_ids.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    ok = RhinoApp.RunScript("_-Export " + Q(path) + " _Enter", false);
                    break;
                default:
                    throw J.Bad("action must be new, open, save, save_as, save_copy, import or export_selected.");
            }

            var active = RhinoDoc.ActiveDoc ?? doc;
            return new JObject
            {
                ["success"] = ok,
                ["path"] = path,
                ["doc_path"] = active.Path,
                ["modified"] = active.Modified
            };
        }

        static JToken Repeat(RhinoDoc doc, string macro, int steps)
        {
            if (Command.InCommand()) throw new RpcException("rhino_busy", "A Rhino command is running.");
            steps = Math.Max(1, Math.Min(100, steps));
            bool ok = true;
            for (int i = 0; i < steps && ok; i++) ok = RhinoApp.RunScript(macro, false);
            return new JObject { ["success"] = ok };
        }
    }
}
