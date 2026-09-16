using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BiruBox.Core;
using BiruBox.UI;

namespace BiruBox.Commands;

public static class CommandSupport
{
    public static Result RunAutoSection(ExternalCommandData commandData, bool showDialog, ref string message)
    {
        if (!LicensingManager.EnsureLicense())
        {
            return Result.Cancelled;
        }

        try
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null) return Result.Cancelled;

            var doc = uiDoc.Document;
            var sourceViewId = uiDoc.ActiveView.Id;
            var preSelectedIds = uiDoc.Selection.GetElementIds();
            IList<Reference>? picks = null;

            if (preSelectedIds == null || preSelectedIds.Count == 0)
            {
                try
                {
                    picks = uiDoc.Selection.PickObjects(ObjectType.Element, "Select elements for Auto Section Box");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }

                if (picks == null || picks.Count == 0)
                {
                    TaskDialog.Show("BiruBox", "No elements selected.");
                    return Result.Cancelled;
                }
            }

            var units = doc.GetUnits();
            var formatOptions = units.GetFormatOptions(SpecTypeId.Length);
            var lengthUnitId = formatOptions.GetUnitTypeId();
            var unitLabel = LabelUtils.GetLabelForUnit(lengthUnitId);

            var default3DViewName = doc.IsWorkshared ? "{3D - " + doc.Application.Username + "}" : "{3D}";
            var settings = SettingsStore.Load();

            if (!SessionManager.IsDocumentInitialized(doc))
            {
                settings.TargetViewName = string.Empty;
                settings.OffsetX = -1;
                settings.OffsetY = -1;
                settings.OffsetZ = -1;
                SessionManager.MarkDocumentInitialized(doc);
            }

            settings.EnsureUnitDefaults(lengthUnitId);

            if (string.IsNullOrWhiteSpace(settings.TargetViewName))
            {
                settings.TargetViewName = default3DViewName;
            }

            if (showDialog)
            {
                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .Where(v => !v.IsTemplate)
                    .Select(v => v.Name)
                    .OrderBy(n => n)
                    .ToList();

                var templates = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.IsTemplate && v.ViewType == ViewType.ThreeD)
                    .Select(v => v.Name)
                    .OrderBy(n => n)
                    .ToList();

                var dlg = new AutoSectionDialog(commandData.Application, settings, unitLabel, views, templates);
                dlg.ShowDialog();
                if (!dlg.Confirmed)
                {
                    return Result.Cancelled;
                }
            }

            SettingsStore.Save(settings);

            if (!SectionBoxService.TryCreateOrApplySectionBox(uiDoc, preSelectedIds, picks, settings, lengthUnitId, default3DViewName, out var targetView, out var error))
            {
                TaskDialog.Show("BiruBox", error);
                message = error;
                return Result.Failed;
            }

            if (targetView == null)
            {
                return Result.Failed;
            }

            var elementsToSelect = new List<ElementId>();
            if (preSelectedIds != null) elementsToSelect.AddRange(preSelectedIds);
            if (picks != null) elementsToSelect.AddRange(picks.Select(p => p.ElementId).Where(id => id != ElementId.InvalidElementId));

            SectionBoxService.ActivateAndZoom(uiDoc, targetView, elementsToSelect);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("BiruBox Error", ex.Message + "\n\n" + ex.StackTrace);
            return Result.Failed;
        }
    }
}
