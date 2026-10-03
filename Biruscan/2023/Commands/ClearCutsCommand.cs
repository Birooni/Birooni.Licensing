using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using Biruscan.Services;

namespace Biruscan.Commands
{
    /// <summary>
    /// Command to clear ALL cuts/slices on the point cloud and restore the full cloud.
    /// Removes the native isolate filter, resets crop boxes and section boxes, clears the
    /// in-memory clipping groups, and erases the persisted clipping state.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ClearCutsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                PointCloudService pcService = new PointCloudService(doc);
                if (pcService.GetFirstPointCloudInstance() == null)
                {
                    TaskDialog.Show("Clear Cuts", "No point cloud found.\nPlease load a point cloud first.");
                    return Result.Cancelled;
                }

                ClippingService clipService = new ClippingService(doc);
                clipService.ClearAllCuts();
                // Remove leftover clone instances from older polygon cuts (untagged duplicates).
                int purged = clipService.PurgeDuplicatePointCloudInstances();

                try { doc.Regenerate(); } catch { }
                try { uidoc.RefreshActiveView(); } catch { }

                // Refresh the Clipping Manager dialog if it is open.
                Biruscan.UI.Views.ClippingManagerWindow.Instance?.RefreshDataPublic();

                if (purged > 0)
                {
                    TaskDialog.Show("Clear Cuts",
                        $"Cuts cleared.\nRemoved {purged} duplicate point cloud instance(s) left by older polygon clips.");
                }

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Clear Cuts Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
