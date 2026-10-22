using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;
using Rhino;

namespace OpenRhinoMcp.Handlers
{
    /// <summary>Reflection over the RhinoCommon actually loaded in this Rhino, so agents see exact signatures.</summary>
    public static class ApiHandlers
    {
        static readonly string[] AssemblyNames = { "RhinoCommon", "Rhino.UI", "Eto", "Grasshopper", "GH_IO" };
        static List<Type> _types;

        public static void Register()
        {
            Registry.Add("api.reflect", Reflect, MethodFlags.NoDoc);
        }

        static List<Type> Types()
        {
            if (_types != null) return _types;
            var list = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = asm.GetName().Name;
                if (!AssemblyNames.Contains(n)) continue;
                try { list.AddRange(asm.GetExportedTypes()); }
                catch (ReflectionTypeLoadException ex) { list.AddRange(ex.Types.Where(t => t != null && t.IsPublic)); }
                catch { }
            }
            _types = list;
            return list;
        }

        static JToken Reflect(RhinoDoc doc, JObject p)
        {
            var query = J.ReqStr(p, "query").Trim();
            var member = J.Str(p, "member");
            var limit = Math.Max(1, Math.Min(200, J.Int(p, "limit", 25)));

            var matches = Types()
                .Where(t => t.FullName != null && t.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => string.Equals(t.FullName, query, StringComparison.OrdinalIgnoreCase) ? 0
                            : string.Equals(t.Name, query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(t => t.FullName.Length)
                .Take(limit)
                .ToList();

            bool exact = matches.Count > 0 && (string.Equals(matches[0].FullName, query, StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(matches[0].Name, query, StringComparison.OrdinalIgnoreCase));
            var types = new JArray();
            for (int i = 0; i < matches.Count; i++)
            {
                var t = matches[i];
                var j = new JObject
                {
                    ["full_name"] = t.FullName,
                    ["kind"] = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static class" : "class",
                    ["base"] = t.BaseType != null && t.BaseType != typeof(object) && t.BaseType != typeof(ValueType) ? Friendly(t.BaseType) : null
                };
                if (matches.Count == 1 || member != null || (i == 0 && exact))
                    j["members"] = new JArray(Members(t, member).Take(400));
                types.Add(j);
            }
            return new JObject { ["count"] = types.Count, ["types"] = types };
        }

        static IEnumerable<string> Members(Type t, string filter)
        {
            const BindingFlags bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            bool Match(string n) => filter == null || n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            string Obs(MemberInfo m) => m.GetCustomAttribute<ObsoleteAttribute>() != null ? "[Obsolete] " : "";

            if (t.IsEnum)
            {
                foreach (var n in Enum.GetNames(t))
                    if (Match(n)) yield return n;
                yield break;
            }

            foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                if (Match(t.Name)) yield return $"{Obs(c)}new {t.Name}({Params(c)})";

            foreach (var pr in t.GetProperties(bf).OrderBy(x => x.Name))
            {
                if (!Match(pr.Name)) continue;
                var getter = pr.GetGetMethod();
                var setter = pr.GetSetMethod();
                var stat = (getter ?? setter)?.IsStatic == true ? "static " : "";
                var idx = pr.GetIndexParameters();
                var name = idx.Length > 0 ? $"this[{string.Join(", ", idx.Select(x => Friendly(x.ParameterType) + " " + x.Name))}]" : pr.Name;
                yield return $"{Obs(pr)}{stat}{Friendly(pr.PropertyType)} {name} {{ {(getter != null ? "get; " : "")}{(setter != null ? "set; " : "")}}}";
            }

            foreach (var m in t.GetMethods(bf).Where(m => !m.IsSpecialName).OrderBy(x => x.Name))
            {
                if (!Match(m.Name)) continue;
                var gen = m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";
                yield return $"{Obs(m)}{(m.IsStatic ? "static " : "")}{Friendly(m.ReturnType)} {m.Name}{gen}({Params(m)})";
            }

            foreach (var f in t.GetFields(bf).OrderBy(x => x.Name))
                if (Match(f.Name)) yield return $"{Obs(f)}{(f.IsStatic ? "static " : "")}{(f.IsLiteral ? "const " : "")}{Friendly(f.FieldType)} {f.Name}";

            foreach (var e in t.GetEvents(bf).OrderBy(x => x.Name))
                if (Match(e.Name)) yield return $"{Obs(e)}event {Friendly(e.EventHandlerType)} {e.Name}";
        }

        static string Params(MethodBase m) => string.Join(", ", m.GetParameters().Select(x =>
        {
            var type = x.ParameterType;
            var prefix = "";
            if (type.IsByRef) { prefix = x.IsOut ? "out " : "ref "; type = type.GetElementType(); }
            var def = x.HasDefaultValue ? " = " + (x.DefaultValue?.ToString() ?? "null") : "";
            return prefix + Friendly(type) + " " + x.Name + def;
        }));

        static string Friendly(Type t)
        {
            if (t == null) return "?";
            if (t == typeof(void)) return "void";
            if (t == typeof(int)) return "int";
            if (t == typeof(double)) return "double";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(object)) return "object";
            if (t == typeof(float)) return "float";
            if (t == typeof(uint)) return "uint";
            if (t == typeof(long)) return "long";
            if (t.IsArray) return Friendly(t.GetElementType()) + "[]";
            if (t.IsGenericType)
            {
                var name = t.Name;
                var tick = name.IndexOf('`');
                if (tick > 0) name = name.Substring(0, tick);
                if (name == "Nullable") return Friendly(t.GetGenericArguments()[0]) + "?";
                return name + "<" + string.Join(", ", t.GetGenericArguments().Select(Friendly)) + ">";
            }
            return t.IsNested ? Friendly(t.DeclaringType) + "." + t.Name : t.Name;
        }
    }
}
