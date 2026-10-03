using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Biruscan.Models;
using Biruscan.Services;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Biruscan.Commands
{
    /// <summary>
    /// Quick Save command: Exports all clipping groups and cuts to a .bclip file.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SaveClipCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                var clipService = new ClippingService(doc);
                var groups = clipService.GetClippingGroups();

                if (groups == null || groups.Count == 0)
                {
                    TaskDialog.Show("Save Clipping", "There are no clipping groups to save.");
                    return Result.Cancelled;
                }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save Clipping State",
                    Filter = "Biruscan Clipping (*.bclip)|*.bclip|JSON (*.json)|*.json",
                    FileName = "clipping_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bclip"
                };

                if (dlg.ShowDialog() == true)
                {
                    string json = JsonSerializer.Serialize(groups, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(dlg.FileName, json);
                    int cuts = groups.Sum(g => g.Clippings != null ? g.Clippings.Count : 0);
                    TaskDialog.Show("Save Clipping", $"Successfully saved {groups.Count} group(s) and {cuts} clipping(s) to:\n{dlg.FileName}");
                    return Result.Succeeded;
                }

                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Save Clipping Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}