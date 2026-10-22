using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace OpenRhinoMcp.Core
{
    public class RpcException : Exception
    {
        public string Code { get; }
        public JToken Details { get; }

        public RpcException(string code, string message, JToken details = null) : base(message)
        {
            Code = code;
            Details = details;
        }
    }

    /// <summary>Parameter parsing and value conversion shared by every handler.</summary>
    public static class J
    {
        public static RpcException Bad(string message) => new RpcException("invalid_params", message);
        public static RpcException NotFound(string message) => new RpcException("not_found", message);
        public static RpcException Failed(string message) => new RpcException("operation_failed", message);

        public static JToken Get(JObject o, string key)
        {
            if (o == null) return null;
            var t = o[key];
            return t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined ? null : t;
        }

        public static bool Has(JObject o, string key) => Get(o, key) != null;

        public static string Str(JObject o, string key, string def = null)
        {
            var t = Get(o, key);
            if (t == null) return def;
            return t.Type == JTokenType.String ? (string)t : t.ToString();
        }

        public static string ReqStr(JObject o, string key)
        {
            var s = Str(o, key);
            if (string.IsNullOrEmpty(s)) throw Bad($"'{key}' is required.");
            return s;
        }

        public static double Num(JObject o, string key, double def)
        {
            var t = Get(o, key);
            return t == null ? def : ToDouble(t, key);
        }

        public static double ReqNum(JObject o, string key)
        {
            var t = Get(o, key);
            if (t == null) throw Bad($"'{key}' is required.");
            return ToDouble(t, key);
        }

        public static double? OptNum(JObject o, string key)
        {
            var t = Get(o, key);
            return t == null ? (double?)null : ToDouble(t, key);
        }

        public static int Int(JObject o, string key, int def)
        {
            var t = Get(o, key);
            return t == null ? def : (int)Math.Round(ToDouble(t, key));
        }

        public static bool Bool(JObject o, string key, bool def)
        {
            var t = Get(o, key);
            if (t == null) return def;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            if (t.Type == JTokenType.String) return string.Equals((string)t, "true", StringComparison.OrdinalIgnoreCase);
            return ToDouble(t, key) != 0;
        }

        public static bool? OptBool(JObject o, string key)
        {
            var t = Get(o, key);
            return t == null ? (bool?)null : Bool(o, key, false);
        }

        public static JObject Obj(JObject o, string key) => Get(o, key) as JObject;
        public static JArray Arr(JObject o, string key) => Get(o, key) as JArray;

        static double ToDouble(JToken t, string key)
        {
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return t.Value<double>();
                case JTokenType.String:
                    if (double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                    break;
            }
            throw Bad($"'{key}' must be a number.");
        }

        // ---------- geometry values ----------

        public static Point3d Pt(JToken t, string what = "point")
        {
            if (t is JArray a && (a.Count == 2 || a.Count == 3))
            {
                double x = a[0].Value<double>(), y = a[1].Value<double>();
                double z = a.Count == 3 ? a[2].Value<double>() : 0;
                return new Point3d(x, y, z);
            }
            if (t is JObject o && Has(o, "x") && Has(o, "y"))
                return new Point3d(Num(o, "x", 0), Num(o, "y", 0), Num(o, "z", 0));
            throw Bad($"'{what}' must be [x, y] or [x, y, z].");
        }

        public static Point3d ReqPt(JObject o, string key)
        {
            var t = Get(o, key);
            if (t == null) throw Bad($"'{key}' is required.");
            return Pt(t, key);
        }

        public static Point3d PtOr(JObject o, string key, Point3d def)
        {
            var t = Get(o, key);
            return t == null ? def : Pt(t, key);
        }

        public static Vector3d Vec(JToken t, string what = "vector") => new Vector3d(Pt(t, what));

        public static Vector3d VecOr(JObject o, string key, Vector3d def)
        {
            var t = Get(o, key);
            return t == null ? def : Vec(t, key);
        }

        public static List<Point3d> Pts(JObject o, string key, int min = 1)
        {
            var arr = Arr(o, key);
            if (arr == null) throw Bad($"'{key}' must be a list of points.");
            var list = arr.Select(x => Pt(x, key)).ToList();
            if (list.Count < min) throw Bad($"'{key}' needs at least {min} points.");
            return list;
        }

        public static Guid ToId(JToken t)
        {
            if (t != null && Guid.TryParse(t.ToString(), out var g)) return g;
            throw Bad($"'{t}' is not a valid object id.");
        }

        public static List<Guid> Ids(JObject o, string key, bool required = true)
        {
            var t = Get(o, key);
            if (t == null)
            {
                if (required) throw Bad($"'{key}' is required.");
                return null;
            }
            if (t.Type == JTokenType.String) return new List<Guid> { ToId(t) };
            if (t is JArray a)
            {
                var ids = a.Select(ToId).ToList();
                if (required && ids.Count == 0) throw Bad($"'{key}' is empty.");
                return ids;
            }
            throw Bad($"'{key}' must be a list of ids.");
        }

        static double R(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 8);

        public static JArray ToJ(Point3d p) => new JArray(R(p.X), R(p.Y), R(p.Z));
        public static JArray ToJ(Vector3d v) => new JArray(R(v.X), R(v.Y), R(v.Z));
        public static JArray ToJ(Point2d p) => new JArray(R(p.X), R(p.Y));
        public static JToken N(double v) => new JValue(R(v));

        public static JToken ToJ(BoundingBox b)
        {
            if (!b.IsValid) return JValue.CreateNull();
            return new JObject { ["min"] = ToJ(b.Min), ["max"] = ToJ(b.Max) };
        }

        public static JObject ToJ(Plane p) => new JObject
        {
            ["origin"] = ToJ(p.Origin),
            ["x_axis"] = ToJ(p.XAxis),
            ["y_axis"] = ToJ(p.YAxis),
            ["normal"] = ToJ(p.ZAxis)
        };

        public static JArray ToJ(IEnumerable<Point3d> pts, int max = 2000)
        {
            var a = new JArray();
            foreach (var p in pts)
            {
                if (a.Count >= max) break;
                a.Add(ToJ(p));
            }
            return a;
        }

        public static string Hex(System.Drawing.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        public static System.Drawing.Color ParseColor(JToken t)
        {
            if (t is JArray a && a.Count >= 3)
                return System.Drawing.Color.FromArgb(Clamp(a[0]), Clamp(a[1]), Clamp(a[2]));
            var s = t?.ToString().Trim() ?? "";
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return System.Drawing.Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            var named = System.Drawing.Color.FromName(t?.ToString() ?? "");
            if (named.IsKnownColor) return named;
            throw Bad($"Color '{t}' must be \"#RRGGBB\" or [r, g, b].");
        }

        static int Clamp(JToken t) => Math.Max(0, Math.Min(255, (int)Math.Round(t.Value<double>())));

        /// <summary>Parses a Plane parameter. Default world XY.</summary>
        public static Plane ParsePlane(RhinoDoc doc, JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return Plane.WorldXY;
            if (t.Type == JTokenType.String)
            {
                switch (((string)t).ToLowerInvariant())
                {
                    case "world_xy": case "xy": case "top": return Plane.WorldXY;
                    case "world_yz": case "yz": case "right": return Plane.WorldYZ;
                    case "world_zx": case "zx": case "front":
                        // Rhino's Front view plane: X right, Z up.
                        return new Plane(Point3d.Origin, Vector3d.XAxis, Vector3d.ZAxis);
                    case "cplane":
                        var view = doc.Views.ActiveView;
                        if (view == null) return Plane.WorldXY;
                        return view.ActiveViewport.ConstructionPlane();
                }
                throw Bad($"Unknown plane '{t}'. Use world_xy, world_yz, world_zx, cplane or {{origin, x_axis, y_axis}}.");
            }
            if (t is JObject o)
            {
                var origin = PtOr(o, "origin", Point3d.Origin);
                if (Has(o, "x_axis") && Has(o, "y_axis"))
                {
                    var pl = new Plane(origin, Vec(o["x_axis"], "x_axis"), Vec(o["y_axis"], "y_axis"));
                    if (!pl.IsValid) throw Bad("Plane axes are parallel or zero-length.");
                    return pl;
                }
                if (Has(o, "normal"))
                {
                    var pl = new Plane(origin, Vec(o["normal"], "normal"));
                    if (!pl.IsValid) throw Bad("Plane normal is zero-length.");
                    return pl;
                }
                var p0 = Plane.WorldXY;
                p0.Origin = origin;
                return p0;
            }
            throw Bad("Invalid plane.");
        }

        /// <summary>Converts a JSON value into plain .NET values (for IronPython inputs).</summary>
        public static object ToNet(JToken t)
        {
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Null: return null;
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.Integer: return (long)t;
                case JTokenType.Float: return (double)t;
                case JTokenType.String: return (string)t;
                case JTokenType.Array: return ((JArray)t).Select(ToNet).ToList();
                case JTokenType.Object:
                    var d = new Dictionary<string, object>();
                    foreach (var kv in (JObject)t) d[kv.Key] = ToNet(kv.Value);
                    return d;
                default: return t.ToString();
            }
        }

        public static string Truncate(string s, int max)
        {
            if (s == null || s.Length <= max) return s;
            return s.Substring(0, max) + $"\n… [truncated {s.Length - max} chars]";
        }
    }
}
