using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Linq;
using Biruscan.Services;

namespace Biruscan.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SaveClippingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                ClippingService clipService = new ClippingService(doc);
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
                    string json = System.Text.Json.JsonSerializer.Serialize(groups,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    System.IO.File.WriteAllText(dlg.FileName, json);
                    int cuts = groups.Sum(g => g.Clippings != null ? g.Clippings.Count : 0);
                    TaskDialog.Show("Save Clipping", $"Saved {groups.Count} group(s) and {cuts} clipping(s) to:\n{dlg.FileName}");
                }

                return Result.Succeeded;
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
