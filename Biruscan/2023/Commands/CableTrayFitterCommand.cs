using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;
using Biruscan.Services;

namespace Biruscan.Commands
{
    /// <summary>
    /// Fit a cable tray from a pick-box over the point cloud (same workflow as Duct Fitter).
    /// Width/height come from the scanned rectangle; type comes from Fitter Settings.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CableTrayFitterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View view = uidoc.ActiveView;

            try
            {
                if (!(view is View3D v3d) || v3d.IsPerspective)
                {
                    TaskDialog.Show("Cable Tray Fitter",
                        "Cable Tray Fitter requires an orthographic 3D view. Open a 3D view that shows the tray run.");
                    return Result.Cancelled;
                }

                PointCloudService pcs = new PointCloudService(doc);
                if (pcs.GetFirstPointCloudInstance() == null)
                {
                    TaskDialog.Show("Cable Tray Fitter", "No point cloud found. Load a point cloud first.");
                    return Result.Cancelled;
                }

                bool fittedAny = false;
                int count = 0;

                while (true)
                {
                    try
                    {
                        PickedBox box = uidoc.Selection.PickBox(PickBoxStyle.Enclosing,
                            "Draw a rectangle over a straight length of cable tray (Press ESC to finish)");

                        string rayDiag;
                        XYZ rayHit = DuctFitterCommand.RayHitOnCloud(view, box.Min, box.Max, out rayDiag);
                        XYZ anchor = rayHit ?? (box.Min + box.Max).Multiply(0.5);

                        string diag;
                        List<XYZ> worldPts = pcs.GetPointsInScreenRect(view, box.Min, box.Max, 200000, out diag, anchor);
                        if (worldPts.Count < 8)
                        {
                            TaskDialog.Show("Cable Tray Fitter",
                                $"Only {worldPts.Count} points found inside the rectangle. Draw a tighter box along the tray.");
                            continue;
                        }

                        worldPts = DuctFitterCommand.NearestSurface(worldPts, view, box.Min, box.Max, out _, minBandFt: 3.5);
                        if (worldPts.Count < 8)
                        {
                            TaskDialog.Show("Cable Tray Fitter",
                                "Not enough points after depth filtering. Draw a tighter rectangle over one tray.");
                            continue;
                        }

                        var fit = DuctFitterCommand.FitRectangularRun(worldPts, view, DuctFitterCommand.RectFitKind.CableTray);
                        if (!fit.Success)
                        {
                            TaskDialog.Show("Cable Tray Fitter", "Fitting failed: " + fit.Message);
                            continue;
                        }

                        double widthInches = fit.WidthInches;
                        double heightInches = fit.HeightInches;

                        XYZ start = fit.Start;
                        XYZ end = fit.End;
                        if (Math.Abs(start.X) > 30000 || Math.Abs(start.Y) > 30000 || Math.Abs(end.X) > 30000)
                        {
                            TaskDialog.Show("Cable Tray Fitter", "Fitted centreline is out of Revit design limits.");
                            continue;
                        }

                        Level level = new FilteredElementCollector(doc)
                            .OfClass(typeof(Level)).Cast<Level>()
                            .OrderBy(l => Math.Abs(l.Elevation - start.Z)).FirstOrDefault();
                        if (level == null)
                        {
                            TaskDialog.Show("Cable Tray Fitter", "No level found. Create a level first.");
                            return Result.Failed;
                        }

                        var fitter = new FitterService(doc);
                        var result = fitter.FitCableTray(start, end, widthInches / 12.0, heightInches / 12.0, level);
                        if (result.Success)
                        {
                            fittedAny = true;
                            count++;
                            try { Services.Ai.AutoTrainingManager.Instance.RecordFittedElement(doc, result.CreatedElementId, "Cable Tray", "Cable Tray Fitter", worldPts); } catch { }
                        }
                        else
                        {
                            TaskDialog.Show("Cable Tray Fitter — Failed", result.Message);
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        break;
                    }
                }

                return fittedAny ? Result.Succeeded : Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Cable Tray Fitter Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
