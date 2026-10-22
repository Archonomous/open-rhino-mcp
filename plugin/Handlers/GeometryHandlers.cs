using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace OpenRhinoMcp.Handlers
{
    public static class GeometryHandlers
    {
        public static void Register()
        {
            Registry.Add("geometry.create", Create, MethodFlags.Edit);
            Registry.Add("geometry.op", Op, MethodFlags.Edit);
            Registry.Add("geometry.boolean", Boolean, MethodFlags.Edit);
            Registry.Add("geometry.analyze", Analyze);
        }

        // ---------------------------------------------------------------- create

        static JToken Create(RhinoDoc doc, JObject p)
        {
            var items = J.Arr(p, "items") ?? throw J.Bad("'items' is required.");
            if (items.Count > 500) throw J.Bad("At most 500 items per call.");
            var ids = new JArray();
            var errors = new JArray();
            for (int i = 0; i < items.Count; i++)
            {
                try
                {
                    var item = items[i] as JObject ?? throw J.Bad("Each item must be an object.");
                    var geom = Build(doc, item);
                    var att = DocUtil.NewAttributes(doc, J.Obj(item, "attributes"));
                    var id = doc.Objects.Add(geom, att);
                    if (id == Guid.Empty) throw J.Failed("Rhino rejected the geometry (invalid or degenerate).");
                    ids.Add(id.ToString());
                }
                catch (Exception ex)
                {
                    ids.Add(JValue.CreateNull());
                    errors.Add(new JObject { ["index"] = i, ["message"] = ex.Message });
                }
            }
            return new JObject { ["ids"] = ids, ["errors"] = errors };
        }

        static Plane PlaneAt(RhinoDoc doc, JObject item, string originKey)
        {
            var pl = J.ParsePlane(doc, J.Get(item, "plane"));
            if (J.Has(item, originKey)) pl.Origin = J.ReqPt(item, originKey);
            return pl;
        }

        static GeometryBase Build(RhinoDoc doc, JObject it)
        {
            var type = J.ReqStr(it, "type").ToLowerInvariant();
            switch (type)
            {
                case "point":
                    return new Point(J.ReqPt(it, "point"));

                case "line":
                    return new LineCurve(J.ReqPt(it, "start"), J.ReqPt(it, "end"));

                case "polyline":
                {
                    var pts = J.Pts(it, "points", 2);
                    if (J.Bool(it, "closed", false) && pts[0].DistanceTo(pts[pts.Count - 1]) > doc.ModelAbsoluteTolerance) pts.Add(pts[0]);
                    return new PolylineCurve(pts);
                }

                case "rectangle":
                {
                    var pl = PlaneAt(doc, it, "corner");
                    var rect = new Rectangle3d(pl, J.ReqNum(it, "width"), J.ReqNum(it, "height"));
                    return new PolylineCurve(rect.ToPolyline());
                }

                case "circle":
                    return new ArcCurve(new Circle(PlaneAt(doc, it, "center"), J.ReqNum(it, "radius")));

                case "arc":
                {
                    if (J.Has(it, "mid"))
                    {
                        var arc = new Arc(J.ReqPt(it, "start"), J.ReqPt(it, "mid"), J.ReqPt(it, "end"));
                        if (!arc.IsValid) throw J.Bad("Arc points are collinear.");
                        return new ArcCurve(arc);
                    }
                    var circle = new Circle(PlaneAt(doc, it, "center"), J.ReqNum(it, "radius"));
                    var a0 = RhinoMath.ToRadians(J.Num(it, "start_angle_deg", 0));
                    var a1 = RhinoMath.ToRadians(J.ReqNum(it, "end_angle_deg"));
                    return new ArcCurve(new Arc(circle, new Interval(a0, a1)));
                }

                case "ellipse":
                    return new Ellipse(PlaneAt(doc, it, "center"), J.ReqNum(it, "radius_x"), J.ReqNum(it, "radius_y")).ToNurbsCurve();

                case "polygon":
                {
                    var pl = PlaneAt(doc, it, "center");
                    var sides = J.Int(it, "sides", 6);
                    if (sides < 3) throw J.Bad("A polygon needs at least 3 sides.");
                    var r = J.ReqNum(it, "radius");
                    if (J.Bool(it, "circumscribed", false)) r /= Math.Cos(Math.PI / sides);
                    var pts = new List<Point3d>();
                    for (int k = 0; k <= sides; k++)
                    {
                        var ang = 2 * Math.PI * (k % sides) / sides;
                        pts.Add(pl.PointAt(r * Math.Cos(ang), r * Math.Sin(ang)));
                    }
                    return new PolylineCurve(pts);
                }

                case "curve":
                {
                    var pts = J.Pts(it, "points", 2);
                    var degree = J.Int(it, "degree", 3);
                    var closed = J.Bool(it, "closed", false);
                    Curve c;
                    if (J.Bool(it, "interpolate", true))
                    {
                        if (closed)
                        {
                            if (pts[0].DistanceTo(pts[pts.Count - 1]) > doc.ModelAbsoluteTolerance) pts.Add(pts[0]);
                            c = Curve.CreateInterpolatedCurve(pts, degree, CurveKnotStyle.ChordPeriodic);
                        }
                        else c = Curve.CreateInterpolatedCurve(pts, degree);
                    }
                    else c = NurbsCurve.Create(closed, Math.Min(degree, pts.Count - 1), pts);
                    return c ?? throw J.Failed("Couldn't build the curve from those points.");
                }

                case "box":
                {
                    Box box;
                    if (J.Has(it, "corner_a"))
                        box = new Box(new BoundingBox(J.ReqPt(it, "corner_a"), J.ReqPt(it, "corner_b")));
                    else
                    {
                        var pl = PlaneAt(doc, it, "corner");
                        var size = J.Get(it, "size") as JArray ?? throw J.Bad("Box needs corner_a/corner_b or size [x,y,z].");
                        box = new Box(pl, new Interval(0, size[0].Value<double>()), new Interval(0, size[1].Value<double>()), new Interval(0, size[2].Value<double>()));
                    }
                    if (!box.IsValid) throw J.Bad("Box is degenerate (zero size in some direction).");
                    return Brep.CreateFromBox(box);
                }

                case "sphere":
                    return new Sphere(J.ReqPt(it, "center"), J.ReqNum(it, "radius")).ToBrep();

                case "cylinder":
                {
                    var cap = J.Bool(it, "cap", true);
                    var circle = new Circle(PlaneAt(doc, it, "center"), J.ReqNum(it, "radius"));
                    return new Cylinder(circle, J.ReqNum(it, "height")).ToBrep(cap, cap);
                }

                case "cone":
                {
                    // 'center' is the base center; the apex sits 'height' along the plane normal.
                    var basePl = PlaneAt(doc, it, "center");
                    var h = J.ReqNum(it, "height");
                    var apex = basePl.Origin + basePl.ZAxis * h;
                    var conePl = new Plane(apex, -basePl.ZAxis);
                    return new Cone(conePl, h, J.ReqNum(it, "radius")).ToBrep(J.Bool(it, "cap", true));
                }

                case "plane_surface":
                {
                    var pl = PlaneAt(doc, it, "corner");
                    return new PlaneSurface(pl, new Interval(0, J.ReqNum(it, "width")), new Interval(0, J.ReqNum(it, "height")));
                }

                case "text":
                case "dot":
                case "leader":
                case "dimension":
                    throw J.Bad("Use rhino_create_annotations for text, leaders, dimensions and dots.");
            }
            throw J.Bad($"Unknown geometry type '{type}'.");
        }

        // ---------------------------------------------------------------- helpers

        static Curve CurveOf(RhinoObject o) => o.Geometry as Curve ?? throw J.Bad($"{o.Id} is not a curve.");

        static Brep BrepOf(RhinoObject o) =>
            Brep.TryConvertBrep(o.Geometry) ?? throw J.Bad($"{o.Id} is not a surface, polysurface or extrusion.");

        static List<RhinoObject> Objs(RhinoDoc doc, JObject p, string key = "ids") => DocUtil.FindAll(doc, J.Ids(p, key));

        static ObjectAttributes AttrsFor(RhinoDoc doc, JObject p, RhinoObject source)
        {
            var att = source != null ? source.Attributes.Duplicate() : DocUtil.ModelAttributes(doc);
            if (source != null)
            {
                // New geometry shouldn't inherit identity-like data from its source.
                att.Name = "";
            }
            DocUtil.ApplyAttributes(doc, att, J.Obj(p, "attributes"));
            return att;
        }

        static JArray AddAll(RhinoDoc doc, IEnumerable<GeometryBase> geoms, ObjectAttributes att)
        {
            var ids = new JArray();
            foreach (var g in geoms)
            {
                if (g == null) continue;
                var id = doc.Objects.Add(g, att);
                if (id != Guid.Empty) ids.Add(id.ToString());
            }
            return ids;
        }

        static void DeleteInputs(RhinoDoc doc, IEnumerable<RhinoObject> objs)
        {
            foreach (var o in objs) doc.Objects.Delete(o.Id, true);
        }

        static RpcException Empty(string op, string hint) => J.Failed($"{op} produced no result. {hint}");

        // ---------------------------------------------------------------- op

        static JToken Op(RhinoDoc doc, JObject p)
        {
            var op = J.ReqStr(p, "op").ToLowerInvariant();
            var tol = DocUtil.Tol(doc);
            var angTol = DocUtil.AngTolRad(doc);
            List<RhinoObject> inputs;
            JArray ids;

            switch (op)
            {
                case "extrude":
                {
                    inputs = Objs(doc, p);
                    var geoms = new List<GeometryBase>();
                    var cap = J.Bool(p, "cap", true);
                    foreach (var o in inputs)
                    {
                        var c = CurveOf(o);
                        if (J.Has(p, "direction"))
                        {
                            var srf = Surface.CreateExtrusion(c, J.Vec(p["direction"], "direction"));
                            var b = srf?.ToBrep();
                            if (b != null && cap && c.IsClosed) b = b.CapPlanarHoles(tol) ?? b;
                            geoms.Add(b);
                        }
                        else
                        {
                            var h = J.ReqNum(p, "height");
                            var ext = Extrusion.Create(c, h, cap && c.IsClosed);
                            if (ext == null)
                            {
                                var srf = Surface.CreateExtrusion(c, Vector3d.ZAxis * h);
                                var b = srf?.ToBrep();
                                if (b != null && cap && c.IsClosed) b = b.CapPlanarHoles(tol) ?? b;
                                geoms.Add(b);
                            }
                            else geoms.Add(ext);
                        }
                    }
                    ids = AddAll(doc, geoms, AttrsFor(doc, p, inputs[0]));
                    if (ids.Count == 0) throw Empty("Extrude", "Check the curves are valid; 'height' needs planar curves, otherwise pass 'direction'.");
                    break;
                }

                case "loft":
                {
                    inputs = Objs(doc, p);
                    if (inputs.Count < 2) throw J.Bad("Loft needs at least 2 curves, in order.");
                    var lt = LoftType.Normal;
                    switch ((J.Str(p, "loft_type", "normal") ?? "normal").ToLowerInvariant())
                    {
                        case "loose": lt = LoftType.Loose; break;
                        case "tight": lt = LoftType.Tight; break;
                        case "straight": lt = LoftType.Straight; break;
                    }
                    var breps = Brep.CreateFromLoft(inputs.Select(CurveOf), Point3d.Unset, Point3d.Unset, lt, J.Bool(p, "closed", false));
                    ids = AddAll(doc, breps ?? new Brep[0], AttrsFor(doc, p, inputs[0]));
                    if (ids.Count == 0) throw Empty("Loft", "Curves may point in opposite directions or have mismatched seams; try _Loft via rhino_run_command.");
                    break;
                }

                case "revolve":
                {
                    inputs = Objs(doc, p);
                    var axis = new Line(J.ReqPt(p, "axis_start"), J.ReqPt(p, "axis_end"));
                    var a0 = RhinoMath.ToRadians(J.Num(p, "start_deg", 0));
                    var a1 = RhinoMath.ToRadians(J.Num(p, "end_deg", 360));
                    var geoms = inputs.Select(o =>
                    {
                        var rev = RevSurface.Create(CurveOf(o), axis, a0, a1);
                        return rev == null ? null : (GeometryBase)Brep.CreateFromRevSurface(rev, false, false);
                    }).ToList();
                    ids = AddAll(doc, geoms, AttrsFor(doc, p, inputs[0]));
                    if (ids.Count == 0) throw Empty("Revolve", "Check the axis isn't crossing the profile.");
                    break;
                }

                case "sweep1":
                {
                    var rail = CurveOf(DocUtil.Find(doc, J.ToId(J.Get(p, "rail_id") ?? throw J.Bad("'rail_id' is required."))));
                    inputs = Objs(doc, p, "profile_ids");
                    var breps = Brep.CreateFromSweep(rail, inputs.Select(CurveOf), J.Bool(p, "closed", false), tol);
                    ids = AddAll(doc, breps ?? new Brep[0], AttrsFor(doc, p, inputs[0]));
                    if (ids.Count == 0) throw Empty("Sweep1", "Profiles should touch or sit near the rail.");
                    break;
                }

                case "sweep2":
                {
                    var rails = J.Ids(p, "rail_ids");
                    if (rails.Count != 2) throw J.Bad("sweep2 needs exactly 2 rail_ids.");
                    var r0 = CurveOf(DocUtil.Find(doc, rails[0]));
                    var r1 = CurveOf(DocUtil.Find(doc, rails[1]));
                    inputs = Objs(doc, p, "profile_ids");
                    var breps = Brep.CreateFromSweep(r0, r1, inputs.Select(CurveOf), J.Bool(p, "closed", false), tol);
                    ids = AddAll(doc, breps ?? new Brep[0], AttrsFor(doc, p, inputs[0]));
                    if (ids.Count == 0) throw Empty("Sweep2", "Profiles should touch both rails.");
                    break;
                }

                case "pipe":
                {
                    inputs = Objs(doc, p);
                    var capMode = PipeCapMode.Flat;
                    var capStr = J.Get(p, "cap");
                    if (capStr != null)
                    {
                        switch (capStr.ToString().ToLowerInvariant())
                        {
                            case "none": case "false": capMode = PipeCapMode.None; break;
                            case "round": capMode = PipeCapMode.Round; break;
                            default: capMode = PipeCapMode.Flat; break;
                        }
                    }
                    var r = J.ReqNum(p, "radius");
                    var geoms = new List<GeometryBase>();
                    foreach (var o in inputs)
                        geoms.AddRange(Brep.CreatePipe(CurveOf(o), r, false, capMode, true, tol, angTol) ?? new Brep[0]);
                    ids = AddAll(doc, geoms, AttrsFor(doc, p, inputs[0]));
                    if (ids.Count == 0) throw Empty("Pipe", "Radius may be too large for the curve's tightest bend.");
                    break;
                }

                case "offset_curve":
                {
                    inputs = Objs(doc, p);
                    var dist = J.ReqNum(p, "distance");
                    var corner = CurveOffsetCornerStyle.Sharp;
                    switch ((J.Str(p, "corner", "sharp") ?? "sharp").ToLowerInvariant())
                    {
                        case "round": corner = CurveOffsetCornerStyle.Round; break;
                        case "smooth": corner = CurveOffsetCornerStyle.Smooth; break;
                        case "chamfer": corner = CurveOffsetCornerStyle.Chamfer; break;
                    }
                    var geoms = new List<GeometryBase>();
                    foreach (var o in inputs)
                    {
                        var c = CurveOf(o);
                        Plane pl;
                        if (J.Has(p, "plane")) pl = J.ParsePlane(doc, p["plane"]);
                        else if (!c.TryGetPlane(out pl, tol)) pl = Plane.WorldXY;
                        geoms.AddRange(c.Offset(pl, dist, tol, corner) ?? new Curve[0]);
                    }
                    ids = AddAll(doc, geoms, AttrsFor(doc, p, inputs[0]));
                    if (ids.Count == 0) throw Empty("Offset", "Distance may be larger than the curve allows; positive/negative flips side relative to the plane normal.");
                    break;
                }

                case "join":
                {
                    inputs = Objs(doc, p);
                    var curves = inputs.Where(o => o.Geometry is Curve).ToList();
                    var breps = inputs.Where(o => !(o.Geometry is Curve)).ToList();
                    var geoms = new List<GeometryBase>();
                    if (curves.Count > 0) geoms.AddRange(Curve.JoinCurves(curves.Select(CurveOf), tol) ?? new Curve[0]);
                    if (breps.Count > 0) geoms.AddRange(Brep.JoinBreps(breps.Select(BrepOf), tol) ?? new Brep[0]);
                    ids = AddAll(doc, geoms, AttrsFor(doc, p, inputs[0]));
                    if (ids.Count == 0) throw Empty("Join", "Nothing could be joined within tolerance.");
                    if (J.Bool(p, "delete_input", true)) DeleteInputs(doc, inputs);
                    return new JObject { ["ids"] = ids };
                }

                case "explode":
                {
                    inputs = Objs(doc, p);
                    var all = new JArray();
                    foreach (var o in inputs)
                    {
                        var att = AttrsFor(doc, p, o);
                        IEnumerable<GeometryBase> parts;
                        if (o.Geometry is Curve c)
                            parts = c.DuplicateSegments();
                        else
                        {
                            var b = BrepOf(o);
                            parts = Enumerable.Range(0, b.Faces.Count).Select(i => (GeometryBase)b.Faces[i].DuplicateFace(false));
                        }
                        foreach (var id in AddAll(doc, parts, att)) all.Add(id);
                    }
                    if (J.Bool(p, "delete_input", true)) DeleteInputs(doc, inputs);
                    return new JObject { ["ids"] = all };
                }

                case "cap":
                {
                    inputs = Objs(doc, p);
                    ids = new JArray();
                    foreach (var o in inputs)
                    {
                        var capped = BrepOf(o).CapPlanarHoles(tol);
                        if (capped == null) continue;
                        if (doc.Objects.Replace(o.Id, capped)) ids.Add(o.Id.ToString());
                    }
                    if (ids.Count == 0) throw Empty("Cap", "Only planar open edges can be capped.");
                    return new JObject { ["ids"] = ids };
                }

                case "fillet_curves":
                {
                    inputs = Objs(doc, p);
                    if (inputs.Count != 2) throw J.Bad("fillet_curves needs exactly 2 ids.");
                    Curve c0 = CurveOf(inputs[0]), c1 = CurveOf(inputs[1]);
                    var p0 = NearEnd(c0, c1);
                    var p1 = NearEnd(c1, c0);
                    var res = Curve.CreateFilletCurves(c0, p0, c1, p1, J.ReqNum(p, "radius"), true, true, true, tol, angTol);
                    ids = AddAll(doc, res ?? new Curve[0], AttrsFor(doc, p, inputs[0]));
                    if (ids.Count == 0) throw Empty("Fillet", "Radius may be too large for the segments.");
                    if (J.Bool(p, "delete_input", true)) DeleteInputs(doc, inputs);
                    return new JObject { ["ids"] = ids };
                }

                case "rebuild_curve":
                {
                    inputs = Objs(doc, p);
                    var count = J.Int(p, "point_count", 10);
                    var deg = J.Int(p, "degree", 3);
                    ids = new JArray();
                    foreach (var o in inputs)
                    {
                        var nc = CurveOf(o).Rebuild(count, deg, true);
                        if (nc != null && doc.Objects.Replace(o.Id, nc)) ids.Add(o.Id.ToString());
                    }
                    return new JObject { ["ids"] = ids };
                }

                case "project":
                {
                    inputs = Objs(doc, p);
                    var targets = Objs(doc, p, "target_ids").Select(BrepOf).ToList();
                    var dir = J.VecOr(p, "direction", -Vector3d.ZAxis);
                    var res = Curve.ProjectToBrep(inputs.Select(CurveOf), targets, dir, tol);
                    ids = AddAll(doc, res ?? new Curve[0], AttrsFor(doc, p, inputs[0]));
                    if (ids.Count == 0) throw Empty("Project", "Curves don't land on the targets in that direction.");
                    break;
                }

                case "split_curve":
                case "trim_curve":
                {
                    inputs = Objs(doc, p);
                    var cutters = Objs(doc, p, "cutter_ids").Select(CurveOf).ToList();
                    ids = new JArray();
                    var keep = op == "trim_curve" ? J.ReqPt(p, "keep_point") : Point3d.Unset;
                    foreach (var o in inputs)
                    {
                        var c = CurveOf(o);
                        var ts = new List<double>();
                        foreach (var cut in cutters)
                        {
                            var events = Intersection.CurveCurve(c, cut, tol, tol);
                            if (events == null) continue;
                            foreach (var ev in events) ts.Add(ev.ParameterA);
                        }
                        if (ts.Count == 0) continue;
                        var pieces = c.Split(ts.Distinct().OrderBy(t => t));
                        if (pieces == null || pieces.Length == 0) continue;
                        var att = AttrsFor(doc, p, o);
                        if (op == "trim_curve")
                        {
                            var best = pieces.OrderBy(pc => pc.ClosestPoint(keep, out double t) ? pc.PointAt(t).DistanceTo(keep) : double.MaxValue).First();
                            if (doc.Objects.Replace(o.Id, best)) ids.Add(o.Id.ToString());
                        }
                        else
                        {
                            foreach (var id in AddAll(doc, pieces, att)) ids.Add(id);
                            if (J.Bool(p, "delete_input", true)) doc.Objects.Delete(o.Id, true);
                        }
                    }
                    if (ids.Count == 0) throw Empty(op, "The cutters don't intersect the curves.");
                    return new JObject { ["ids"] = ids };
                }

                default:
                    throw J.Bad("Unknown op. Use extrude, loft, revolve, sweep1, sweep2, pipe, offset_curve, join, explode, cap, fillet_curves, rebuild_curve, project, split_curve or trim_curve.");
            }

            if (J.Bool(p, "delete_input", false)) DeleteInputs(doc, inputs);
            return new JObject { ["ids"] = ids };
        }

        static Point3d NearEnd(Curve c, Curve other)
        {
            double Dist(Point3d pt) => other.ClosestPoint(pt, out double t) ? other.PointAt(t).DistanceTo(pt) : double.MaxValue;
            return Dist(c.PointAtStart) <= Dist(c.PointAtEnd) ? c.PointAtStart : c.PointAtEnd;
        }

        // ---------------------------------------------------------------- boolean

        static JToken Boolean(RhinoDoc doc, JObject p)
        {
            var op = J.ReqStr(p, "op").ToLowerInvariant();
            var tol = DocUtil.Tol(doc);
            var a = DocUtil.FindAll(doc, J.Ids(p, "a_ids"));
            var b = J.Ids(p, "b_ids", false) is List<Guid> bIds ? DocUtil.FindAll(doc, bIds) : new List<RhinoObject>();
            var all = a.Concat(b).ToList();
            var att = AttrsFor(doc, p, a[0]);
            var results = new List<GeometryBase>();

            bool curves = all.All(o => o.Geometry is Curve);
            if (curves)
            {
                var ca = a.Select(CurveOf).ToList();
                var cb = b.Select(CurveOf).ToList();
                if (all.Any(o => !((Curve)o.Geometry).IsClosed)) throw J.Bad("Curve booleans need closed planar curves.");
                switch (op)
                {
                    case "union":
                        results.AddRange(Curve.CreateBooleanUnion(ca.Concat(cb), tol) ?? new Curve[0]);
                        break;
                    case "difference":
                        if (cb.Count == 0) throw J.Bad("difference needs b_ids (the curves to subtract).");
                        foreach (var c in ca) results.AddRange(Curve.CreateBooleanDifference(c, cb, tol) ?? new Curve[0]);
                        break;
                    case "intersection":
                        if (cb.Count == 0) throw J.Bad("intersection needs b_ids.");
                        foreach (var c in ca)
                            foreach (var d in cb)
                                results.AddRange(Curve.CreateBooleanIntersection(c, d, tol) ?? new Curve[0]);
                        break;
                    default:
                        throw J.Bad("For curves use union, difference or intersection (split_curve is in rhino_geometry_op).");
                }
            }
            else
            {
                var ba = a.Select(BrepOf).ToList();
                var bb = b.Select(BrepOf).ToList();
                switch (op)
                {
                    case "union":
                        results.AddRange(Brep.CreateBooleanUnion(ba.Concat(bb), tol) ?? new Brep[0]);
                        break;
                    case "difference":
                        if (bb.Count == 0) throw J.Bad("difference needs b_ids (what to subtract).");
                        results.AddRange(Brep.CreateBooleanDifference(ba, bb, tol) ?? new Brep[0]);
                        break;
                    case "intersection":
                        if (bb.Count == 0) throw J.Bad("intersection needs b_ids.");
                        results.AddRange(Brep.CreateBooleanIntersection(ba, bb, tol) ?? new Brep[0]);
                        break;
                    case "split":
                        if (bb.Count == 0) throw J.Bad("split needs b_ids (the cutters).");
                        foreach (var brep in ba)
                        {
                            var pieces = new List<Brep> { brep };
                            foreach (var cutter in bb)
                            {
                                var next = new List<Brep>();
                                foreach (var piece in pieces)
                                {
                                    var split = piece.Split(cutter, tol);
                                    if (split != null && split.Length > 0) next.AddRange(split); else next.Add(piece);
                                }
                                pieces = next;
                            }
                            results.AddRange(pieces);
                        }
                        break;
                    default:
                        throw J.Bad("op must be union, difference, intersection or split.");
                }
            }

            if (results.Count == 0)
                throw J.Failed($"Boolean {op} failed. Inputs must be closed solids (or closed planar curves) that actually overlap; " +
                               "check naked edges with rhino_get_objects and that normals point outward.");

            var ids = AddAll(doc, results, att);
            if (J.Bool(p, "delete_input", true))
                DeleteInputs(doc, op == "split" ? a : all);
            return new JObject { ["ids"] = ids };
        }

        // ---------------------------------------------------------------- analyze

        static JToken Analyze(RhinoDoc doc, JObject p)
        {
            var op = (J.Str(p, "op", "measure") ?? "measure").ToLowerInvariant();
            var tol = DocUtil.Tol(doc);
            switch (op)
            {
                case "measure":
                {
                    var items = new JArray();
                    foreach (var o in Objs(doc, p))
                    {
                        var g = o.Geometry;
                        var j = new JObject { ["id"] = o.Id.ToString(), ["bbox"] = J.ToJ(g.GetBoundingBox(true)) };
                        if (g is Curve c)
                        {
                            j["length"] = J.N(c.GetLength());
                            j["is_closed"] = c.IsClosed;
                            if (c.IsClosed && c.IsPlanar(tol))
                            {
                                var amp = AreaMassProperties.Compute(c);
                                if (amp != null) { j["area"] = J.N(amp.Area); j["centroid"] = J.ToJ(amp.Centroid); }
                            }
                        }
                        else if (g is Mesh m)
                        {
                            var amp = AreaMassProperties.Compute(m);
                            if (amp != null) { j["area"] = J.N(amp.Area); j["centroid"] = J.ToJ(amp.Centroid); }
                            j["is_closed"] = m.IsClosed;
                            if (m.IsClosed)
                            {
                                var vmp = VolumeMassProperties.Compute(m);
                                if (vmp != null) j["volume"] = J.N(vmp.Volume);
                            }
                        }
                        else if (Brep.TryConvertBrep(g) is Brep b)
                        {
                            var amp = AreaMassProperties.Compute(b);
                            if (amp != null) { j["area"] = J.N(amp.Area); j["centroid"] = J.ToJ(amp.Centroid); }
                            j["is_solid"] = b.IsSolid;
                            if (b.IsSolid)
                            {
                                var vmp = VolumeMassProperties.Compute(b);
                                if (vmp != null) { j["volume"] = J.N(vmp.Volume); j["centroid"] = J.ToJ(vmp.Centroid); }
                            }
                        }
                        items.Add(j);
                    }
                    return new JObject { ["units"] = doc.ModelUnitSystem.ToString(), ["items"] = items };
                }

                case "intersect":
                {
                    var objs = Objs(doc, p);
                    if (objs.Count != 2) throw J.Bad("intersect needs exactly 2 ids.");
                    GeometryBase g0 = objs[0].Geometry, g1 = objs[1].Geometry;
                    var points = new JArray();
                    var curves = new JArray();
                    int overlaps = 0;

                    if (g0 is Curve c0 && g1 is Curve c1)
                    {
                        var ev = Intersection.CurveCurve(c0, c1, tol, tol);
                        if (ev != null)
                            foreach (var e in ev) { if (e.IsOverlap) overlaps++; points.Add(J.ToJ(e.PointA)); }
                    }
                    else if (g0 is Curve || g1 is Curve)
                    {
                        var c = (g0 as Curve) ?? (Curve)g1;
                        var b = BrepOf(g0 is Curve ? objs[1] : objs[0]);
                        Intersection.CurveBrep(c, b, tol, out Curve[] ov, out Point3d[] pts);
                        foreach (var pt in pts ?? new Point3d[0]) points.Add(J.ToJ(pt));
                        foreach (var oc in ov ?? new Curve[0]) { overlaps++; curves.Add(Sample(oc)); }
                    }
                    else
                    {
                        Intersection.BrepBrep(BrepOf(objs[0]), BrepOf(objs[1]), tol, out Curve[] ic, out Point3d[] pts);
                        foreach (var pt in pts ?? new Point3d[0]) points.Add(J.ToJ(pt));
                        foreach (var cc in ic ?? new Curve[0]) curves.Add(Sample(cc));
                    }
                    return new JObject { ["points"] = points, ["curves"] = curves, ["overlaps"] = overlaps };
                }

                case "closest_point":
                {
                    var o = Objs(doc, p).First();
                    var pt = J.ReqPt(p, "point");
                    Point3d cp;
                    switch (o.Geometry)
                    {
                        case Curve c:
                            if (!c.ClosestPoint(pt, out double t)) throw J.Failed("Closest point failed.");
                            cp = c.PointAt(t);
                            break;
                        case Mesh m:
                            cp = m.ClosestPoint(pt);
                            break;
                        case Point pg:
                            cp = pg.Location;
                            break;
                        default:
                            cp = BrepOf(o).ClosestPoint(pt);
                            break;
                    }
                    return new JObject { ["point"] = J.ToJ(cp), ["distance"] = J.N(cp.DistanceTo(pt)) };
                }

                case "distance":
                    return new JObject { ["distance"] = J.N(J.ReqPt(p, "point").DistanceTo(J.ReqPt(p, "point_b"))) };
            }
            throw J.Bad("op must be measure, intersect, closest_point or distance.");
        }

        static JObject Sample(Curve c)
        {
            var pts = new List<Point3d>();
            if (c.TryGetPolyline(out Polyline pl)) pts.AddRange(pl);
            else
            {
                var ts = c.DivideByCount(48, true);
                if (ts != null) pts.AddRange(ts.Select(c.PointAt));
            }
            return new JObject { ["length"] = J.N(c.GetLength()), ["closed"] = c.IsClosed, ["points"] = J.ToJ(pts, 200) };
        }
    }
}
