using Autodesk.Revit.DB;

namespace BiruBox.Core;

public static class BoxMath
{
    public static BoundingBoxXYZ FromWorldCorners(IEnumerable<XYZ> worldPoints, Transform frame, double bufferFt)
    {
        Transform inverse = frame.Inverse;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        int count = 0;
        foreach (XYZ p in worldPoints)
        {
            XYZ local = inverse.OfPoint(p);
            minX = Math.Min(minX, local.X);
            minY = Math.Min(minY, local.Y);
            minZ = Math.Min(minZ, local.Z);
            maxX = Math.Max(maxX, local.X);
            maxY = Math.Max(maxY, local.Y);
            maxZ = Math.Max(maxZ, local.Z);
            count++;
        }

        if (count == 0)
            throw new InvalidOperationException("No geometry to box.");

        var box = new BoundingBoxXYZ
        {
            Transform = frame,
            Min = new XYZ(minX - bufferFt, minY - bufferFt, minZ - bufferFt),
            Max = new XYZ(maxX + bufferFt, maxY + bufferFt, maxZ + bufferFt),
        };
        return box;
    }

    public static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
    {
        Transform t = box.Transform ?? Transform.Identity;
        XYZ min = box.Min;
        XYZ max = box.Max;
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
        for (int k = 0; k < 2; k++)
        {
            var local = new XYZ(
                i == 0 ? min.X : max.X,
                j == 0 ? min.Y : max.Y,
                k == 0 ? min.Z : max.Z);
            yield return t.OfPoint(local);
        }
    }

    public static BoundingBoxXYZ? ElementWorldBox(Element element, Transform? additional)
    {
        BoundingBoxXYZ? bb = element.get_BoundingBox(null);
        if (bb is null)
            return null;
        if (additional is null || additional.IsIdentity)
            return bb;

        var world = new BoundingBoxXYZ { Transform = Transform.Identity };
        IEnumerable<XYZ> pts = Corners(bb).Select(additional.OfPoint);
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        foreach (XYZ p in pts)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            minZ = Math.Min(minZ, p.Z);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
            maxZ = Math.Max(maxZ, p.Z);
        }

        world.Min = new XYZ(minX, minY, minZ);
        world.Max = new XYZ(maxX, maxY, maxZ);
        return world;
    }

    public static Transform FrameFromDirection(XYZ direction, XYZ origin)
    {
        XYZ x = new XYZ(direction.X, direction.Y, 0);
        if (x.GetLength() < 1e-8)
            x = XYZ.BasisX;
        x = x.Normalize();
        XYZ z = XYZ.BasisZ;
        XYZ y = z.CrossProduct(x);
        if (y.GetLength() < 1e-8)
            y = XYZ.BasisY;
        y = y.Normalize();
        x = y.CrossProduct(z).Normalize();
        Transform t = Transform.CreateTranslation(XYZ.Zero);
        t.Origin = origin;
        t.BasisX = x;
        t.BasisY = y;
        t.BasisZ = z;
        return t;
    }

    public static BoundingBoxXYZ Grow(BoundingBoxXYZ box, double deltaFt)
    {
        var copy = new BoundingBoxXYZ
        {
            Transform = box.Transform,
            Min = box.Min - new XYZ(deltaFt, deltaFt, deltaFt),
            Max = box.Max + new XYZ(deltaFt, deltaFt, deltaFt),
        };
        return copy;
    }

    public static double MmToFt(double mm) =>
        UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}
