using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Bridge;
using OpenRhinoMcp.Core;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace OpenRhinoMcp.Handlers
{
    public static class SystemHandlers
    {
        public static void Register()
        {
            Registry.Add("system.hello", (doc, p) => new JObject
            {
                ["protocol"] = BridgeServer.ProtocolVersion,
                ["plugin_version"] = OpenRhinoMcpPlugin.Instance?.Version,
                ["rhino_version"] = RhinoApp.Version.ToString(),
                ["platform"] = Rhino.Runtime.HostUtils.RunningOnOSX ? "macos" : "windows",
                ["methods"] = new JArray(Registry.Methods)
            }, MethodFlags.NoDoc);

            Registry.Add("system.status", (doc, p) => Status(doc), MethodFlags.NoDoc);
        }

        public static JObject Status(RhinoDoc doc)
        {
            var j = new JObject
            {
                ["rhino_version"] = RhinoApp.Version.ToString(),
                ["command_running"] = Command.InCommand()
            };
            if (doc == null)
            {
                j["doc_name"] = null;
                return j;
            }
            var settings = new ObjectEnumeratorSettings { HiddenObjects = true, LockedObjects = true, NormalObjects = true, IncludeLights = false, IncludeGrips = false };
            j["doc_name"] = doc.Name;
            j["doc_path"] = doc.Path;
            j["modified"] = doc.Modified;
            j["units"] = doc.ModelUnitSystem.ToString();
            j["page_units"] = doc.PageUnitSystem.ToString();
            j["abs_tolerance"] = doc.ModelAbsoluteTolerance;
            j["angle_tolerance_deg"] = doc.ModelAngleToleranceDegrees;
            j["object_count"] = doc.Objects.GetObjectList(settings).Count();
            j["layer_count"] = doc.Layers.Count(l => !l.IsDeleted);
            j["current_layer"] = doc.Layers.CurrentLayer?.FullPath;
            var av = doc.Views.ActiveView;
            j["active_view"] = av != null ? DocUtil.ViewName(av) : null;
            j["selected_count"] = doc.Objects.GetSelectedObjects(false, false).Count();
            return j;
        }
    }
}
