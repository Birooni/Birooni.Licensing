using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using Biruscan.Services;

namespace Biruscan.Commands
{
    /// <summary>
    /// Command to perform a rectangular cut on the point cloud.
    /// User picks two diagonal corners to define a rectangular cutting region.
    /// The cut extends through the full height (Z range) of the point cloud.
    /// Actually cuts the PointCloudInstance — persists across ALL views.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class RectangularCutCommand : IExternalCommand
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
                    TaskDialog.Show("Rectangular Cut", "No point cloud found.\nPlease load a point cloud first.");
                    return Result.Cancelled;
                }

                // Use PickBox to let the user draw a rubber-band rectangle on the screen
                PickedBox pickedBox = uidoc.Selection.PickBox(
                    PickBoxStyle.Directional, 
                    "Draw a rectangular window to cut the point cloud");

                XYZ corner1 = pickedBox.Min;
                XYZ corner2 = pickedBox.Max;

                ClippingService clipService = new ClippingService(doc);
                clipService.CutPointCloudScreenAligned(corner1, corner2, uidoc.ActiveView);

                // Large clouds often keep a stale display cache until the view is refreshed.
                try { doc.Regenerate(); } catch { }
                try { uidoc.RefreshActiveView(); } catch { }

                Biruscan.UI.Views.ClippingManagerWindow.Instance?.RefreshDataPublic();

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Rectangular Cut Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
