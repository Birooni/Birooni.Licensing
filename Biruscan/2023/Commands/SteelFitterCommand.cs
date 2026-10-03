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
    /// Command to fit structural steel framing to point cloud data.
    /// Replicates the "Steel Fitter" functionality from CloudWorx for Revit 1.0.2.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SteelFitterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // Pick start and end points on the point cloud for the steel member
                Reference ref1 = uidoc.Selection.PickObject(
                    ObjectType.PointOnElement,
                    new PointCloudSelectionFilter(),
                    "Pick the START point of the steel member on the point cloud");

                Reference ref2 = uidoc.Selection.PickObject(
                    ObjectType.PointOnElement,
                    new PointCloudSelectionFilter(),
                    "Pick the END point of the steel member on the point cloud");

                XYZ start = ref1.GlobalPoint;
                XYZ end = ref2.GlobalPoint;

                Level level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => Math.Abs(l.Elevation - start.Z))
                    .FirstOrDefault();

                if (level == null)
                {
                    TaskDialog.Show("Steel Fitter", "No level found. Please create a level first.");
                    return Result.Failed;
                }

                FitterService fitter = new FitterService(doc);
                var result = fitter.FitSteel(start, end, level);

                if (result.Success)
                {
                    TaskDialog.Show("Steel Fitter", result.Message);
                    return Result.Succeeded;
                }
                else
                {
                    TaskDialog.Show("Steel Fitter — Failed", result.Message);
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
                TaskDialog.Show("Steel Fitter Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
