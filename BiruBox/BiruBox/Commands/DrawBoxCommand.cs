using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BiruBox.Core;

namespace BiruBox.Commands;

public sealed class TwoDViewAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
    {
        View? activeView = applicationData.ActiveUIDocument?.ActiveView;
        if (activeView is null || activeView.IsTemplate)
            return false;

        return Is2DView(activeView);
    }

    public static bool Is2DView(View view)
    {
        if (view.IsTemplate)
            return false;

        return view is ViewPlan ||
               view.ViewType == ViewType.FloorPlan ||
               view.ViewType == ViewType.CeilingPlan ||
               view.ViewType == ViewType.EngineeringPlan ||
               view.ViewType == ViewType.AreaPlan ||
               view.ViewType == ViewType.Section ||
               view.ViewType == ViewType.Elevation ||
               view.ViewType == ViewType.Detail;
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class DrawBoxCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!LicensingManager.EnsureLicense())
        {
            return Result.Cancelled;
        }

        UIApplication uiapp = commandData.Application;
        UIDocument? uidoc = uiapp.ActiveUIDocument;
        if (uidoc is null)
        {
            message = "Open a project first.";
            return Result.Failed;
        }

        View activeView = uidoc.ActiveView;
        if (!TwoDViewAvailability.Is2DView(activeView))
        {
            TaskDialog.Show("BiruBox", "Draw Box only works in 2D views (Floor Plans, Ceiling Plans, Sections, and Elevations).");
            return Result.Cancelled;
        }

        PickedBox pickedBox;
        try
        {
            pickedBox = uidoc.Selection.PickBox(PickBoxStyle.Directional, "Drag a box to define the section box area");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }

        Document doc = uidoc.Document;
        try
        {
            BoundingBoxXYZ box = DrawBoxService.BuildBox(doc, activeView, pickedBox);
            var settings = SettingsStore.Load();
            var lengthUnitId = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
            settings.EnsureUnitDefaults(lengthUnitId);

            var default3DViewName = doc.IsWorkshared ? "{3D - " + doc.Application.Username + "}" : "{3D}";
            if (string.IsNullOrWhiteSpace(settings.TargetViewName))
            {
                settings.TargetViewName = default3DViewName;
            }

            if (!SectionBoxService.TryApplySectionBox(uidoc, box, settings, default3DViewName, out var targetView, out var error))
            {
                TaskDialog.Show("BiruBox", error);
                message = error;
                return Result.Failed;
            }

            if (targetView != null)
            {
                SectionBoxService.ActivateAndZoom(uidoc, targetView);
            }

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("BiruBox", ex.Message);
            return Result.Failed;
        }
    }
}
