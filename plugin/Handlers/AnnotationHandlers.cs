using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace OpenRhinoMcp.Handlers
{
    public static class AnnotationHandlers
    {
        public static void Register()
        {
            Registry.Add("annotations.create", Create, MethodFlags.Edit);
            Registry.Add("annotations.edit", Edit, MethodFlags.Edit);
        }

        static TextJustification ParseJustification(string s)
        {
            var key = s.Replace("_", "").Replace("-", "").Replace(" ", "");
            if (Enum.TryParse(key, true, out TextJustification j)) return j;
            throw J.Bad($"Unknown justification '{s}'. Use left, center, right, bottom_left, middle_center, top_left, etc.");
        }

        static JToken Create(RhinoDoc doc, JObject p)
        {
            var items = J.Arr(p, "items") ?? throw J.Bad("'items' is required.");
            var ids = new JArray();
            var errors = new JArray();
            for (int i = 0; i < items.Count; i++)
            {
                try
                {
                    var it = items[i] as JObject ?? throw J.Bad("Each item must be an object.");
                    var id = CreateOne(doc, it);
                    if (id == Guid.Empty) throw J.Failed("Rhino rejected the annotation.");
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

        static Guid CreateOne(RhinoDoc doc, JObject it)
        {
            var type = J.ReqStr(it, "type").ToLowerInvariant();
            var att = DocUtil.NewAttributes(doc, J.Obj(it, "attributes"));
            var style = DocUtil.DimStyle(doc, J.Str(it, "dimstyle"));
            var plane = J.ParsePlane(doc, J.Get(it, "plane"));

            switch (type)
            {
                case "text":
                {
                    plane.Origin = J.ReqPt(it, "point");
                    var rot = J.Num(it, "rotation_deg", 0);
                    if (rot != 0) plane.Rotate(RhinoMath.ToRadians(rot), plane.ZAxis);
                    var wrap = J.OptNum(it, "wrap_width");
                    var te = TextEntity.Create(J.ReqStr(it, "text"), plane, style, wrap.HasValue, wrap ?? 0, 0);
                    if (te == null) throw J.Failed("Couldn't create the text.");
                    var h = J.OptNum(it, "height");
                    if (h.HasValue) te.TextHeight = h.Value;
                    var just = J.Str(it, "justification");
                    if (just != null) te.Justification = ParseJustification(just);
                    var font = J.Str(it, "font");
                    if (font != null) te.Font = new Font(font);
                    return doc.Objects.AddText(te, att);
                }

                case "leader":
                {
                    var pts = J.Pts(it, "points", 2);
                    plane.Origin = pts[0];
                    var pts2 = pts.Select(pt =>
                    {
                        plane.ClosestParameter(pt, out double s, out double t);
                        return new Point2d(s, t);
                    }).ToList();
                    var prev = doc.DimStyles.CurrentIndex;
                    try
                    {
                        if (J.Has(it, "dimstyle")) doc.DimStyles.SetCurrent(style.Index, true);
                        return doc.Objects.AddLeader(J.ReqStr(it, "text"), plane, pts2, att);
                    }
                    finally
                    {
                        if (J.Has(it, "dimstyle")) doc.DimStyles.SetCurrent(prev, true);
                    }
                }

                case "dimension":
                {
                    var start = J.ReqPt(it, "start");
                    var end = J.ReqPt(it, "end");
                    var linePt = J.ReqPt(it, "line_point");
                    plane.Origin = start;
                    var kind = (J.Str(it, "kind", "aligned") ?? "aligned").ToLowerInvariant();
                    AnnotationType at;
                    double rotation = 0;
                    switch (kind)
                    {
                        case "aligned": at = AnnotationType.Aligned; break;
                        case "horizontal": at = AnnotationType.Rotated; rotation = 0; break;
                        case "vertical": at = AnnotationType.Rotated; rotation = Math.PI / 2; break;
                        default: throw J.Bad("dimension kind must be aligned, horizontal or vertical.");
                    }
                    var dim = LinearDimension.Create(at, style, plane, plane.XAxis, start, end, linePt, rotation);
                    if (dim == null) throw J.Failed("Couldn't create the dimension (points coincident or off-plane?).");
                    return doc.Objects.AddLinearDimension(dim, att);
                }

                case "dot":
                {
                    var dot = new TextDot(J.ReqStr(it, "text"), J.ReqPt(it, "point"))
                    {
                        FontHeight = J.Int(it, "height", 14)
                    };
                    return doc.Objects.AddTextDot(dot, att);
                }

                case "hatch":
                {
                    var boundaries = DocUtil.FindAll(doc, J.Ids(it, "boundary_ids"))
                        .Select(o => o.Geometry as Curve ?? throw J.Bad($"{o.Id} is not a curve."))
                        .ToList();
                    var pattern = HatchPatternIndex(doc, J.Str(it, "pattern", "Solid"));
                    var hatches = Hatch.Create(boundaries, pattern, RhinoMath.ToRadians(J.Num(it, "rotation_deg", 0)),
                                               J.Num(it, "scale", 1), doc.ModelAbsoluteTolerance);
                    if (hatches == null || hatches.Length == 0)
                        throw J.Failed("No hatch region found. Boundaries must be closed, planar curves.");
                    var first = Guid.Empty;
                    foreach (var h in hatches)
                    {
                        var id = doc.Objects.AddHatch(h, att);
                        if (first == Guid.Empty) first = id;
                    }
                    return first;
                }
            }
            throw J.Bad($"Unknown annotation type '{type}'. Use text, leader, dimension, dot or hatch.");
        }

        /// <summary>
        /// Finds a hatch pattern by name. New documents often have an empty pattern table, so the
        /// standard patterns (Solid, Hatch1, Hatch2, Hatch3, HatchDash, Grid, Grid60, Plus, Squares)
        /// are added on first use.
        /// </summary>
        static int HatchPatternIndex(RhinoDoc doc, string name)
        {
            var existing = doc.HatchPatterns.FindName(name);
            if (existing != null && !existing.IsDeleted) return existing.Index;

            var defaults = typeof(HatchPattern).GetNestedType("Defaults");
            var prop = defaults?.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                               .FirstOrDefault(pi => string.Equals(pi.Name, name, StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(pi.Name, name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            if (prop?.GetValue(null) is HatchPattern standard)
            {
                var idx = doc.HatchPatterns.Add(standard);
                if (idx >= 0) return idx;
                var again = doc.HatchPatterns.FindName(standard.Name);
                if (again != null) return again.Index;
            }
            var available = string.Join(", ", doc.HatchPatterns.Where(h => !h.IsDeleted).Select(h => h.Name));
            throw J.NotFound($"No hatch pattern '{name}'. In this file: {(available.Length > 0 ? available : "none")}. " +
                             "Standard patterns: Solid, Hatch1, Hatch2, Hatch3, HatchDash, Grid, Grid60, Plus, Squares.");
        }

        static JToken Edit(RhinoDoc doc, JObject p)
        {
            var newText = J.Str(p, "text");
            var find = J.Str(p, "find");
            var replace = J.Str(p, "replace") ?? "";
            var height = J.OptNum(p, "height");
            var styleName = J.Str(p, "dimstyle");
            var keepEdge = (J.Str(p, "keep_edge") ?? "").ToLowerInvariant();
            if (keepEdge.Length > 0 && keepEdge != "left" && keepEdge != "right" && keepEdge != "center")
                throw J.Bad("keep_edge must be left, right or center.");
            if (newText == null && find == null && !height.HasValue && styleName == null)
                throw J.Bad("Pass text, find/replace, height or dimstyle.");

            var changed = new JArray();
            var skipped = new JArray();
            var unmatched = new JArray();
            foreach (var o in DocUtil.FindAll(doc, J.Ids(p, "ids")))
            {
                string Next(string current) =>
                    newText ?? (find != null ? current.Replace(find, replace) : current);

                switch (o.Geometry)
                {
                    case TextEntity te:
                    {
                        if (find != null && (te.PlainText ?? "").IndexOf(find, StringComparison.Ordinal) < 0)
                        {
                            unmatched.Add(o.Id.ToString());
                            break;
                        }
                        // Edit inside the rich text so fonts, bold/italic and line breaks survive.
                        var nte = (TextEntity)te.Duplicate();
                        var h0 = te.TextHeight;
                        var oldBox = te.GetBoundingBox(true);
                        var rich = te.RichText ?? "";
                        var oldPlain = (te.PlainText ?? "").TrimEnd('\r', '\n');
                        string newRich = null;
                        if (find != null)
                        {
                            var key = EscapeRtf(find);
                            if (key.Length > 0 && rich.Contains(key)) newRich = rich.Replace(key, EscapeRtf(replace));
                        }
                        else if (newText != null)
                        {
                            var key = EscapeRtf(oldPlain);
                            if (key.Length > 0 && rich.Contains(key)) newRich = ReplaceFirst(rich, key, EscapeRtf(newText));
                        }

                        if (newRich != null)
                            nte.RichText = newRich;
                        else if (newText != null || find != null)
                        {
                            // Text spans several formatting runs: rebuild paragraph by paragraph, keeping
                            // each paragraph's font and bold/italic; fall back to one run in the first font.
                            var lines = Next(te.PlainText ?? "").Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                            nte.RichText = RebuildRtf(rich, lines) ?? PlainRtf(string.Join("\n", lines), te.Font);
                        }

                        // Setting RichText can drop a per-object height override back to the dimstyle's.
                        nte.TextHeight = height ?? h0;
                        if (styleName != null) nte.DimensionStyleId = DocUtil.DimStyle(doc, styleName).Id;
                        if (doc.Objects.Replace(o.Id, nte))
                        {
                            changed.Add(o.Id.ToString());
                            if (keepEdge.Length > 0 && oldBox.IsValid)
                            {
                                // Measure the committed object: bounding boxes of unattached text can be wrong.
                                var nb = doc.Objects.FindId(o.Id)?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
                                if (nb.IsValid)
                                {
                                    double dx = keepEdge == "left" ? oldBox.Min.X - nb.Min.X
                                              : keepEdge == "right" ? oldBox.Max.X - nb.Max.X
                                              : oldBox.Center.X - nb.Center.X;
                                    if (Math.Abs(dx) > 1e-9) doc.Objects.Transform(o.Id, Transform.Translation(dx, 0, 0), true);
                                }
                            }
                        }
                        else skipped.Add(o.Id.ToString());
                        break;
                    }
                    case TextDot td:
                    {
                        var nd = new TextDot(Next(td.Text), td.Point) { FontHeight = height.HasValue ? (int)height.Value : td.FontHeight };
                        if (doc.Objects.Replace(o.Id, nd)) changed.Add(o.Id.ToString());
                        else skipped.Add(o.Id.ToString());
                        break;
                    }
                    default:
                        skipped.Add(o.Id.ToString());
                        break;
                }
            }
            var result = new JObject { ["changed_ids"] = changed };
            if (unmatched.Count > 0)
            {
                result["unmatched_ids"] = unmatched;
                result["warning"] = $"'{find}' wasn't found in {unmatched.Count} object(s); they were left unchanged.";
            }
            if (skipped.Count > 0)
            {
                result["skipped_ids"] = skipped;
                result["note"] = "Only text and dots are editable here. For leaders and dimensions use rhino_run_command with _EditText / _Properties, or rhino_run_python.";
            }
            return result;
        }

        /// <summary>Escapes plain text the way Rhino writes it inside RTF runs.</summary>
        static string EscapeRtf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var ch in s.Replace("\r\n", "\n"))
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '{': sb.Append("\\{"); break;
                    case '}': sb.Append("\\}"); break;
                    case '\n': sb.Append("\\par "); break;
                    default:
                        if (ch > 127) sb.Append("\\u").Append((int)ch).Append('?');
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        static string ReplaceFirst(string s, string find, string replace)
        {
            var i = s.IndexOf(find, StringComparison.Ordinal);
            return i < 0 ? s : s.Substring(0, i) + replace + s.Substring(i + find.Length);
        }

        static readonly System.Text.RegularExpressions.Regex RunStart =
            new System.Text.RegularExpressions.Regex(@"\{\\f(\d+)((?:\\[a-z]+-?\d*)*) ");
        static readonly System.Text.RegularExpressions.Regex Fragment =
            new System.Text.RegularExpressions.Regex(@"\{\\f(\d+)((?:\\[a-z]+-?\d*)*) ([^{}]*?)(\\par)?\}");

        /// <summary>
        /// Rebuilds Rhino rich text with new lines, keeping each paragraph's font and flags
        /// (\b, \i, ...). Returns null when the rich text isn't in the shape Rhino writes.
        /// </summary>
        static string RebuildRtf(string rich, string[] lines)
        {
            var t = rich.IndexOf("{\\fonttbl", StringComparison.Ordinal);
            if (t < 0) return null;
            int depth = 0, end = -1;
            for (int j = t; j < rich.Length; j++)
            {
                if (rich[j] == '{') depth++;
                else if (rich[j] == '}' && --depth == 0) { end = j; break; }
            }
            if (end < 0) return null;
            var m0 = RunStart.Match(rich, end + 1);
            if (!m0.Success) return null;

            var paras = new List<string>();
            string cur = null;
            for (var m = Fragment.Match(rich, m0.Index); m.Success; m = m.NextMatch())
            {
                if (cur == null) cur = m.Groups[1].Value + m.Groups[2].Value;
                if (m.Groups[4].Success) { paras.Add(cur); cur = null; }
            }
            if (cur != null) paras.Add(cur);
            if (paras.Count == 0) return null;

            var sb = new System.Text.StringBuilder(rich.Substring(0, m0.Index));
            for (int k = 0; k < lines.Length; k++)
                sb.Append("{\\f").Append(paras[Math.Min(k, paras.Count - 1)]).Append(' ')
                  .Append(EscapeRtf(lines[k])).Append("\\par}");
            return sb.Append('}').ToString();
        }

        static string PlainRtf(string text, Font font)
        {
            var face = font?.QuartetName ?? "Arial";
            var style = (font != null && font.Bold ? "\\b" : "") + (font != null && font.Italic ? "\\i" : "");
            return "{\\rtf1\\deff0{\\fonttbl{\\f0 " + face + ";}}{\\f0" + style + " " + EscapeRtf(text) + "}}";
        }
    }
}
