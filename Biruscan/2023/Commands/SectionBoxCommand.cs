using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using Biruscan.Services;

namespace Biruscan.Commands
{
    /// <summary>
    /// Command to enable or adjust the section box around the point cloud.
    /// Replicates the "Section Box" functionality from CloudWorx for Revit 1.0.2.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SectionBoxCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                View activeView = doc.ActiveView;
                if (!(activeView is View3D view3d))
                {
                    TaskDialog.Show("Section Box", "Please switch to a 3D view to use the Section Box tool.");
                    return Result.Cancelled;
                }

                ViewService viewService = new ViewService(doc);
                PointCloudService pcService = new PointCloudService(doc);

                BoundingBoxXYZ cloudBounds = pcService.GetPointCloudBoundingBox();
                if (cloudBounds == null)
                {
                    TaskDialog.Show("Section Box", "No point cloud found to fit the section box.");
                    return Result.Cancelled;
                }

                ClippingService clippingService = new ClippingService(doc);
                BoundingBoxXYZ targetBounds = clippingService.GetActiveBoundingBox(cloudBounds) ?? cloudBounds;

                viewService.EnableSectionBox(view3d, targetBounds);
                try { doc.Regenerate(); } catch { }
                try { uidoc.RefreshActiveView(); } catch { }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Section Box Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
