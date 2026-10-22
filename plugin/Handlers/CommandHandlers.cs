using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;
using Rhino;
using Rhino.Commands;

namespace OpenRhinoMcp.Handlers
{
    public static class CommandHandlers
    {
        const int MaxHistory = 20000;

        public static void Register()
        {
            Registry.Add("commands.list", List, MethodFlags.NoDoc);
            Registry.Add("commands.run", Run, MethodFlags.Mutating);
            Registry.Add("commands.history", (doc, p) =>
            {
                var lines = Math.Max(1, Math.Min(2000, J.Int(p, "lines", 50)));
                var text = RhinoApp.CommandHistoryWindowText ?? "";
                var all = text.Replace("\r\n", "\n").Split('\n');
                return new JObject { ["text"] = string.Join("\n", all.Skip(Math.Max(0, all.Length - lines))) };
            }, MethodFlags.NoDoc);
        }

        static JToken List(RhinoDoc doc, JObject p)
        {
            var filter = J.Str(p, "filter");
            var limit = Math.Max(1, Math.Min(5000, J.Int(p, "limit", 2000)));
            var names = Command.GetCommandNames(true, true) ?? new string[0];
            var list = names.Where(n => !string.IsNullOrEmpty(n))
                            .Where(n => filter == null || n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToList();
            return new JObject { ["count"] = list.Count, ["commands"] = new JArray(list.Take(limit)) };
        }

        public static JToken Run(RhinoDoc doc, JObject p)
        {
            if (Command.InCommand())
                throw new RpcException("rhino_busy", "A Rhino command is already running. Finish or cancel it (Esc) in Rhino, then retry.");

            var macro = J.ReqStr(p, "macro");
            var echo = J.Bool(p, "echo", false);
            var ids = J.Ids(p, "select_ids", false);
            if (ids != null)
            {
                doc.Objects.UnselectAll();
                foreach (var id in ids)
                    if (!doc.Objects.Select(id, true)) throw J.NotFound($"Couldn't select {id} (missing, locked or hidden?).");
            }

            var before = RhinoApp.CommandHistoryWindowText ?? "";
            bool ok = RhinoApp.RunScript(macro, echo);
            var after = RhinoApp.CommandHistoryWindowText ?? "";

            var result = new JObject { ["success"] = ok };
            if (J.Bool(p, "capture_history", true))
                result["history"] = J.Truncate(HistoryDelta(before, after), MaxHistory);
            return result;
        }

        /// <summary>Text appended to the command history between two snapshots (the window has a max size, so match on a tail).</summary>
        public static string HistoryDelta(string before, string after)
        {
            if (after.StartsWith(before, StringComparison.Ordinal)) return after.Substring(before.Length).Trim();
            var tail = before.Length > 400 ? before.Substring(before.Length - 400) : before;
            var at = tail.Length > 0 ? after.LastIndexOf(tail, StringComparison.Ordinal) : -1;
            if (at >= 0) return after.Substring(at + tail.Length).Trim();
            return after.Length > 4000 ? after.Substring(after.Length - 4000) : after;
        }
    }
}
