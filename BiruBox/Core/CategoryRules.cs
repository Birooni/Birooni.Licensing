using Autodesk.Revit.DB;

namespace BiruBox.Core;

public static class CategoryRules
{
    static readonly HashSet<BuiltInCategory> Annotations =
    [
        BuiltInCategory.OST_Dimensions,
        BuiltInCategory.OST_Constraints,
        BuiltInCategory.OST_TextNotes,
        BuiltInCategory.OST_GenericAnnotation,
        BuiltInCategory.OST_Viewports,
        BuiltInCategory.OST_TitleBlocks,
        BuiltInCategory.OST_Tags,
        BuiltInCategory.OST_DoorTags,
        BuiltInCategory.OST_WindowTags,
        BuiltInCategory.OST_WallTags,
        BuiltInCategory.OST_RoomTags,
        BuiltInCategory.OST_PipeTags,
        BuiltInCategory.OST_DuctTags,
        BuiltInCategory.OST_Matchline,
        BuiltInCategory.OST_Lines,
        BuiltInCategory.OST_SketchLines,
        BuiltInCategory.OST_CLines,
    ];

    static readonly HashSet<BuiltInCategory> Datums =
    [
        BuiltInCategory.OST_Grids,
        BuiltInCategory.OST_Levels,
        BuiltInCategory.OST_VolumeOfInterest,
        BuiltInCategory.OST_Viewers,
    ];

    public static bool IsAnnotation(Element e)
    {
        BuiltInCategory? bic = Bic(e);
        return bic is { } value && Annotations.Contains(value);
    }

    public static bool IsDatum(Element e)
    {
        if (e is Grid or Level)
            return true;
        BuiltInCategory? bic = Bic(e);
        return bic is { } value && Datums.Contains(value);
    }

    public static bool IsScopeBox(Element e) =>
        Bic(e) == BuiltInCategory.OST_VolumeOfInterest;

    public static bool IsViewer(Element e) =>
        Bic(e) == BuiltInCategory.OST_Viewers || e is ViewSection;

    public static bool IsLineBased(Element e) =>
        e is Wall or Grid
        || e.Location is LocationCurve
        || (e is FamilyInstance fi && fi.Location is LocationPoint lp && Math.Abs(lp.Rotation) > 1e-4);

    static BuiltInCategory? Bic(Element e)
    {
        if (e.Category is null)
            return null;
        try
        {
            return e.Category.BuiltInCategory;
        }
        catch
        {
            return null;
        }
    }
}
