using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace BiruBox.Core;

public static class DrawBoxService
{
    public static BoundingBoxXYZ BuildBox(Document doc, View activeView, PickedBox pickedBox)
    {
        if (activeView is ViewPlan planView)
        {
            return BuildBoxForPlan(doc, planView, pickedBox);
        }

        if (activeView is ViewSection sectionView)
        {
            return BuildBoxForSection(sectionView, pickedBox);
        }

        if (activeView.ViewType == ViewType.Section ||
            activeView.ViewType == ViewType.Elevation ||
            activeView.ViewType == ViewType.Detail)
        {
            return BuildBoxForSection(activeView, pickedBox);
        }

        return BuildBoxGeneric(activeView, pickedBox);
    }

    static BoundingBoxXYZ BuildBoxForPlan(Document doc, ViewPlan planView, PickedBox pickedBox)
    {
        (double minZ, double maxZ) = GetPlanZRange(doc, planView);

        XYZ p1 = pickedBox.Min;
        XYZ p2 = pickedBox.Max;

        BoundingBoxXYZ? crop = planView.CropBox;
        Transform transform = crop?.Transform ?? Transform.Identity;

        // If the plan view is standard axis-aligned
        bool isAxisAligned = Math.Abs(transform.BasisX.Y) < 1e-5 && Math.Abs(transform.BasisY.X) < 1e-5;
        if (isAxisAligned)
        {
            double minX = Math.Min(p1.X, p2.X);
            double maxX = Math.Max(p1.X, p2.X);
            double minY = Math.Min(p1.Y, p2.Y);
            double maxY = Math.Max(p1.Y, p2.Y);

            if (maxX - minX < 1.0) { minX -= 0.5; maxX += 0.5; }
            if (maxY - minY < 1.0) { minY -= 0.5; maxY += 0.5; }

            return new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ),
            };
        }

        // Rotated plan view: project points into local view coordinates
        Transform inv = transform.Inverse;
        XYZ l1 = inv.OfPoint(p1);
        XYZ l2 = inv.OfPoint(p2);

        double locMinX = Math.Min(l1.X, l2.X);
        double locMaxX = Math.Max(l1.X, l2.X);
        double locMinY = Math.Min(l1.Y, l2.Y);
        double locMaxY = Math.Max(l1.Y, l2.Y);

        if (locMaxX - locMinX < 1.0) { locMinX -= 0.5; locMaxX += 0.5; }
        if (locMaxY - locMinY < 1.0) { locMinY -= 0.5; locMaxY += 0.5; }

        var corners = new List<XYZ>(8);
        double[] xs = [locMinX, locMaxX];
        double[] ys = [locMinY, locMaxY];

        foreach (double x in xs)
        {
            foreach (double y in ys)
            {
                XYZ worldPt = transform.OfPoint(new XYZ(x, y, 0));
                corners.Add(new XYZ(worldPt.X, worldPt.Y, minZ));
                corners.Add(new XYZ(worldPt.X, worldPt.Y, maxZ));
            }
        }

        XYZ center = (corners[0] + corners[2] + corners[4] + corners[6]) / 4.0;
        Transform frame = BoxMath.FrameFromDirection(transform.BasisX, center);
        return BoxMath.FromWorldCorners(corners, frame, 0);
    }

    static (double minZ, double maxZ) GetPlanZRange(Document doc, ViewPlan planView)
    {
        try
        {
            PlanViewRange vr = planView.GetViewRange();
            List<Level> sortedLevels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            double topZ = GetPlaneElevation(doc, planView, vr, PlanViewPlane.TopClipPlane, sortedLevels)
                          ?? (planView.GenLevel?.Elevation + 10.0 ?? 10.0);
            double cutZ = GetPlaneElevation(doc, planView, vr, PlanViewPlane.CutPlane, sortedLevels)
                          ?? (planView.GenLevel?.Elevation + 4.0 ?? 4.0);
            double bottomZ = GetPlaneElevation(doc, planView, vr, PlanViewPlane.BottomClipPlane, sortedLevels)
                             ?? (planView.GenLevel?.Elevation ?? 0.0);
            double depthZ = GetPlaneElevation(doc, planView, vr, PlanViewPlane.ViewDepthPlane, sortedLevels)
                            ?? bottomZ;

            bool isCeiling = planView.ViewType == ViewType.CeilingPlan;

            double minZ, maxZ;
            if (isCeiling)
            {
                // Ceiling plan (RCP): looks upwards from cut plane to top / view depth
                minZ = Math.Min(cutZ, bottomZ);
                maxZ = Math.Max(topZ, depthZ);
            }
            else
            {
                // Floor plan: looks downwards from top clip to view depth / bottom clip
                minZ = Math.Min(bottomZ, depthZ);
                maxZ = topZ;
            }

            if (maxZ - minZ < 1.0)
                maxZ = minZ + 1.0;

            return (minZ, maxZ);
        }
        catch
        {
            double baseZ = planView.GenLevel?.Elevation ?? 0.0;
            return (baseZ, baseZ + 10.0);
        }
    }

    static double? GetPlaneElevation(
        Document doc,
        ViewPlan planView,
        PlanViewRange vr,
        PlanViewPlane plane,
        List<Level> sortedLevels)
    {
        ElementId levelId = vr.GetLevelId(plane);
        double offset = vr.GetOffset(plane);

        Level? baseLevel = null;

        if (levelId == PlanViewRange.Current || levelId.Value == -3)
        {
            baseLevel = planView.GenLevel;
        }
        else if (levelId == PlanViewRange.LevelAbove || levelId.Value == -2)
        {
            if (planView.GenLevel != null)
            {
                baseLevel = sortedLevels.FirstOrDefault(l => l.Elevation > planView.GenLevel.Elevation + 1e-4)
                            ?? planView.GenLevel;
            }
        }
        else if (levelId == PlanViewRange.LevelBelow || levelId.Value == -4)
        {
            if (planView.GenLevel != null)
            {
                baseLevel = sortedLevels.LastOrDefault(l => l.Elevation < planView.GenLevel.Elevation - 1e-4)
                            ?? planView.GenLevel;
            }
        }
        else if (levelId == PlanViewRange.Unlimited || levelId.Value == -1)
        {
            if (plane == PlanViewPlane.TopClipPlane)
                return sortedLevels.Count > 0 ? sortedLevels.Last().Elevation + 20.0 : 100.0;
            else
                return sortedLevels.Count > 0 ? sortedLevels.First().Elevation - 20.0 : -100.0;
        }
        else if (levelId != ElementId.InvalidElementId)
        {
            baseLevel = doc.GetElement(levelId) as Level ?? planView.GenLevel;
        }
        else
        {
            baseLevel = planView.GenLevel;
        }

        if (baseLevel == null)
            return null;

        return baseLevel.Elevation + offset;
    }

    static BoundingBoxXYZ BuildBoxForSection(View sectionView, PickedBox pickedBox)
    {
        BoundingBoxXYZ? crop = sectionView.CropBox;
        Transform cropTransform = crop?.Transform ?? Transform.Identity;

        if (crop == null || cropTransform.IsIdentity)
        {
            cropTransform = Transform.CreateTranslation(XYZ.Zero);
            cropTransform.Origin = sectionView.Origin;
            cropTransform.BasisX = sectionView.RightDirection.Normalize();
            cropTransform.BasisY = sectionView.UpDirection.Normalize();
            cropTransform.BasisZ = sectionView.ViewDirection.Normalize();
        }

        Transform inv = cropTransform.Inverse;
        XYZ l1 = inv.OfPoint(pickedBox.Min);
        XYZ l2 = inv.OfPoint(pickedBox.Max);

        double minX = Math.Min(l1.X, l2.X);
        double maxX = Math.Max(l1.X, l2.X);
        double minY = Math.Min(l1.Y, l2.Y);
        double maxY = Math.Max(l1.Y, l2.Y);

        if (maxX - minX < 1.0) { minX -= 0.5; maxX += 0.5; }
        if (maxY - minY < 1.0) { minY -= 0.5; maxY += 0.5; }

        double minZ = 0, maxZ = 0;
        if (crop is not null && Math.Abs(crop.Max.Z - crop.Min.Z) > 1e-4)
        {
            minZ = Math.Min(crop.Min.Z, crop.Max.Z);
            maxZ = Math.Max(crop.Min.Z, crop.Max.Z);
        }
        else
        {
            Parameter? pFar = sectionView.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
            if (pFar != null && pFar.HasValue && pFar.AsDouble() > 1e-4)
            {
                double depth = pFar.AsDouble();
                minZ = -depth;
                maxZ = 0;
            }
            else
            {
                minZ = -5.0;
                maxZ = 5.0;
            }
        }

        if (maxZ - minZ < 1.0)
            maxZ = minZ + 1.0;

        return new BoundingBoxXYZ
        {
            Transform = cropTransform,
            Min = new XYZ(minX, minY, minZ),
            Max = new XYZ(maxX, maxY, maxZ),
        };
    }

    static BoundingBoxXYZ BuildBoxGeneric(View view, PickedBox pickedBox)
    {
        XYZ p1 = pickedBox.Min;
        XYZ p2 = pickedBox.Max;
        double minX = Math.Min(p1.X, p2.X);
        double maxX = Math.Max(p1.X, p2.X);
        double minY = Math.Min(p1.Y, p2.Y);
        double maxY = Math.Max(p1.Y, p2.Y);
        double minZ = Math.Min(p1.Z, p2.Z);
        double maxZ = Math.Max(p1.Z, p2.Z);

        if (maxX - minX < 1.0) { minX -= 0.5; maxX += 0.5; }
        if (maxY - minY < 1.0) { minY -= 0.5; maxY += 0.5; }
        if (maxZ - minZ < 1.0) { maxZ = minZ + 10.0; }

        return new BoundingBoxXYZ
        {
            Transform = Transform.Identity,
            Min = new XYZ(minX, minY, minZ),
            Max = new XYZ(maxX, maxY, maxZ),
        };
    }
}
