using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Linq;
using Biruscan.Services;

namespace Biruscan.Commands
{
    /// <summary>
    /// Command to fit a Revit window to point cloud data.
    /// Replicates the "Window Fitter" functionality from CloudWorx for Revit 1.0.2.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class WindowFitterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // Step 1: Pick a point on the point cloud where the window should go
                Reference pointRef = uidoc.Selection.PickObject(
                    ObjectType.PointOnElement,
                    new PointCloudSelectionFilter(),
                    "Pick a point on the point cloud where the window should be placed");

                XYZ insertionPoint = pointRef.GlobalPoint;

                // Step 2: Find the nearest wall to host the window
                Wall nearestWall = FindNearestWall(doc, insertionPoint);

                if (nearestWall == null)
                {
                    TaskDialog.Show("Window Fitter",
                        "No wall found near the picked point.\n" +
                        "Please use the Wall Fitter first to create a wall from the point cloud.");
                    return Result.Failed;
                }

                FitterService fitter = new FitterService(doc);
                var result = fitter.FitWindow(insertionPoint, nearestWall);

                if (result.Success)
                {
                    TaskDialog.Show("Window Fitter", result.Message);
                    return Result.Succeeded;
                }
                else
                {
                    TaskDialog.Show("Window Fitter — Failed", result.Message);
                    return Result.Failed;
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Window Fitter Error", ex.Message);
                return Result.Failed;
            }
        }

        private Wall FindNearestWall(Document doc, XYZ point)
        {
            Wall nearest = null;
            double minDist = double.MaxValue;

            foreach (Wall wall in new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>())
            {
                LocationCurve locCurve = wall.Location as LocationCurve;
                if (locCurve?.Curve is Line wallLine)
                {
                    double dist = wallLine.Distance(point);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearest = wall;
                    }
                }
            }

            // Only return if wall is within reasonable distance (5 feet)
            return minDist < 5.0 ? nearest : null;
        }
    }
}
