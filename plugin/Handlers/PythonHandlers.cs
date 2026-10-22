using System;
using System.Text;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;
using Rhino;
using Rhino.Runtime;

namespace OpenRhinoMcp.Handlers
{
    /// <summary>Runs IronPython 2.7 through Rhino's own Python engine (rhinoscriptsyntax available).</summary>
    public static class PythonHandlers
    {
        // Kept on one line so traceback line numbers are off by exactly one.
        const string Prelude = "import rhinoscriptsyntax as rs; import scriptcontext as sc; import Rhino; import System\n";

        public static void Register()
        {
            Registry.Add("python.run", RunPython, MethodFlags.Edit);
            Registry.Add("python.help", Help);
        }

        static PythonScript NewScript(RhinoDoc doc, StringBuilder output)
        {
            var py = PythonScript.Create();
            if (py == null) throw J.Failed("Rhino's Python engine isn't available (is the RhinoPython plug-in disabled?).");
            py.ScriptContextDoc = doc;
            if (output != null) py.Output = s => output.Append(s);
            return py;
        }

        static JToken RunPython(RhinoDoc doc, JObject p)
        {
            var code = J.ReqStr(p, "code");
            var output = new StringBuilder();
            var py = NewScript(doc, output);

            if (J.Obj(p, "inputs") is JObject inputs)
                foreach (var kv in inputs) py.SetVariable(kv.Key, J.ToNet(kv.Value));

            bool ok;
            string error = null, traceback = null;
            try
            {
                ok = py.ExecuteScript(Prelude + code);
                if (!ok) error = "Script returned failure.";
            }
            catch (Exception ex)
            {
                ok = false;
                error = ex.Message;
                try { traceback = py.GetStackTraceFromException(ex); } catch { }
            }

            JToken result = JValue.CreateNull();
            try
            {
                if (py.ContainsVariable("result"))
                    result = Summaries.FromNet(doc, py.GetVariable("result"));
            }
            catch (Exception ex) { result = "<couldn't convert result: " + ex.Message + ">"; }

            var j = new JObject
            {
                ["ok"] = ok,
                ["output"] = J.Truncate(output.ToString(), 50000),
                ["result"] = result
            };
            if (error != null) j["error"] = error;
            if (traceback != null) j["traceback"] = J.Truncate(traceback + "\n(note: line numbers include 1 prelude line)", 8000);
            return j;
        }

        const string HelpScript = @"
import rhinoscriptsyntax as rs, scriptcontext as sc, Rhino, System, inspect
_o = eval(_target)
_kind = type(_o).__name__
_doc = getattr(_o, '__doc__', None) or ''
_sig = None
try:
    if inspect.isfunction(_o):
        _sig = _target.split('.')[-1] + inspect.formatargspec(*inspect.getargspec(_o))
except Exception:
    pass
_members = None
if inspect.ismodule(_o) or isinstance(_o, type) or 'Namespace' in _kind or 'Module' in _kind:
    _members = [m for m in dir(_o) if not m.startswith('_')]
";

        static JToken Help(RhinoDoc doc, JObject p)
        {
            var name = J.ReqStr(p, "name").Trim();
            var py = NewScript(doc, null);
            py.SetVariable("_target", name);
            try
            {
                py.ExecuteScript(HelpScript);
            }
            catch (Exception ex)
            {
                throw J.NotFound($"Couldn't resolve '{name}' in IronPython: {ex.Message}. Try 'rs' to list rhinoscriptsyntax functions, or 'Rhino.Geometry'.");
            }
            var j = new JObject
            {
                ["name"] = name,
                ["kind"] = py.GetVariable("_kind")?.ToString(),
                ["doc"] = J.Truncate(py.GetVariable("_doc")?.ToString() ?? "", 12000)
            };
            var sig = py.GetVariable("_sig");
            if (sig != null) j["signature"] = sig.ToString();
            var members = py.GetVariable("_members");
            if (members != null)
            {
                var arr = Summaries.FromNet(doc, members) as JArray;
                if (arr != null) j["members"] = arr;
            }
            return j;
        }
    }
}
