using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BiruBox.Core;

namespace BiruBox.Commands;

static class ResizeSectionBox
{
    public static Result Run(ExternalCommandData commandData, int sign, ref string message)
    {
        if (!LicensingManager.EnsureLicense())
        {
            return Result.Cancelled;
        }

        UIDocument? uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc == null) return Result.Cancelled;

        Document doc = uidoc.Document;
        var view = doc.ActiveView as View3D;
        if (view == null || !view.IsSectionBoxActive)
        {
            message = "Activate a 3D view that already has an active section box.";
            TaskDialog.Show("BiruBox", message);
            return Result.Cancelled;
        }

        var formatOptions = doc.GetUnits().GetFormatOptions(SpecTypeId.Length);
        var lengthUnitId = formatOptions.GetUnitTypeId();

        var settings = SettingsStore.Load();
        settings.EnsureUnitDefaults(lengthUnitId);

        double stepVal = settings.GrowStep > 0 ? settings.GrowStep : settings.OffsetX;
        double deltaFt = UnitUtils.ConvertToInternalUnits(stepVal, lengthUnitId) * sign;

        BoundingBoxXYZ current = view.GetSectionBox();
        XYZ deltaVec = new XYZ(deltaFt, deltaFt, deltaFt);
        XYZ newMin = current.Min - deltaVec;
        XYZ newMax = current.Max + deltaVec;

        // Bounds guard for shrinking: prevent collapsing
        if (newMax.X - newMin.X < 0.2 || newMax.Y - newMin.Y < 0.2 || newMax.Z - newMin.Z < 0.2)
        {
            message = "Cannot shrink the section box further without collapsing it.";
            TaskDialog.Show("BiruBox", message);
            return Result.Cancelled;
        }

        var resized = new BoundingBoxXYZ
        {
            Transform = current.Transform,
            Min = newMin,
            Max = newMax
        };

        using var t = new Transaction(doc, sign > 0 ? "BiruBox Grow Section Box" : "BiruBox Shrink Section Box");
        t.Start();
        view.SetSectionBox(resized);
        SectionBoxService.SetCachedSectionBox(view.Id, resized);
        doc.Regenerate();
        t.Commit();

        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class GrowCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        ResizeSectionBox.Run(commandData, +1, ref message);
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ShrinkCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        ResizeSectionBox.Run(commandData, -1, ref message);
}
