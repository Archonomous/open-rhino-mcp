using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace OpenRhinoMcp.Core
{
    public delegate JToken Handler(RhinoDoc doc, JObject p);

    [Flags]
    public enum MethodFlags
    {
        ReadOnly = 0,
        /// <summary>Tracks created/deleted/modified ids and redraws.</summary>
        Mutating = 1,
        /// <summary>Wraps the call in one undo record (don't use for methods that run Rhino commands).</summary>
        UndoRecord = 2,
        /// <summary>Runs even with no active document.</summary>
        NoDoc = 4,
        /// <summary>May run while a Rhino command is active (e.g. cancelling it).</summary>
        AllowDuringCommand = 8,
        Edit = Mutating | UndoRecord,
    }

    public static class Registry
    {
        sealed class Entry
        {
            public Handler Handler;
            public MethodFlags Flags;
        }

        static readonly Dictionary<string, Entry> Map = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public static void Add(string method, Handler handler, MethodFlags flags = MethodFlags.ReadOnly)
            => Map[method] = new Entry { Handler = handler, Flags = flags };

        public static IEnumerable<string> Methods => Map.Keys.OrderBy(k => k);

        /// <summary>Runs on the UI thread.</summary>
        public static JToken Invoke(string method, JObject p)
        {
            if (!Map.TryGetValue(method, out var e))
                throw new RpcException("unknown_method", $"Unknown method '{method}'. This plug-in supports: {string.Join(", ", Methods)}.");

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null && (e.Flags & MethodFlags.NoDoc) == 0)
                throw J.Failed("Rhino has no active document.");

            if ((e.Flags & MethodFlags.Mutating) == 0)
                return e.Handler(doc, p);

            // Requests are handled concurrently, so a read can run while a command is busy,
            // but changing the document underneath a running command is never safe.
            if ((e.Flags & MethodFlags.AllowDuringCommand) == 0 && Rhino.Commands.Command.InCommand())
                throw new RpcException("rhino_busy", "A Rhino command is running. Wait for it, or call rhino_cancel, then retry.");

            using (var tracker = new ChangeTracker())
            {
                uint undo = 0;
                if ((e.Flags & MethodFlags.UndoRecord) != 0)
                    undo = doc.BeginUndoRecord("Open Rhino MCP: " + method);
                JToken result;
                try
                {
                    result = e.Handler(doc, p);
                }
                finally
                {
                    if (undo != 0) doc.EndUndoRecord(undo);
                    RhinoDoc.ActiveDoc?.Views.Redraw();
                }
                var changes = tracker.Result(RhinoDoc.ActiveDoc ?? doc);
                var obj = result as JObject ?? new JObject { ["result"] = result ?? JValue.CreateNull() };
                foreach (var kv in changes)
                    if (obj[kv.Key] == null) obj[kv.Key] = kv.Value;
                return obj;
            }
        }
    }

    /// <summary>Collects object ids added, deleted or changed while a handler runs.</summary>
    public sealed class ChangeTracker : IDisposable
    {
        readonly HashSet<Guid> _added = new HashSet<Guid>();
        readonly HashSet<Guid> _deleted = new HashSet<Guid>();
        readonly HashSet<Guid> _modified = new HashSet<Guid>();

        public ChangeTracker()
        {
            RhinoDoc.AddRhinoObject += OnAdd;
            RhinoDoc.DeleteRhinoObject += OnDelete;
            RhinoDoc.ReplaceRhinoObject += OnReplace;
            RhinoDoc.ModifyObjectAttributes += OnModify;
        }

        void OnAdd(object sender, RhinoObjectEventArgs e) => _added.Add(e.ObjectId);
        void OnDelete(object sender, RhinoObjectEventArgs e) => _deleted.Add(e.ObjectId);
        void OnReplace(object sender, RhinoReplaceObjectEventArgs e) => _modified.Add(e.ObjectId);
        void OnModify(object sender, RhinoModifyObjectAttributesEventArgs e) { if (e.RhinoObject != null) _modified.Add(e.RhinoObject.Id); }

        public JObject Result(RhinoDoc doc)
        {
            bool Exists(Guid id)
            {
                var o = doc?.Objects.FindId(id);
                // Objects inside block definitions fire add events too; they aren't model objects.
                return o != null && !o.IsInstanceDefinitionGeometry;
            }

            var created = new List<string>();
            var deleted = new List<string>();
            var modified = new HashSet<Guid>(_modified);

            foreach (var id in _added)
            {
                if (_deleted.Contains(id)) { if (Exists(id)) modified.Add(id); } // replace = delete + add with same id
                else if (Exists(id)) created.Add(id.ToString());
            }
            foreach (var id in _deleted)
                if (!_added.Contains(id) && !Exists(id)) deleted.Add(id.ToString());

            var mod = modified.Where(id => Exists(id) && !_added.Contains(id) || (_added.Contains(id) && _deleted.Contains(id) && Exists(id)))
                              .Select(id => id.ToString()).Distinct().ToList();

            return new JObject
            {
                ["created_ids"] = new JArray(created),
                ["deleted_ids"] = new JArray(deleted),
                ["modified_ids"] = new JArray(mod)
            };
        }

        public void Dispose()
        {
            RhinoDoc.AddRhinoObject -= OnAdd;
            RhinoDoc.DeleteRhinoObject -= OnDelete;
            RhinoDoc.ReplaceRhinoObject -= OnReplace;
            RhinoDoc.ModifyObjectAttributes -= OnModify;
        }
    }
}
