using System.Linq;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;
using Rhino;

namespace OpenRhinoMcp.Handlers
{
    /// <summary>Object and document user text — the tag/attribute layer of a model.</summary>
    public static class UserTextHandlers
    {
        public static void Register()
        {
            Registry.Add("usertext.get", Get);
            Registry.Add("usertext.set", Set, MethodFlags.Edit);
        }

        static JObject Pick(JObject all, JArray keys)
        {
            if (keys == null || keys.Count == 0) return all;
            var o = new JObject();
            foreach (var k in keys.Select(x => x.ToString()))
                if (all[k] != null) o[k] = all[k];
            return o;
        }

        static JToken Get(RhinoDoc doc, JObject p)
        {
            var keys = J.Arr(p, "keys");
            var result = new JObject();
            var ids = J.Ids(p, "ids", false);
            if (ids != null)
            {
                var arr = new JArray();
                foreach (var o in DocUtil.FindAll(doc, ids))
                    arr.Add(new JObject { ["id"] = o.Id.ToString(), ["user_text"] = Pick(Summaries.UserText(o.Attributes), keys) });
                result["objects"] = arr;
            }
            if (J.Bool(p, "document", false) || ids == null)
                result["document"] = Pick(DocHandlers.DocUserText(doc), keys);
            return result;
        }

        static JToken Set(RhinoDoc doc, JObject p)
        {
            var values = J.Obj(p, "values") ?? throw J.Bad("'values' is required, e.g. {\"circuit\": \"11\"}.");
            var replace = (J.Str(p, "mode", "merge") ?? "merge").ToLowerInvariant() == "replace";
            var ids = J.Ids(p, "ids", false);
            bool document = J.Bool(p, "document", false);
            if (ids == null && !document) throw J.Bad("Pass 'ids' and/or document: true.");

            if (ids != null)
            {
                foreach (var o in DocUtil.FindAll(doc, ids))
                {
                    var att = o.Attributes.Duplicate();
                    if (replace) att.DeleteAllUserStrings();
                    foreach (var kv in values)
                    {
                        if (kv.Value == null || kv.Value.Type == JTokenType.Null) att.DeleteUserString(kv.Key);
                        else att.SetUserString(kv.Key, kv.Value.Type == JTokenType.String ? (string)kv.Value : kv.Value.ToString());
                    }
                    doc.Objects.ModifyAttributes(o.Id, att, true);
                }
            }

            if (document)
            {
                if (replace)
                {
                    var existing = DocHandlers.DocUserText(doc).Properties().Select(x => x.Name).ToList();
                    foreach (var k in existing) doc.Strings.Delete(k);
                }
                foreach (var kv in values)
                {
                    if (kv.Value == null || kv.Value.Type == JTokenType.Null) doc.Strings.Delete(kv.Key);
                    else doc.Strings.SetString(kv.Key, kv.Value.Type == JTokenType.String ? (string)kv.Value : kv.Value.ToString());
                }
            }
            return new JObject { ["success"] = true };
        }
    }
}
