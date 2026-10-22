using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpenRhinoMcp.Core;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace OpenRhinoMcp.Handlers
{
    public static class ViewHandlers
    {
        public static void Register()
        {
            Registry.Add("views.list", List);
            Registry.Add("views.set", Set, MethodFlags.ReadOnly);
            Registry.Add("views.capture", Capture);
            Registry.Add("layouts.create", CreateLayout, MethodFlags.Edit);
            Registry.Add("export.pdf", ExportPdf);
        }

        static string ProjectionName(RhinoViewport vp)
        {
            if (vp.IsTwoPointPerspectiveProjection) return "two_point_perspective";
            if (vp.IsPerspectiveProjection) return "perspective";
            return "parallel";
        }

        static JToken List(RhinoDoc doc, JObject p)
        {
            var views = new JArray();
            foreach (var v in doc.Views.GetViewList(true, false))
            {
                var vp = v.MainViewport;
                views.Add(new JObject
                {
                    ["name"] = vp.Name,
                    ["kind"] = "model",
                    ["projection"] = ProjectionName(vp),
                    ["display_mode"] = vp.DisplayMode?.EnglishName,
                    ["maximized"] = v.Maximized
                });
            }
            foreach (var pv in doc.Views.GetPageViews().OrderBy(x => x.PageNumber))
            {
                var details = new JArray();
                foreach (var d in pv.GetDetailViews())
                {
                    details.Add(new JObject
                    {
                        ["id"] = d.Id.ToString(),
                        ["name"] = d.Attributes.Name ?? "",
                        ["projection"] = ProjectionName(d.Viewport),
                        ["view_name"] = d.Viewport.Name,
                        ["locked"] = d.DetailGeometry.IsProjectionLocked,
                        ["page_to_model_ratio"] = J.N(d.DetailGeometry.PageToModelRatio),
                        ["display_mode"] = d.Viewport.DisplayMode?.EnglishName
                    });
                }
                views.Add(new JObject
                {
                    ["name"] = pv.PageName,
                    ["kind"] = "layout",
                    ["page_number"] = pv.PageNumber,
                    ["width"] = J.N(pv.PageWidth),
                    ["height"] = J.N(pv.PageHeight),
                    ["page_units"] = doc.PageUnitSystem.ToString(),
                    ["details"] = details
                });
            }
            var named = new JArray();
            for (int i = 0; i < doc.NamedViews.Count; i++) named.Add(doc.NamedViews[i].Name);
            var av = doc.Views.ActiveView;
            return new JObject
            {
                ["active"] = av != null ? DocUtil.ViewName(av) : null,
                ["views"] = views,
                ["named_views"] = named,
                ["display_modes"] = new JArray(DisplayModeDescription.GetDisplayModes().Select(d => d.EnglishName))
            };
        }

        static DisplayModeDescription Mode(string name)
        {
            var dm = DisplayModeDescription.FindByName(name)
                     ?? DisplayModeDescription.GetDisplayModes().FirstOrDefault(d => string.Equals(d.EnglishName, name, StringComparison.OrdinalIgnoreCase)
                                                                                  || string.Equals(d.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (dm == null) throw J.NotFound($"No display mode '{name}'. Use rhino_list_views to see them.");
            return dm;
        }

        static DefinedViewportProjection Projection(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "top": return DefinedViewportProjection.Top;
                case "bottom": return DefinedViewportProjection.Bottom;
                case "left": return DefinedViewportProjection.Left;
                case "right": return DefinedViewportProjection.Right;
                case "front": return DefinedViewportProjection.Front;
                case "back": return DefinedViewportProjection.Back;
                case "perspective": return DefinedViewportProjection.Perspective;
                case "two_point_perspective": return DefinedViewportProjection.TwoPointPerspective;
            }
            throw J.Bad("projection must be top, bottom, left, right, front, back, perspective or two_point_perspective.");
        }

        static JToken Set(RhinoDoc doc, JObject p)
        {
            var action = J.ReqStr(p, "action").ToLowerInvariant();
            var views = J.Bool(p, "all_views", false)
                ? doc.Views.GetViewList(true, false).ToList()
                : new List<RhinoView> { DocUtil.FindView(doc, J.Str(p, "view")) };

            foreach (var view in views)
            {
                var vp = view.ActiveViewport;
                switch (action)
                {
                    case "activate":
                        doc.Views.ActiveView = view;
                        if (view is RhinoPageView pv) pv.SetPageAsActive();
                        break;
                    case "zoom_extents":
                        vp.ZoomExtents();
                        break;
                    case "zoom_selected":
                        vp.ZoomExtentsSelected();
                        break;
                    case "zoom_ids":
                        var box = DocUtil.UnionBox(DocUtil.FindAll(doc, J.Ids(p, "ids")));
                        if (!box.IsValid) throw J.Failed("Those objects have no extents.");
                        box.Inflate(box.Diagonal.Length * 0.05);
                        vp.ZoomBoundingBox(box);
                        break;
                    case "projection":
                        vp.SetProjection(Projection(J.ReqStr(p, "projection")), null, false);
                        vp.ZoomExtents();
                        break;
                    case "named_view":
                        var nv = J.ReqStr(p, "named_view");
                        var idx = doc.NamedViews.FindByName(nv);
                        if (idx < 0) throw J.NotFound($"No named view '{nv}'.");
                        // Restore(int, RhinoViewport, bool) is obsolete since Rhino 6 (backgroundBitmap ignored).
                        doc.NamedViews.Restore(idx, vp);
                        break;
                    case "display_mode":
                        vp.DisplayMode = Mode(J.ReqStr(p, "display_mode"));
                        break;
                    case "save_named_view":
                        var name = J.ReqStr(p, "named_view");
                        if (doc.NamedViews.Add(name, vp.Id) < 0) throw J.Failed($"Couldn't save named view '{name}'.");
                        break;
                    default:
                        throw J.Bad("action must be activate, zoom_extents, zoom_selected, zoom_ids, projection, named_view, display_mode or save_named_view.");
                }
                view.Redraw();
            }
            return new JObject { ["success"] = true };
        }

        static JToken Capture(RhinoDoc doc, JObject p)
        {
            var view = DocUtil.FindView(doc, J.Str(p, "view"));
            var vp = view.ActiveViewport;
            var width = Math.Max(64, Math.Min(4096, J.Int(p, "width", 1600)));
            var height = Math.Max(64, Math.Min(4096, J.Int(p, "height", 1000)));

            // Layouts render the paper only (like a PDF page), optionally cropped to a page region.
            if (view is RhinoPageView page && J.Str(p, "display_mode") == null && J.Get(p, "zoom_ids") == null)
                return CapturePage(doc, page, p, width);

            var ids = J.Ids(p, "zoom_ids", false);
            if (ids != null)
            {
                var box = DocUtil.UnionBox(DocUtil.FindAll(doc, ids));
                if (box.IsValid) { box.Inflate(box.Diagonal.Length * 0.05); vp.ZoomBoundingBox(box); }
            }

            DisplayModeDescription previous = null;
            var modeName = J.Str(p, "display_mode");
            if (modeName != null) { previous = vp.DisplayMode; vp.DisplayMode = Mode(modeName); }

            System.Drawing.Bitmap bmp = null;
            try
            {
                view.Redraw();
                var vc = new ViewCapture
                {
                    Width = width,
                    Height = height,
                    ScaleScreenItems = false,
                    DrawAxes = J.Bool(p, "axes", false),
                    DrawGrid = J.Bool(p, "grid", false),
                    DrawGridAxes = J.Bool(p, "grid", false),
                    TransparentBackground = J.Bool(p, "transparent", false)
                };
                bmp = vc.CaptureToBitmap(view) ?? view.CaptureToBitmap(new System.Drawing.Size(width, height));
                if (bmp == null) throw J.Failed("View capture failed.");
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return new JObject
                    {
                        ["view"] = DocUtil.ViewName(view),
                        ["width"] = bmp.Width,
                        ["height"] = bmp.Height,
                        ["png_base64"] = Convert.ToBase64String(ms.ToArray())
                    };
                }
            }
            finally
            {
                bmp?.Dispose();
                if (previous != null) { vp.DisplayMode = previous; view.Redraw(); }
            }
        }

        static JToken CapturePage(RhinoDoc doc, RhinoPageView page, JObject p, int width)
        {
            double pageW = page.PageWidth, pageH = page.PageHeight;
            double x0 = 0, y0 = 0, x1 = pageW, y1 = pageH;
            if (J.Obj(p, "crop") is JObject crop)
            {
                var a = J.ReqPt(crop, "corner_a");
                var b = J.ReqPt(crop, "corner_b");
                x0 = Math.Max(0, Math.Min(a.X, b.X)); x1 = Math.Min(pageW, Math.Max(a.X, b.X));
                y0 = Math.Max(0, Math.Min(a.Y, b.Y)); y1 = Math.Min(pageH, Math.Max(a.Y, b.Y));
                if (x1 - x0 <= 0 || y1 - y0 <= 0) throw J.Bad("crop is outside the page or has zero size (page units, origin bottom-left).");
            }

            var toInches = RhinoMath.UnitScale(doc.PageUnitSystem, UnitSystem.Inches);
            var pageWIn = pageW * toInches;
            var cropWIn = (x1 - x0) * toInches;
            // dpi that gives the requested pixel width for the cropped region, capped so the full page stays < 14k px.
            var dpi = Math.Max(36, Math.Min(Math.Min(1200, 14000 / Math.Max(pageWIn, 0.1)), width / Math.Max(cropWIn, 0.01)));

            var settings = new ViewCaptureSettings(page, dpi);
            using (var full = ViewCapture.CaptureToBitmap(settings))
            {
                if (full == null) throw J.Failed("Layout capture failed.");
                int px0 = (int)Math.Floor(x0 / pageW * full.Width);
                int px1 = (int)Math.Ceiling(x1 / pageW * full.Width);
                int py0 = (int)Math.Floor((pageH - y1) / pageH * full.Height);   // bitmap rows run top-down
                int py1 = (int)Math.Ceiling((pageH - y0) / pageH * full.Height);
                var rect = new System.Drawing.Rectangle(px0, py0, Math.Max(1, Math.Min(full.Width, px1) - px0), Math.Max(1, Math.Min(full.Height, py1) - py0));
                using (var bmp = rect.Width == full.Width && rect.Height == full.Height ? (System.Drawing.Bitmap)full.Clone() : full.Clone(rect, full.PixelFormat))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return new JObject
                    {
                        ["view"] = page.PageName,
                        ["width"] = bmp.Width,
                        ["height"] = bmp.Height,
                        ["dpi"] = J.N(dpi),
                        ["region"] = new JObject { ["corner_a"] = new JArray(J.N(x0), J.N(y0)), ["corner_b"] = new JArray(J.N(x1), J.N(y1)) },
                        ["png_base64"] = Convert.ToBase64String(ms.ToArray())
                    };
                }
            }
        }

        static JToken CreateLayout(RhinoDoc doc, JObject p)
        {
            var name = J.ReqStr(p, "name");
            if (DocUtil.FindView(doc, name, false) is RhinoPageView) throw J.Failed($"A layout named '{name}' already exists.");
            var pv = doc.Views.AddPageView(name, J.ReqNum(p, "width"), J.ReqNum(p, "height"));
            if (pv == null) throw J.Failed("Couldn't create the layout.");

            var detailIds = new JArray();
            if (J.Arr(p, "details") is JArray details)
            {
                foreach (var dt in details.OfType<JObject>())
                {
                    var a = J.ReqPt(dt, "corner_a");
                    var b = J.ReqPt(dt, "corner_b");
                    var proj = Projection(J.Str(dt, "projection", "top"));
                    var dv = pv.AddDetailView(J.Str(dt, "name", "Detail"), new Point2d(a.X, a.Y), new Point2d(b.X, b.Y), proj);
                    if (dv == null) throw J.Failed("Couldn't add a detail (check corners are inside the page).");

                    pv.SetActiveDetail(dv.Id);
                    var zoomIds = J.Ids(dt, "zoom_ids", false);
                    if (zoomIds != null)
                    {
                        var box = DocUtil.UnionBox(DocUtil.FindAll(doc, zoomIds));
                        if (box.IsValid) dv.Viewport.ZoomBoundingBox(box);
                    }
                    else dv.Viewport.ZoomExtents();
                    // Commit the camera first so it can't overwrite the scale set below.
                    dv.CommitViewportChanges();

                    if (J.Obj(dt, "scale") is JObject sc)
                    {
                        dv.DetailGeometry.SetScale(J.ReqNum(sc, "model"), DocUtil.ParseUnits(J.Str(sc, "model_units", doc.ModelUnitSystem.ToString())),
                                                   J.ReqNum(sc, "page"), DocUtil.ParseUnits(J.Str(sc, "page_units", doc.PageUnitSystem.ToString())));
                    }
                    if (J.Bool(dt, "lock", true)) dv.DetailGeometry.IsProjectionLocked = true;
                    var detailName = J.Str(dt, "name");
                    if (!string.IsNullOrEmpty(detailName)) dv.Attributes.Name = detailName;
                    dv.CommitChanges();
                    detailIds.Add(dv.Id.ToString());
                }
            }
            pv.SetPageAsActive();
            doc.Views.ActiveView = pv;
            pv.Redraw();
            return new JObject { ["view"] = pv.PageName, ["details"] = detailIds };
        }

        static JToken ExportPdf(RhinoDoc doc, JObject p)
        {
            var path = Path.GetFullPath(J.ReqStr(p, "path"));
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) path += ".pdf";
            var dpi = Math.Max(72, Math.Min(1200, J.Int(p, "dpi", 300)));
            var color = (J.Str(p, "color", "display") ?? "display").ToLowerInvariant();

            var settings = new List<ViewCaptureSettings>();
            var viewName = J.Str(p, "view");
            if (viewName != null)
            {
                var view = DocUtil.FindView(doc, viewName);
                if (view is RhinoPageView pvOnly) settings.Add(new ViewCaptureSettings(pvOnly, dpi));
                else
                {
                    var size = new System.Drawing.Size((int)(J.Num(p, "width", 11) * dpi), (int)(J.Num(p, "height", 8.5) * dpi));
                    settings.Add(new ViewCaptureSettings(view, size, dpi));
                }
            }
            else
            {
                IEnumerable<RhinoPageView> pages = doc.Views.GetPageViews().OrderBy(v => v.PageNumber);
                if (J.Arr(p, "layouts") is JArray names && names.Count > 0)
                    pages = names.Select(n => DocUtil.FindView(doc, n.ToString()) as RhinoPageView
                                              ?? throw J.NotFound($"'{n}' is not a layout."));
                foreach (var pv in pages) settings.Add(new ViewCaptureSettings(pv, dpi));
            }
            if (settings.Count == 0) throw J.Failed("Nothing to export: the document has no layouts. Pass 'view' to export a model view.");

            var pdf = FilePdf.Create();
            foreach (var s in settings)
            {
                switch (color)
                {
                    case "print": s.OutputColor = ViewCaptureSettings.ColorMode.PrintColor; break;
                    case "black_white": s.OutputColor = ViewCaptureSettings.ColorMode.BlackAndWhite; break;
                    default: s.OutputColor = ViewCaptureSettings.ColorMode.DisplayColor; break;
                }
                pdf.AddPage(s);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            pdf.Write(path);
            return new JObject { ["path"] = path, ["pages"] = settings.Count };
        }
    }
}
