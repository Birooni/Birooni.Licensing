using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using Biruscan.Services;

namespace Biruscan.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class HorizontalSliceCommand : IExternalCommand
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
                    TaskDialog.Show("Horizontal Slice", "No point cloud found.\nPlease load a point cloud first.");
                    return Result.Cancelled;
                }

                SketchPlane tempSketchPlane = null;
                SketchPlane originalSketchPlane = uidoc.ActiveView.SketchPlane;

                // Anchor sketch plane at cloud centre (not camera origin) so picks work
                // on large / offset scans — same fix as Polygonal Cut.
                XYZ planeOrigin = uidoc.ActiveView.Origin;
                BoundingBoxXYZ cloudBox = pcService.GetPointCloudBoundingBox();
                if (cloudBox != null)
                    planeOrigin = (cloudBox.Min + cloudBox.Max).Multiply(0.5);

                using (Transaction tSetup = new Transaction(doc, "Setup SketchPlane"))
                {
                    tSetup.Start();
                    Plane plane = Plane.CreateByNormalAndOrigin(uidoc.ActiveView.ViewDirection, planeOrigin);
                    tempSketchPlane = SketchPlane.Create(doc, plane);
                    uidoc.ActiveView.SketchPlane = tempSketchPlane;
                    tSetup.Commit();
                }

                try
                {
                    XYZ p1 = uidoc.Selection.PickPoint(ObjectSnapTypes.None, "Pick the first point of the horizontal slice");
                    XYZ p2 = uidoc.Selection.PickPoint(ObjectSnapTypes.None, "Pick the second point to define the thickness of the horizontal slice");

                    ClippingService clipService = new ClippingService(doc);
                    clipService.ApplyScreenHorizontalSlice(p1, p2, uidoc.ActiveView);

                    try { doc.Regenerate(); } catch { }
                    try { uidoc.RefreshActiveView(); } catch { }

                    Biruscan.UI.Views.ClippingManagerWindow.Instance?.RefreshDataPublic();

                    return Result.Succeeded;
                }
                finally
                {
                    using (Transaction tCleanup = new Transaction(doc, "Cleanup SketchPlane"))
                    {
                        tCleanup.Start();
                        // Restore original sketch plane if it existed
                        if (originalSketchPlane != null)
                        {
                            uidoc.ActiveView.SketchPlane = originalSketchPlane;
                        }
                        if (tempSketchPlane != null) doc.Delete(tempSketchPlane.Id);
                        tCleanup.Commit();
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Horizontal Slice Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
