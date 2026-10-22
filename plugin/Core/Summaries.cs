using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace OpenRhinoMcp.Core
{
    /// <summary>Converts Rhino objects into the ObjectSummary / detail JSON shapes.</summary>
    public static class Summaries
    {
        public static string TypeName(RhinoObject o)
        {
            var g = o.Geometry;
            switch (o.ObjectType)
            {
                case ObjectType.Curve: return "curve";
                case ObjectType.Brep:
                case ObjectType.Surface: return "brep";
                case ObjectType.Extrusion: return "extrusion";
                case ObjectType.Mesh: return "mesh";
                case ObjectType.SubD: return "subd";
                case ObjectType.Point: return "point";
                case ObjectType.PointSet: return "pointcloud";
                case ObjectType.TextDot: return "textdot";
                case ObjectType.Hatch: return "hatch";
                case ObjectType.InstanceReference: return "block_instance";
                case ObjectType.Detail: return "detail";
                case ObjectType.Light: return "light";
                case ObjectType.Annotation:
                    if (g is TextEntity) return "text";
                    if (g is Leader) return "leader";
                    if (g is Dimension) return "dimension";
                    return "annotation";
            }
            return "other";
        }

        public static string TextOf(GeometryBase g)
        {
            switch (g)
            {
                case TextEntity te: return te.PlainText;
                case Leader ld: return ld.PlainText;
                case Dimension d: return d.PlainText;
                case TextDot td: return td.Text;
            }
            return null;
        }

        public static JObject UserText(ObjectAttributes a)
        {
            var j = new JObject();
            var nv = a.GetUserStrings();
            if (nv == null) return j;
            foreach (string key in nv.AllKeys)
                if (key != null) j[key] = nv[key];
            return j;
        }

        static string ColorSource(ObjectColorSource s)
        {
            switch (s)
            {
                case ObjectColorSource.ColorFromLayer: return "layer";
                case ObjectColorSource.ColorFromObject: return "object";
                case ObjectColorSource.ColorFromMaterial: return "material";
                case ObjectColorSource.ColorFromParent: return "parent";
            }
            return s.ToString();
        }

        public static JObject Summary(RhinoDoc doc, RhinoObject o)
        {
            var a = o.Attributes;
            var g = o.Geometry;
            var j = new JObject
            {
                ["id"] = o.Id.ToString(),
                ["type"] = TypeName(o),
                ["geometry_type"] = g?.GetType().Name,
                ["layer"] = DocUtil.LayerPath(doc, a.LayerIndex),
                ["name"] = a.Name ?? "",
                ["color"] = J.Hex(a.DrawColor(doc)),
                ["color_source"] = ColorSource(a.ColorSource),
                ["visible"] = o.Visible,
                ["locked"] = o.IsLocked,
                ["selected"] = o.IsSelected(false) > 0,
                ["bbox"] = g != null ? J.ToJ(g.GetBoundingBox(true)) : JValue.CreateNull(),
                ["user_text"] = UserText(a)
            };

            if (a.Space == ActiveSpace.PageSpace)
                j["layout"] = doc.Views.GetPageViews().FirstOrDefault(v => v.MainViewport.Id == a.ViewportId)?.PageName;

            var groups = a.GetGroupList();
            if (groups != null && groups.Length > 0)
                j["groups"] = new JArray(groups.Select(i => doc.Groups.GroupName(i) ?? i.ToString()));

            if (o is InstanceObject io)
                j["block"] = io.InstanceDefinition?.Name;

            var text = TextOf(g);
            if (text != null) j["text"] = text;

            if (g is Curve c)
            {
                j["length"] = J.N(c.GetLength());
                j["closed"] = c.IsClosed;
            }
            return j;
        }

        public static JObject Detail(RhinoDoc doc, RhinoObject o)
        {
            var j = Summary(doc, o);
            var g = o.Geometry;
            var geo = new JObject();
            try
            {
                FillGeometry(doc, g, o, geo);
            }
            catch (Exception ex)
            {
                geo["error"] = ex.Message;
            }
            j["geometry"] = geo;
            return j;
        }

        static void FillGeometry(RhinoDoc doc, GeometryBase g, RhinoObject o, JObject geo)
        {
            var tol = doc.ModelAbsoluteTolerance;
            switch (g)
            {
                case Point pt:
                    geo["point"] = J.ToJ(pt.Location);
                    break;

                case PointCloud pc:
                    geo["point_count"] = pc.Count;
                    geo["points"] = J.ToJ(pc.GetPoints(), 2000);
                    break;

                case LineCurve lc:
                    geo["kind"] = "line";
                    geo["start"] = J.ToJ(lc.Line.From);
                    geo["end"] = J.ToJ(lc.Line.To);
                    geo["length"] = J.N(lc.Line.Length);
                    break;

                case Curve c:
                    FillCurve(c, geo, tol);
                    break;

                case TextEntity te:
                    geo["kind"] = "text";
                    geo["text"] = te.PlainText;
                    geo["rich_text"] = te.RichText;
                    geo["plane"] = J.ToJ(te.Plane);
                    geo["height"] = J.N(te.TextHeight);
                    geo["justification"] = te.Justification.ToString();
                    geo["dimstyle"] = doc.DimStyles.FindId(te.DimensionStyleId)?.Name;
                    try { geo["font"] = te.Font?.FaceName; } catch { }
                    break;

                case Leader ld:
                    geo["kind"] = "leader";
                    geo["text"] = ld.PlainText;
                    geo["plane"] = J.ToJ(ld.Plane);
                    geo["dimstyle"] = doc.DimStyles.FindId(ld.DimensionStyleId)?.Name;
                    break;

                case Dimension d:
                    geo["kind"] = d.GetType().Name;
                    geo["value"] = J.N(d.NumericValue);
                    geo["text"] = d.PlainText;
                    geo["plane"] = J.ToJ(d.Plane);
                    geo["dimstyle"] = doc.DimStyles.FindId(d.DimensionStyleId)?.Name;
                    break;

                case TextDot td:
                    geo["kind"] = "textdot";
                    geo["text"] = td.Text;
                    geo["point"] = J.ToJ(td.Point);
                    geo["font_height"] = td.FontHeight;
                    break;

                case InstanceReferenceGeometry irg:
                    var io = o as InstanceObject;
                    geo["kind"] = "block_instance";
                    geo["definition"] = io?.InstanceDefinition?.Name;
                    geo["insertion_point"] = J.ToJ(io?.InsertionPoint ?? Point3d.Origin);
                    geo["xform"] = XformToJ(irg.Xform);
                    break;

                case Mesh m:
                    geo["kind"] = "mesh";
                    geo["vertex_count"] = m.Vertices.Count;
                    geo["face_count"] = m.Faces.Count;
                    geo["is_closed"] = m.IsClosed;
                    break;

                case Hatch h:
                    geo["kind"] = "hatch";
                    geo["pattern"] = h.PatternIndex >= 0 && h.PatternIndex < doc.HatchPatterns.Count ? doc.HatchPatterns[h.PatternIndex].Name : null;
                    geo["scale"] = J.N(h.PatternScale);
                    geo["rotation_deg"] = J.N(RhinoMath.ToDegrees(h.PatternRotation));
                    break;

                default:
                    var brep = Brep.TryConvertBrep(g);
                    if (brep != null) FillBrep(brep, geo);
                    break;
            }
        }

        static void FillCurve(Curve c, JObject geo, double tol)
        {
            geo["length"] = J.N(c.GetLength());
            geo["closed"] = c.IsClosed;
            geo["planar"] = c.IsPlanar(tol);
            geo["start"] = J.ToJ(c.PointAtStart);
            geo["end"] = J.ToJ(c.PointAtEnd);

            if (c.TryGetPolyline(out Polyline pl))
            {
                geo["kind"] = "polyline";
                geo["vertex_count"] = pl.Count;
                geo["vertices"] = J.ToJ(pl, 2000);
                return;
            }
            if (c.TryGetCircle(out Circle circle, tol))
            {
                geo["kind"] = "circle";
                geo["center"] = J.ToJ(circle.Center);
                geo["radius"] = J.N(circle.Radius);
                geo["plane"] = J.ToJ(circle.Plane);
                return;
            }
            if (c.TryGetArc(out Arc arc, tol))
            {
                geo["kind"] = "arc";
                geo["center"] = J.ToJ(arc.Center);
                geo["radius"] = J.N(arc.Radius);
                geo["angle_deg"] = J.N(arc.AngleDegrees);
                geo["mid"] = J.ToJ(arc.MidPoint);
                geo["plane"] = J.ToJ(arc.Plane);
                return;
            }
            var nc = c.ToNurbsCurve();
            if (nc != null)
            {
                geo["kind"] = c is PolyCurve ? "polycurve" : "nurbs";
                geo["degree"] = nc.Degree;
                geo["periodic"] = nc.IsPeriodic;
                geo["rational"] = nc.IsRational;
                geo["control_point_count"] = nc.Points.Count;
                geo["knot_count"] = nc.Knots.Count;
                var pts = new List<Point3d>();
                for (int i = 0; i < nc.Points.Count && i < 2000; i++) pts.Add(nc.Points[i].Location);
                geo["control_points"] = J.ToJ(pts);
                if (c is PolyCurve pc) geo["segment_count"] = pc.SegmentCount;
            }
        }

        static void FillBrep(Brep b, JObject geo)
        {
            geo["kind"] = "brep";
            geo["face_count"] = b.Faces.Count;
            geo["edge_count"] = b.Edges.Count;
            geo["naked_edge_count"] = b.Edges.Count(e => e.Valence == EdgeAdjacency.Naked);
            geo["is_solid"] = b.IsSolid;
            var amp = AreaMassProperties.Compute(b);
            if (amp != null) geo["area"] = J.N(amp.Area);
            if (b.IsSolid)
            {
                var vmp = VolumeMassProperties.Compute(b);
                if (vmp != null)
                {
                    geo["volume"] = J.N(vmp.Volume);
                    geo["centroid"] = J.ToJ(vmp.Centroid);
                }
            }
        }

        public static JArray XformToJ(Transform x)
        {
            var rows = new JArray();
            for (int r = 0; r < 4; r++)
            {
                var row = new JArray();
                for (int c = 0; c < 4; c++) row.Add(J.N(x[r, c]));
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>Converts arbitrary .NET / IronPython values to JSON for python.run results.</summary>
        public static JToken FromNet(RhinoDoc doc, object v, int depth = 0)
        {
            if (v == null) return JValue.CreateNull();
            if (depth > 6) return v.ToString();
            switch (v)
            {
                case string s: return s;
                case bool b: return b;
                case int i: return i;
                case long l: return l;
                case short sh: return sh;
                case float f: return J.N(f);
                case double d: return J.N(d);
                case decimal m: return J.N((double)m);
                case Guid g:
                    var ro = doc?.Objects.FindId(g);
                    return ro != null ? (JToken)Summary(doc, ro) : g.ToString();
                case Point3d p: return J.ToJ(p);
                case Point3f pf: return J.ToJ(new Point3d(pf));
                case Vector3d vec: return J.ToJ(vec);
                case Point2d p2: return J.ToJ(p2);
                case Plane pl: return J.ToJ(pl);
                case BoundingBox bb: return J.ToJ(bb);
                case Line ln: return new JObject { ["start"] = J.ToJ(ln.From), ["end"] = J.ToJ(ln.To) };
                case Interval iv: return new JArray(J.N(iv.T0), J.N(iv.T1));
                case Transform xf: return XformToJ(xf);
                case System.Drawing.Color col: return J.Hex(col);
                case RhinoObject rob: return Summary(doc, rob);
                case GeometryBase gb: return new JObject { ["geometry_type"] = gb.GetType().Name, ["bbox"] = J.ToJ(gb.GetBoundingBox(true)) };
                case System.Collections.IDictionary dict:
                    var o = new JObject();
                    foreach (System.Collections.DictionaryEntry kv in dict)
                        o[kv.Key?.ToString() ?? "null"] = FromNet(doc, kv.Value, depth + 1);
                    return o;
                case System.Collections.IEnumerable en:
                    var a = new JArray();
                    foreach (var item in en)
                    {
                        if (a.Count >= 5000) { a.Add("… truncated"); break; }
                        a.Add(FromNet(doc, item, depth + 1));
                    }
                    return a;
            }
            if (v.GetType().IsEnum) return v.ToString();
            return v.ToString();
        }
    }
}
