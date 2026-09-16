using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BiruBox.Core;

public static class SectionBoxService
{
    private static readonly ConcurrentDictionary<ElementId, BoundingBoxXYZ> _boxCache = new();

    public static bool TryCreateOrApplySectionBox(
        UIDocument uiDoc,
        ICollection<ElementId>? preSelectedIds,
        IList<Reference>? pickedReferences,
        AutoSectionSettings settings,
        ForgeTypeId lengthUnitId,
        string default3DViewName,
        out View3D? outputView,
        out string errorMessage)
    {
        outputView = null;
        errorMessage = string.Empty;
        var doc = uiDoc.Document;
        var activeView = doc.ActiveView;

        var hasPreSelection = preSelectedIds != null && preSelectedIds.Count > 0;
        var hasPicks = pickedReferences != null && pickedReferences.Count > 0;

        if (!hasPreSelection && !hasPicks)
        {
            errorMessage = "No elements were selected.";
            return false;
        }

        var points = new List<XYZ>();
        XYZ? alignmentDirection = null;
        var sourceView = activeView;

        if (hasPicks)
        {
            foreach (var reference in pickedReferences!)
            {
                if (!TryGetReferencePoints(doc, sourceView, reference, out var pts))
                {
                    continue;
                }

                points.AddRange(pts);

                if (alignmentDirection == null && settings.AlignToLinearElements)
                {
                    alignmentDirection = TryGetReferenceLinearDirection(doc, reference);
                }
            }
        }

        if (hasPreSelection)
        {
            foreach (var id in preSelectedIds!)
            {
                var element = doc.GetElement(id);
                if (element == null || !TryGetElementPoints(element, sourceView, out var pts))
                {
                    continue;
                }

                points.AddRange(pts);

                if (alignmentDirection == null && settings.AlignToLinearElements)
                {
                    alignmentDirection = TryGetElementLinearDirection(element, Transform.Identity);
                }
            }
        }

        if (points.Count == 0)
        {
            errorMessage = "The selected elements do not produce a valid bounding region in this view.";
            return false;
        }

        var offset = new XYZ(
            UnitUtils.ConvertToInternalUnits(settings.OffsetX, lengthUnitId),
            UnitUtils.ConvertToInternalUnits(settings.OffsetY, lengthUnitId),
            UnitUtils.ConvertToInternalUnits(settings.OffsetZ, lengthUnitId));

        var sectionBox = BuildSectionBox(points, offset, alignmentDirection);
        if (sectionBox == null)
        {
            errorMessage = "Failed to compute section box extents.";
            return false;
        }

        View3D? targetView;

        // Transaction 1: Prepare target view (create, detach template, activate section box)
        using (var tx = new Transaction(doc, "BiruBox Auto-Section Box - Prepare"))
        {
            tx.Start();

            targetView = GetOrCreateTarget3DView(doc, settings, sourceView, default3DViewName, out var viewCreateError);
            if (targetView == null)
            {
                tx.RollBack();
                errorMessage = viewCreateError;
                return false;
            }

            if (targetView.ViewTemplateId != ElementId.InvalidElementId)
            {
                targetView.ViewTemplateId = ElementId.InvalidElementId;
            }

            if (!targetView.IsSectionBoxActive)
            {
                targetView.IsSectionBoxActive = true;
            }

            tx.Commit();
        }

        // Transaction 2: Set section box bounds
        using (var tx = new Transaction(doc, "BiruBox Auto-Section Box - Apply"))
        {
            tx.Start();

            targetView.SetSectionBox(sectionBox);
            _boxCache[targetView.Id] = sectionBox;
            outputView = targetView;

            if (!string.IsNullOrWhiteSpace(settings.ViewTemplateName))
            {
                ApplyViewTemplate(doc, targetView, settings.ViewTemplateName);
            }

            doc.Regenerate();
            tx.Commit();
        }

        return true;
    }

    public static bool TryApplySectionBox(
        UIDocument uiDoc,
        BoundingBoxXYZ sectionBox,
        AutoSectionSettings settings,
        string default3DViewName,
        out View3D? outputView,
        out string errorMessage)
    {
        outputView = null;
        errorMessage = string.Empty;
        var doc = uiDoc.Document;
        var sourceView = doc.ActiveView;

        if (sectionBox == null)
        {
            errorMessage = "Invalid section box provided.";
            return false;
        }

        View3D? targetView;

        using (var tx = new Transaction(doc, "BiruBox Auto-Section Box - Prepare"))
        {
            tx.Start();

            targetView = GetOrCreateTarget3DView(doc, settings, sourceView, default3DViewName, out var viewCreateError);
            if (targetView == null)
            {
                tx.RollBack();
                errorMessage = viewCreateError;
                return false;
            }

            if (targetView.ViewTemplateId != ElementId.InvalidElementId)
            {
                targetView.ViewTemplateId = ElementId.InvalidElementId;
            }

            if (!targetView.IsSectionBoxActive)
            {
                targetView.IsSectionBoxActive = true;
            }

            tx.Commit();
        }

        using (var tx = new Transaction(doc, "BiruBox Auto-Section Box - Apply"))
        {
            tx.Start();

            targetView.SetSectionBox(sectionBox);
            _boxCache[targetView.Id] = sectionBox;
            outputView = targetView;

            if (!string.IsNullOrWhiteSpace(settings.ViewTemplateName))
            {
                ApplyViewTemplate(doc, targetView, settings.ViewTemplateName);
            }

            doc.Regenerate();
            tx.Commit();
        }

        return true;
    }

    private static void ApplyViewTemplate(Document doc, View3D view, string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName) ||
            templateName.Equals("(none)", StringComparison.OrdinalIgnoreCase) ||
            templateName.Equals("<None>", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var template = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(v => v.IsTemplate && v.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase));

        if (template != null)
        {
            try
            {
                view.ViewTemplateId = template.Id;
            }
            catch
            {
                // In case template restricts certain view properties
            }
        }
    }

    public static void ActivateAndZoom(UIDocument? uiDoc, View3D targetView, ICollection<ElementId>? elementsToSelect = null)
    {
        if (uiDoc == null || targetView == null) return;

        // 1. Immediately request / set the active view to targetView
        try
        {
            uiDoc.ActiveView = targetView;
        }
        catch
        {
            try
            {
                uiDoc.RequestViewChange(targetView);
            }
            catch
            {
                // Fallback
            }
        }

        // 2. Zoom to fit if already present in open UI views
        var openViews = uiDoc.GetOpenUIViews();
        var uiv = openViews.FirstOrDefault(v => v.ViewId == targetView.Id);
        if (uiv != null)
        {
            uiv.ZoomToFit();
        }

        // 3. Hook Idling event to guarantee targetView is active and zoomed to fit once Revit completes view rendering
        int idleRetries = 0;
        EventHandler<Autodesk.Revit.UI.Events.IdlingEventArgs>? handler = null;
        handler = (sender, _) =>
        {
            idleRetries++;
            if (sender is UIApplication app)
            {
                // Always unsubscribe after max retries to prevent leaking
                if (app.ActiveUIDocument == null || idleRetries >= 5)
                {
                    app.Idling -= handler;
                    return;
                }

                app.Idling -= handler;
                try
                {
                    var currentUIDoc = app.ActiveUIDocument;
                    if (currentUIDoc.ActiveView?.Id != targetView.Id)
                    {
                        currentUIDoc.ActiveView = targetView;
                    }

                    var targetUiv = currentUIDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == targetView.Id);
                    targetUiv?.ZoomToFit();

                    if (elementsToSelect != null && elementsToSelect.Count > 0)
                    {
                        currentUIDoc.Selection.SetElementIds(elementsToSelect);
                    }
                }
                catch
                {
                }
            }
        };
        uiDoc.Application.Idling += handler;
    }

    public static void TileViews(UIDocument? uiDoc, ElementId? sourceViewId, ElementId? targetViewId, ICollection<ElementId>? elementsToSelect = null)
    {
        if (uiDoc == null || targetViewId == null) return;
        if (uiDoc.Document.GetElement(targetViewId) is View3D view3D)
        {
            ActivateAndZoom(uiDoc, view3D, elementsToSelect);
        }
    }

    public static bool ToggleActive3DSectionBox(UIDocument uiDoc, out string message)
    {
        message = string.Empty;
        var view = uiDoc.Document.ActiveView as View3D;
        if (view == null || view.IsTemplate)
        {
            message = "Open a non-template 3D view to toggle section box visibility.";
            return false;
        }

        using (var tx = new Transaction(uiDoc.Document, "BiruBox Toggle Section Box"))
        {
            tx.Start();
            if (view.IsSectionBoxActive)
            {
                _boxCache[view.Id] = view.GetSectionBox();
                view.IsSectionBoxActive = false;
            }
            else
            {
                view.IsSectionBoxActive = true;
                if (_boxCache.TryGetValue(view.Id, out var cached))
                {
                    view.SetSectionBox(cached);
                }
            }
            tx.Commit();
        }

        message = view.IsSectionBoxActive ? "Section box is now visible." : "Section box is now hidden.";
        return true;
    }

    public static BoundingBoxXYZ? GetCachedSectionBox(ElementId viewId)
    {
        return _boxCache.TryGetValue(viewId, out var box) ? box : null;
    }

    public static void SetCachedSectionBox(ElementId viewId, BoundingBoxXYZ box)
    {
        _boxCache[viewId] = box;
    }

    private static View3D? GetOrCreateTarget3DView(Document doc, AutoSectionSettings settings, View sourceView, string default3DViewName, out string errorMessage)
    {
        errorMessage = string.Empty;
        var requestedName = settings.GetResolvedViewName(default3DViewName);

        // 1. Literal name search (e.g. {3D})
        var existingView = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase));

        if (existingView != null) return existingView;

        // 2. Sanitized name search
        var sanitizedName = SanitizeViewName(requestedName);
        existingView = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(sanitizedName, StringComparison.OrdinalIgnoreCase));

        if (existingView != null) return existingView;

        // 3. Create new 3D view
        var viewType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);

        if (viewType == null)
        {
            errorMessage = "3D view family type not found.";
            return null;
        }

        var newView = View3D.CreateIsometric(doc, viewType.Id);
        newView.Name = MakeUniqueViewName(doc, sanitizedName);
        newView.DisplayStyle = sourceView.DisplayStyle;

        return newView;
    }

    private static string SanitizeViewName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "3D View";

        char[] prohibited = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };
        var result = name;
        foreach (char c in prohibited)
        {
            result = result.Replace(c, ' ');
        }
        while (result.Contains("  "))
            result = result.Replace("  ", " ");
        return result.Trim();
    }

    private static string MakeUniqueViewName(Document doc, string baseName)
    {
        var root = string.IsNullOrWhiteSpace(baseName) ? "Auto Section Box" : baseName.Trim();
        var candidate = root;
        var suffix = 1;

        while (new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Any(v => v.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = root + " " + suffix++;
        }

        return candidate;
    }

    private static BoundingBoxXYZ? BuildSectionBox(IList<XYZ> points, XYZ offset, XYZ? alignmentDirection)
    {
        var origin = XYZ.Zero;
        var xAxis = XYZ.BasisX;
        var yAxis = XYZ.BasisY;
        var zAxis = XYZ.BasisZ;

        if (alignmentDirection != null && alignmentDirection.GetLength() > 1e-9)
        {
            var xyProj = new XYZ(alignmentDirection.X, alignmentDirection.Y, 0);
            xAxis = xyProj.GetLength() > 1e-8 ? xyProj.Normalize() : XYZ.BasisX;

            var up = XYZ.BasisZ;
            yAxis = up.CrossProduct(xAxis).Normalize();
            zAxis = up;
        }

        var transform = Transform.CreateTranslation(XYZ.Zero);
        transform.Origin = origin;
        transform.BasisX = xAxis;
        transform.BasisY = yAxis;
        transform.BasisZ = zAxis;

        var inverse = transform.Inverse;
        var min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new XYZ(double.MinValue, double.MinValue, double.MinValue);

        foreach (var pt in points)
        {
            var local = inverse.OfPoint(pt);
            min = new XYZ(Math.Min(min.X, local.X), Math.Min(min.Y, local.Y), Math.Min(min.Z, local.Z));
            max = new XYZ(Math.Max(max.X, local.X), Math.Max(max.Y, local.Y), Math.Max(max.Z, local.Z));
        }

        min = new XYZ(min.X - offset.X, min.Y - offset.Y, min.Z - offset.Z);
        max = new XYZ(max.X + offset.X, max.Y + offset.Y, max.Z + offset.Z);

        if (max.X - min.X < 1e-5 || max.Y - min.Y < 1e-5 || max.Z - min.Z < 1e-5)
        {
            return null;
        }

        return new BoundingBoxXYZ
        {
            Transform = transform,
            Min = min,
            Max = max
        };
    }

    public static IEnumerable<XYZ> ExpandCorners(BoundingBoxXYZ box)
    {
        var min = box.Min;
        var max = box.Max;
        var pts = new[]
        {
            new XYZ(min.X, min.Y, min.Z),
            new XYZ(max.X, min.Y, min.Z),
            new XYZ(min.X, max.Y, min.Z),
            new XYZ(max.X, max.Y, min.Z),
            new XYZ(min.X, min.Y, max.Z),
            new XYZ(max.X, min.Y, max.Z),
            new XYZ(min.X, max.Y, max.Z),
            new XYZ(max.X, max.Y, max.Z)
        };

        var transform = box.Transform ?? Transform.Identity;
        foreach (var pt in pts)
        {
            yield return transform.OfPoint(pt);
        }
    }

    private static bool TryGetReferencePoints(Document hostDoc, View sourceView, Reference reference, out List<XYZ> points)
    {
        points = new List<XYZ>();
        if (reference == null) return false;

        var hostElement = hostDoc.GetElement(reference.ElementId);
        if (hostElement == null) return false;

        if (reference.LinkedElementId != ElementId.InvalidElementId && hostElement is RevitLinkInstance linkInstance)
        {
            var linkDoc = linkInstance.GetLinkDocument();
            if (linkDoc == null) return false;

            var linkedElement = linkDoc.GetElement(reference.LinkedElementId);
            if (!TryGetElementPoints(linkedElement, null, out var linkedPoints)) return false;

            var totalTransform = linkInstance.GetTotalTransform();
            foreach (var pt in linkedPoints)
            {
                points.Add(totalTransform.OfPoint(pt));
            }

            return true;
        }

        return TryGetElementPoints(hostElement, sourceView, out points);
    }

    private static bool TryGetElementPoints(Element? element, View? sourceView, out List<XYZ> points)
    {
        points = new List<XYZ>();
        if (element == null) return false;

        // Elevation marker crop box
        if (element is ElevationMarker marker)
        {
            for (var i = 0; i < marker.MaximumViewCount; i++)
            {
                if (!marker.IsAvailableIndex(i)) continue;

                var viewId = marker.GetViewId(i);
                if (viewId == ElementId.InvalidElementId) continue;

                if (element.Document.GetElement(viewId) is ViewSection markerView)
                {
                    if (marker.CurrentViewCount == 1 || markerView.CropBoxActive)
                    {
                        var box = markerView.CropBox;
                        if (box != null)
                        {
                            points.AddRange(ExpandCorners(box));
                            return true;
                        }
                    }
                }
            }
        }

        // Section view crop box
        if (element is ViewSection sectionView)
        {
            var box = sectionView.CropBox;
            if (box != null)
            {
                points.AddRange(ExpandCorners(box));
                return true;
            }
        }

        // Section head annotation marker (OST_Viewers)
        if (CategoryRules.IsViewer(element))
        {
            var matchingSection = new FilteredElementCollector(element.Document)
                .OfClass(typeof(ViewSection))
                .Cast<ViewSection>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(element.Name, StringComparison.OrdinalIgnoreCase));
            if (matchingSection?.CropBox != null)
            {
                points.AddRange(ExpandCorners(matchingSection.CropBox));
                return true;
            }
        }

        // PRIMARY: Tight solid geometry vertices (skipping links for speed)
        if (element is not RevitLinkInstance)
        {
            var solidPts = GetTightSolidPoints(element);
            if (solidPts.Count > 0)
            {
                points.AddRange(solidPts);
                return true;
            }
        }

        // SECONDARY: Element bounding box
        var modelBox = element.get_BoundingBox(null);
        if (modelBox != null)
        {
            points.AddRange(ExpandCorners(modelBox));
            return true;
        }

        // FALLBACK: Location curve
        if (element.Location is LocationCurve lc && lc.Curve != null)
        {
            points.Add(lc.Curve.GetEndPoint(0));
            points.Add(lc.Curve.GetEndPoint(1));
            return true;
        }

        // FALLBACK: Grid
        if (element is Grid grid && grid.Curve != null)
        {
            points.Add(grid.Curve.GetEndPoint(0));
            points.Add(grid.Curve.GetEndPoint(1));
            return true;
        }

        // FALLBACK: Level
        if (element is Level level)
        {
            var z = level.Elevation;
            var size = 10.0;
            points.Add(new XYZ(-size, -size, z));
            points.Add(new XYZ(size, size, z));
            return true;
        }

        return false;
    }

    public static List<XYZ> GetTightSolidPoints(Element element)
    {
        var pts = new List<XYZ>();
        if (element == null) return pts;

        var options = new Options { DetailLevel = ViewDetailLevel.Fine };
        var geomElement = element.get_Geometry(options);
        if (geomElement != null)
        {
            ExtractSolidPoints(geomElement, Transform.Identity, pts);
        }
        return pts;
    }

    private static void ExtractSolidPoints(GeometryElement geomElement, Transform transform, List<XYZ> pts)
    {
        if (geomElement == null) return;
        foreach (var geomObj in geomElement)
        {
            if (geomObj is Solid solid && solid.Faces.Size > 0 && solid.Volume > 0)
            {
                foreach (Face face in solid.Faces)
                {
                    var mesh = face.Triangulate();
                    if (mesh != null)
                    {
                        foreach (var vertex in mesh.Vertices)
                        {
                            pts.Add(transform.OfPoint(vertex));
                        }
                    }
                }
            }
            else if (geomObj is GeometryInstance instance)
            {
                // GetInstanceGeometry() returns geometry already in world coordinates,
                // so always pass Identity to avoid double-transforming
                var instanceGeom = instance.GetInstanceGeometry();
                ExtractSolidPoints(instanceGeom, Transform.Identity, pts);
            }
        }
    }

    private static XYZ? TryGetReferenceLinearDirection(Document doc, Reference reference)
    {
        if (reference == null) return null;

        var element = doc.GetElement(reference.ElementId);
        if (element is RevitLinkInstance link && reference.LinkedElementId != ElementId.InvalidElementId)
        {
            var linked = link.GetLinkDocument()?.GetElement(reference.LinkedElementId);
            return TryGetElementLinearDirection(linked, link.GetTotalTransform());
        }

        return TryGetElementLinearDirection(element, Transform.Identity);
    }

    private static XYZ? TryGetElementLinearDirection(Element? element, Transform transform)
    {
        if (element == null) return null;

        if (element.Location is LocationCurve locationCurve && locationCurve.Curve != null)
        {
            var direction = locationCurve.Curve.GetEndPoint(1) - locationCurve.Curve.GetEndPoint(0);
            var vec = transform.OfVector(direction);
            return vec.GetLength() > 1e-8 ? vec.Normalize() : null;
        }

        if (element is Grid grid && grid.Curve != null)
        {
            var direction = grid.Curve.GetEndPoint(1) - grid.Curve.GetEndPoint(0);
            var vec = transform.OfVector(direction);
            return vec.GetLength() > 1e-8 ? vec.Normalize() : null;
        }

        if (element is FamilyInstance fi && fi.Location is LocationPoint lp)
        {
            if (Math.Abs(lp.Rotation) > 1e-4)
            {
                var dir = new XYZ(Math.Cos(lp.Rotation), Math.Sin(lp.Rotation), 0);
                var vec = transform.OfVector(dir);
                return vec.GetLength() > 1e-8 ? vec.Normalize() : null;
            }
            if (fi.FacingOrientation != null && fi.FacingOrientation.GetLength() > 1e-4)
            {
                var dir = fi.HandOrientation != null && fi.HandOrientation.GetLength() > 1e-4
                    ? fi.HandOrientation
                    : fi.FacingOrientation;
                var vec = transform.OfVector(dir);
                return vec.GetLength() > 1e-8 ? vec.Normalize() : null;
            }
        }

        return null;
    }
}
