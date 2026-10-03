using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Linq;
using Biruscan.Services;
using Biruscan.UI.Views;

namespace Biruscan.Commands
{
    /// <summary>
    /// Command to fit Revit walls to point cloud data automatically or by picking points.
    /// Supports:
    ///   1. Automatic Wall Generation: 1-Click Scan-to-BIM extraction with thickness and corner rectifying
    ///   2. Three-point mode: 2 wall line points + 1 opposite face point for auto-thickness fit
    ///   3. Two-point mode: Pick start and end points for direct wall placement
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class WallFitterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                var pcs = new PointCloudService(doc);
                if (pcs.GetFirstPointCloudInstance() == null)
                {
                    TaskDialog.Show("Wall Fitter", "No point cloud found in the project. Please load a point cloud first.");
                    return Result.Cancelled;
                }

                // Show Wall Fitter Options Window
                WallFitterWindow dlg = new WallFitterWindow(doc);
                try
                {
                    new System.Windows.Interop.WindowInteropHelper(dlg).Owner = commandData.Application.MainWindowHandle;
                }
                catch { }

                if (dlg.ShowDialog() != true)
                {
                    return Result.Cancelled;
                }

                Level targetLevel = dlg.SelectedLevel;
                WallType targetWallType = dlg.SelectedWallType;
                double wallHeight = dlg.WallHeight;
                bool orthoSnap = dlg.OrthoSnap;

                switch (dlg.SelectedFittingMode)
                {
                    case WallFittingMode.Auto:
                        return ExecuteAutoWallGeneration(doc, targetLevel, targetWallType, wallHeight, dlg.MinWallLength, orthoSnap);

                    case WallFittingMode.ThreePoint:
                        return FitWallThreePoint(uidoc, doc, targetLevel, targetWallType, wallHeight, orthoSnap);

                    case WallFittingMode.TwoPoint:
                        return FitWallTwoPoint(uidoc, doc, targetLevel, targetWallType, wallHeight, orthoSnap);

                    default:
                        return Result.Cancelled;
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Wall Fitter Error", ex.Message);
                return Result.Failed;
            }
        }

        private Result ExecuteAutoWallGeneration(Document doc, Level level, WallType wallType, double wallHeight, double minLength, bool orthoSnap)
        {
            var autoService = new AutoWallDetectionService(doc);
            var options = new AutoWallOptions
            {
                TargetLevel = level,
                OverrideWallType = wallType,
                WallHeight = wallHeight,
                MinWallLengthFt = minLength,
                OrthoSnap = orthoSnap,
                UseActiveRoiOnly = true
            };

            var result = autoService.AutoExtractAndCreateWalls(options);
            if (result.Success)
            {
                TaskDialog.Show("Wall Fitter — Success", result.Message);
                return Result.Succeeded;
            }
            else
            {
                TaskDialog.Show("Wall Fitter — Automatic Extraction", result.Message);
                return Result.Failed;
            }
        }

        private Result FitWallThreePoint(UIDocument uidoc, Document doc, Level level, WallType wallType, double wallHeight, bool orthoSnap)
        {
            Reference ref1 = uidoc.Selection.PickObject(
                ObjectType.PointOnElement,
                new PointCloudSelectionFilter(),
                "Pick the FIRST point of the wall face on the point cloud");

            Reference ref2 = uidoc.Selection.PickObject(
                ObjectType.PointOnElement,
                new PointCloudSelectionFilter(),
                "Pick the SECOND point of the wall face on the point cloud");

            Reference ref3 = uidoc.Selection.PickObject(
                ObjectType.PointOnElement,
                new PointCloudSelectionFilter(),
                "Pick a point on the OPPOSITE wall face (for auto-thickness detection)");

            XYZ p1 = ref1.GlobalPoint;
            XYZ p2 = ref2.GlobalPoint;
            XYZ facePoint = ref3.GlobalPoint;

            if (level == null)
            {
                level = FindNearestLevel(doc, (p1.Z + p2.Z) / 2.0);
            }

            FitterService fitter = new FitterService(doc);
            var result = fitter.FitWallByRegion(p1, p2, facePoint, level, wallHeight, wallType, orthoSnap);

            if (result.Success)
            {
                TaskDialog.Show("Wall Fitter", result.Message);
                return Result.Succeeded;
            }
            else
            {
                TaskDialog.Show("Wall Fitter — Failed", result.Message);
                return Result.Failed;
            }
        }

        private Result FitWallTwoPoint(UIDocument uidoc, Document doc, Level level, WallType wallType, double wallHeight, bool orthoSnap)
        {
            Reference ref1 = uidoc.Selection.PickObject(
                ObjectType.PointOnElement,
                new PointCloudSelectionFilter(),
                "Pick the START point of the wall on the point cloud");

            Reference ref2 = uidoc.Selection.PickObject(
                ObjectType.PointOnElement,
                new PointCloudSelectionFilter(),
                "Pick the END point of the wall on the point cloud");

            XYZ start = ref1.GlobalPoint;
            XYZ end = ref2.GlobalPoint;

            if (level == null)
            {
                level = FindNearestLevel(doc, (start.Z + end.Z) / 2.0);
            }

            FitterService fitter = new FitterService(doc);
            var result = fitter.FitWall(start, end, level.Elevation, level.Elevation + wallHeight, level, wallType, orthoSnap);

            if (result.Success)
            {
                TaskDialog.Show("Wall Fitter", result.Message);
                return Result.Succeeded;
            }
            else
            {
                TaskDialog.Show("Wall Fitter — Failed", result.Message);
                return Result.Failed;
            }
        }

        private Level FindNearestLevel(Document doc, double elevation)
        {
            Level nearest = null;
            double minDist = double.MaxValue;

            foreach (Level level in new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>())
            {
                double dist = Math.Abs(level.Elevation - elevation);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = level;
                }
            }

            if (nearest == null || minDist > 5.0)
            {
                using (Transaction t = new Transaction(doc, "Create Level"))
                {
                    t.Start();
                    nearest = Level.Create(doc, elevation);
                    t.Commit();
                }
            }

            return nearest;
        }
    }
}
