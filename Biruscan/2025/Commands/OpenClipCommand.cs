using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Biruscan.Models;
using Biruscan.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Biruscan.Commands
{
    /// <summary>
    /// Quick Open command: Loads clipping groups from a .bclip file and applies active cuts.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class OpenClipCommand : IExternalCommand
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

                string json = File.ReadAllText(dlg.FileName);
                var loaded = JsonSerializer.Deserialize<List<ClippingGroup>>(json);
                if (loaded == null || loaded.Count == 0)
                {
                    TaskDialog.Show("Open Clipping", "The selected file contained no clipping groups.");
                    return Result.Cancelled;
                }

                var clipService = new ClippingService(doc);
                var rawGroups = clipService.GetClippingGroups();
                rawGroups.Clear();

                foreach (var g in loaded)
                {
                    if (g.Clippings != null)
                    {
                        foreach (var c in g.Clippings) c.RestoreGeometry();
                    }
                    rawGroups.Add(g);
                }

                if (!rawGroups.Any(g => g.IsActive) && rawGroups.Count > 0)
                {
                    rawGroups[0].IsActive = true;
                }

                // Apply active group cuts to the point cloud
                clipService.RebuildCropFromActiveGroup();

                int cutsLoaded = loaded.Sum(g => g.Clippings != null ? g.Clippings.Count : 0);
                TaskDialog.Show("Open Clipping", $"Successfully loaded {loaded.Count} group(s) and {cutsLoaded} clipping(s).\nActive clipping applied to point cloud.");
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