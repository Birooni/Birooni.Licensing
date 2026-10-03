using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Linq;
using Biruscan.Services;
using Biruscan.Models;
using System.Collections.Generic;

namespace Biruscan.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class OpenClippingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Open Clipping State",
                    Filter = "Biruscan Clipping (*.bclip)|*.bclip|JSON (*.json)|*.json|All files (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return Result.Cancelled;

                string json = System.IO.File.ReadAllText(dlg.FileName);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<List<ClippingGroup>>(json);
                if (loaded == null || loaded.Count == 0)
                {
                    TaskDialog.Show("Open Clipping", "The file contained no clipping groups.");
                    return Result.Cancelled;
                }

                ClippingService clipService = new ClippingService(doc);
                var rawGroups = clipService.GetClippingGroups();
                rawGroups.Clear();
                foreach (var g in loaded)
                {
                    if (g.Clippings != null)
                        foreach (var c in g.Clippings) c.RestoreGeometry(); // rebuild planes / slice box
                    rawGroups.Add(g);
                }

                // Make sure exactly one group is active so its cuts get applied.
                if (!rawGroups.Any(g => g.IsActive) && rawGroups.Count > 0)
                    rawGroups[0].IsActive = true;

                // Apply active group
                clipService.RebuildCropFromActiveGroup();

                // Refresh UI if open
                Biruscan.UI.Views.ClippingManagerWindow.Instance?.RefreshDataPublic();

                int cutsLoaded = loaded.Sum(g => g.Clippings != null ? g.Clippings.Count : 0);
                TaskDialog.Show("Open Clipping", $"Loaded {loaded.Count} group(s) and {cutsLoaded} clipping(s).");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Open Clipping Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
