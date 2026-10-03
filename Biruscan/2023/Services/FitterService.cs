using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Plumbing;
using System;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using System.Collections.Generic;
using System.Linq;
using Biruscan.Models;

namespace Biruscan.Services
{
    /// <summary>
    /// Service for fitting Revit elements to point cloud data.
    /// Implements Wall, Pipe, Steel, HVAC, Window, and Cable Tray fitters.
    /// </summary>
    public class FitterService
    {
        private readonly Document _doc;

        public FitterService(Document doc)
        {
            _doc = doc;
        }

        #region Wall Fitter

        /// <summary>
        /// Fits a Revit wall between two picked points using the point cloud as reference.
        /// </summary>
        public FitterResult FitWall(XYZ start, XYZ end, double bottomElevation, double topElevation, Level level, WallType overrideWallType = null, bool orthoSnap = true)
        {
            try
            {
                if (start.DistanceTo(end) < 0.2)
                    return FitterResult.Fail("Start and end points are too close.");

                // Ortho-snap if requested
                if (orthoSnap)
                {
                    XYZ wallDir = (end - start).Normalize();
                    double angle = Math.Atan2(wallDir.Y, wallDir.X);
                    double snapped = Math.Round(angle / (Math.PI / 2.0)) * (Math.PI / 2.0);
                    if (Math.Abs(angle - snapped) <= 5.0 * Math.PI / 180.0)
                    {
                        XYZ snappedDir = new XYZ(Math.Cos(snapped), Math.Sin(snapped), 0);
                        double len = start.DistanceTo(end);
                        end = start + snappedDir.Multiply(len);
                    }
                }

                // Project to level elevation
                XYZ ptStart = new XYZ(start.X, start.Y, level.Elevation);
                XYZ ptEnd = new XYZ(end.X, end.Y, level.Elevation);

                WallType wallType = overrideWallType ?? new FilteredElementCollector(_doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Kind == WallKind.Basic);

                if (wallType == null)
                    return FitterResult.Fail("No basic wall type found in the document.");

                double wallHeight = topElevation - bottomElevation;
                if (wallHeight < 1.0) wallHeight = 10.0;

                Line wallLine = Line.CreateBound(ptStart, ptEnd);

                using (Transaction t = new Transaction(_doc, "Fit Wall from Point Cloud"))
                {
                    t.Start();
                    Wall wall = Wall.Create(_doc, wallLine, wallType.Id, level.Id, wallHeight, 0, false, false);
                    if (wall != null)
                    {
                        t.Commit();
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, wall.Id, "Wall", "Wall Fitter"); } catch { }
                        return FitterResult.OK(wall.Id, "Wall",
                            $"Wall created: {wallType.Name}, Height={wallHeight:F1}ft");
                    }

                    t.RollBack();
                    return FitterResult.Fail("Wall.Create returned null.");
                }
            }
            catch (Exception ex)
            {
                return FitterResult.Fail("Wall fitting error: " + ex.Message);
            }
        }

        /// <summary>
        /// Fits a wall using 3 picked points on the point cloud (3-point face + thickness mode).
        /// point1 & point2 define the primary wall face line.
        /// facePoint is on the opposite wall face to calculate real thickness.
        /// </summary>
        public FitterResult FitWallByRegion(XYZ point1, XYZ point2, XYZ facePoint, Level level, double wallHeight = 10.0, WallType overrideWallType = null, bool orthoSnap = true)
        {
            try
            {
                if (point1.DistanceTo(point2) < 0.2)
                    return FitterResult.Fail("Point 1 and Point 2 are too close. Pick points further apart along the wall face.");

                // 1. Calculate perpendicular thickness from facePoint to the line (p1 -> p2)
                XYZ dir = (point2 - point1).Normalize();
                XYZ normal = new XYZ(-dir.Y, dir.X, 0);
                double measuredThickness = Math.Abs((facePoint.X - point1.X) * normal.X + (facePoint.Y - point1.Y) * normal.Y);
                if (measuredThickness < 0.1) measuredThickness = 0.667; // fallback to 8" if on same face

                // 2. Centerline is shifted midway between the two faces
                double halfThickness = measuredThickness * 0.5;
                double side = ((facePoint.X - point1.X) * normal.X + (facePoint.Y - point1.Y) * normal.Y) >= 0 ? 1.0 : -1.0;
                XYZ offset = normal.Multiply(side * halfThickness);

                XYZ start = new XYZ(point1.X + offset.X, point1.Y + offset.Y, level.Elevation);
                XYZ end = new XYZ(point2.X + offset.X, point2.Y + offset.Y, level.Elevation);

                // 3. Ortho-snap if requested
                if (orthoSnap)
                {
                    XYZ wallDir = (end - start).Normalize();
                    double angle = Math.Atan2(wallDir.Y, wallDir.X);
                    double snapped = Math.Round(angle / (Math.PI / 2.0)) * (Math.PI / 2.0);
                    if (Math.Abs(angle - snapped) <= 5.0 * Math.PI / 180.0)
                    {
                        XYZ snappedDir = new XYZ(Math.Cos(snapped), Math.Sin(snapped), 0);
                        double len = start.DistanceTo(end);
                        end = start + snappedDir.Multiply(len);
                    }
                }

                // 4. Match wall type
                WallType wallType = overrideWallType;
                if (wallType == null)
                {
                    var basicTypes = new FilteredElementCollector(_doc)
                        .OfClass(typeof(WallType))
                        .Cast<WallType>()
                        .Where(wt => wt.Kind == WallKind.Basic)
                        .ToList();

                    if (basicTypes.Count == 0)
                        return FitterResult.Fail("No basic wall type found in the document.");

                    wallType = basicTypes.OrderBy(wt => Math.Abs(wt.Width - measuredThickness)).FirstOrDefault();
                }

                Line wallLine = Line.CreateBound(start, end);

                using (Transaction t = new Transaction(_doc, "Fit Wall from Points"))
                {
                    t.Start();
                    Wall wall = Wall.Create(_doc, wallLine, wallType.Id, level.Id, wallHeight, 0, false, false);
                    if (wall != null)
                    {
                        t.Commit();
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, wall.Id, "Wall", "Wall Fitter (3-Point)"); } catch { }
                        return FitterResult.OK(wall.Id, "Wall",
                            $"Wall fitted: {wallType.Name} (Thickness={measuredThickness * 304.8:F0}mm / {measuredThickness * 12:F1}\", Height={wallHeight:F1}ft)");
                    }
                    t.RollBack();
                    return FitterResult.Fail("Wall.Create returned null.");
                }
            }
            catch (Exception ex)
            {
                return FitterResult.Fail("Wall 3-point fit error: " + ex.Message);
            }
        }

        #endregion

        #region Pipe Fitter

        /// <summary>
        /// Fits a Revit pipe between two picked points on the point cloud.
        /// </summary>
        public FitterResult FitPipe(XYZ start, XYZ end, double diameter, Level level)
        {
            try
            {
                if (start.DistanceTo(end) < 0.01)
                    return FitterResult.Fail("Start and end points are too close. Pick two points further apart.");

                var settings = FitterSettings.Instance;

                var allPipeTypes = new FilteredElementCollector(_doc).OfClass(typeof(PipeType)).Cast<PipeType>();
                PipeType pipeType = FitterSettings.MatchType(allPipeTypes, settings.PreferredPipeTypeName)
                    ?? allPipeTypes.FirstOrDefault();

                if (pipeType == null)
                    return FitterResult.Fail("No pipe type found. Load a pipe type (use a Mechanical/Plumbing template) first.");

                // Find a piping system type. NOTE: MEPSystemType is an abstract base
                // class and is rejected by FilteredElementCollector.OfClass at runtime,
                // so we must query the concrete PipingSystemType. Prefer a supply
                // hydronic system, but fall back to ANY available piping system type.
                var allPipingSystems = new FilteredElementCollector(_doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>();
                PipingSystemType systemType = FitterSettings.MatchType(allPipingSystems, settings.PreferredPipingSystemTypeName);
                if (systemType == null)
                    systemType = allPipingSystems.FirstOrDefault(st => st.SystemClassification == MEPSystemClassification.SupplyHydronic);
                if (systemType == null)
                    systemType = allPipingSystems.FirstOrDefault();

                if (systemType == null)
                    return FitterResult.Fail("No piping system type found. Load a pipe system (use a Mechanical/Plumbing template) first.");

                using (Transaction t = new Transaction(_doc, "Fit Pipe from Point Cloud"))
                {
                    t.Start();

                    Pipe pipe = Pipe.Create(_doc, systemType.Id, pipeType.Id, level.Id,
                        start, end);

                    if (pipe != null)
                    {
                        // Set diameter
                        Parameter diamParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (diamParam != null)
                        {
                            if (diamParam.IsReadOnly)
                            {
                                Parameter fallbackParam = pipe.LookupParameter("Diameter") ?? pipe.LookupParameter("Size");
                                if (fallbackParam != null && !fallbackParam.IsReadOnly)
                                    fallbackParam.Set(diameter);
                            }
                            else
                            {
                                diamParam.Set(diameter);
                            }
                        }

                        t.Commit();
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, pipe.Id, "Piping", "Pipe Fitter"); } catch { }
                        return FitterResult.OK(pipe.Id, "Pipe",
                            $"Pipe created: Dia={diameter * 12.0:F2}\" ({diameter * 304.8:F0}mm), Length={start.DistanceTo(end):F1}ft");
                    }

                    t.RollBack();
                    return FitterResult.Fail("Pipe.Create returned null.");
                }
            }
            catch (Exception ex)
            {
                return FitterResult.Fail("Pipe fitting error: " + ex.Message);
            }
        }

        #endregion

        #region Steel Fitter

        /// <summary>
        /// Fits structural steel framing (beam) between two points from the point cloud.
        /// </summary>
        public FitterResult FitSteel(XYZ start, XYZ end, Level level)
        {
            try
            {
                if (start == null || end == null || start.DistanceTo(end) < 0.01)
                    return FitterResult.Fail("Start and end points are too close. Pick two points further apart.");
                if (level == null)
                    return FitterResult.Fail("No level found. Create a level first.");

                // Find a structural framing type
                FamilySymbol beamSymbol = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Family.Name.Contains("Beam") ||
                                          fs.Family.Name.Contains("W-Wide") ||
                                          fs.Family.Name.Contains("HSS"));

                if (beamSymbol == null)
                    return FitterResult.Fail("No structural framing family found.");

                if (!beamSymbol.IsActive)
                {
                    using (Transaction t = new Transaction(_doc, "Activate Beam Symbol"))
                    {
                        t.Start();
                        beamSymbol.Activate();
                        t.Commit();
                    }
                }

                Line beamLine = Line.CreateBound(start, end);

                using (Transaction t = new Transaction(_doc, "Fit Steel from Point Cloud"))
                {
                    t.Start();

                    FamilyInstance beam = _doc.Create.NewFamilyInstance(
                        beamLine, beamSymbol, level, StructuralType.Beam);

                    if (beam != null)
                    {
                        t.Commit();
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, beam.Id, "Steel", "Steel Fitter"); } catch { }
                        return FitterResult.OK(beam.Id, "Steel",
                            $"Steel beam created: {beamSymbol.Name}");
                    }

                    t.RollBack();
                    return FitterResult.Fail("Steel beam creation failed.");
                }
            }
            catch (Exception ex)
            {
                return FitterResult.Fail("Steel fitting error: " + ex.Message);
            }
        }

        #endregion

        #region HVAC Fitter

        /// <summary>
        /// Fits a rectangular duct between two points from the point cloud.
        /// </summary>
        public FitterResult FitDuct(XYZ start, XYZ end, double width, double height, Level level)
        {
            try
            {
                if (start.DistanceTo(end) < 0.01)
                    return FitterResult.Fail("Start and end points are too close. Pick two points further apart.");

                var settings = FitterSettings.Instance;

                var allDuctTypes = new FilteredElementCollector(_doc)
                    .OfClass(typeof(DuctType))
                    .Cast<DuctType>()
                    .ToList();

                if (allDuctTypes.Count == 0)
                    return FitterResult.Fail("No duct type found. Load a duct type (use a Mechanical template) first.");

                DuctType ductType = FitterSettings.MatchType(allDuctTypes, settings.PreferredDuctTypeName);
                if (ductType == null)
                {
                    ductType = allDuctTypes.FirstOrDefault(dt => dt.Shape == ConnectorProfileType.Rectangular)
                            ?? allDuctTypes.FirstOrDefault(dt => {
                                string n = (dt.Name ?? "").ToLowerInvariant();
                                return n.Contains("rectangular") || n.Contains("miter") || n.Contains("radius");
                            })
                            ?? allDuctTypes.FirstOrDefault();
                }

                // NOTE: MEPSystemType is an abstract base class and is rejected by
                // FilteredElementCollector.OfClass at runtime, so we must query the
                // concrete MechanicalSystemType.
                var allMechSystems = new FilteredElementCollector(_doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>();
                MechanicalSystemType systemType = FitterSettings.MatchType(allMechSystems, settings.PreferredDuctSystemTypeName);
                if (systemType == null)
                    systemType = allMechSystems.FirstOrDefault(st => st.SystemClassification == MEPSystemClassification.SupplyAir
                                       || st.SystemClassification == MEPSystemClassification.ReturnAir);
                                       
                if (systemType == null)
                    systemType = allMechSystems.FirstOrDefault();

                if (systemType == null)
                    return FitterResult.Fail("No mechanical system type found. Load a duct system (use a Mechanical template) first.");

                using (Transaction t = new Transaction(_doc, "Fit Duct from Point Cloud"))
                {
                    t.Start();

                    Duct duct = Duct.Create(_doc, systemType.Id, ductType.Id, level.Id,
                        start, end);

                    if (duct != null)
                    {
                        Parameter widthParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                        Parameter heightParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

                        if (widthParam != null && !widthParam.IsReadOnly) widthParam.Set(width);
                        if (heightParam != null && !heightParam.IsReadOnly) heightParam.Set(height);

                        t.Commit();
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, duct.Id, "Duct", "Duct Fitter"); } catch { }
                        return FitterResult.OK(duct.Id, "Duct",
                            $"Duct created: {width * 12.0:F0}\"x{height * 12.0:F0}\", Length={start.DistanceTo(end):F1}ft");
                    }

                    t.RollBack();
                    return FitterResult.Fail("Duct.Create returned null.");
                }
            }
            catch (Exception ex)
            {
                return FitterResult.Fail("Duct fitting error: " + ex.Message);
            }
        }

        #endregion

        #region Window Fitter

        /// <summary>
        /// Fits a window into a host wall at a specified point from the point cloud.
        /// </summary>
        public FitterResult FitWindow(XYZ insertionPoint, Wall hostWall)
        {
            try
            {
                FamilySymbol windowSymbol = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();

                if (windowSymbol == null)
                    return FitterResult.Fail("No window family found in the document.");

                if (!windowSymbol.IsActive)
                {
                    using (Transaction t = new Transaction(_doc, "Activate Window Symbol"))
                    {
                        t.Start();
                        windowSymbol.Activate();
                        t.Commit();
                    }
                }

                using (Transaction t = new Transaction(_doc, "Fit Window from Point Cloud"))
                {
                    t.Start();

                    FamilyInstance window = _doc.Create.NewFamilyInstance(
                        insertionPoint, windowSymbol, hostWall, StructuralType.NonStructural);

                    if (window != null)
                    {
                        t.Commit();
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, window.Id, "Window", "Window Fitter"); } catch { }
                        return FitterResult.OK(window.Id, "Window",
                            $"Window placed: {windowSymbol.Name}");
                    }

                    t.RollBack();
                    return FitterResult.Fail("Window creation failed.");
                }
            }
            catch (Exception ex)
            {
                return FitterResult.Fail("Window fitting error: " + ex.Message);
            }
        }

        #endregion

        #region Cable Tray Fitter

        /// <summary>
        /// Fits a cable tray between two points from the point cloud.
        /// </summary>
        public FitterResult FitCableTray(XYZ start, XYZ end, double width, double height, Level level)
        {
            try
            {
                if (start.DistanceTo(end) < 0.01)
                    return FitterResult.Fail("Start and end points are too close. Pick two points further apart.");

                var settings = FitterSettings.Instance;

                // Cable tray is a SYSTEM family — it has a CableTrayType, not a loadable
                // FamilySymbol. The previous code required a FamilySymbol in OST_CableTray
                // which almost never exists, so the fitter always failed. Use the
                // CableTrayType directly with CableTray.Create.
                var allTrayTypes = new FilteredElementCollector(_doc).OfClass(typeof(CableTrayType)).Cast<CableTrayType>();
                CableTrayType trayType = FitterSettings.MatchType(allTrayTypes, settings.PreferredCableTrayTypeName)
                    ?? allTrayTypes.FirstOrDefault();

                if (trayType == null)
                    return FitterResult.Fail("No cable tray type found. Define a Cable Tray type (use an Electrical/Systems template) first.");

                using (Transaction t = new Transaction(_doc, "Fit Cable Tray from Point Cloud"))
                {
                    t.Start();

                    // CableTray.Create(Document, cableTrayTypeId, startPoint, endPoint, levelId)
                    CableTray tray = CableTray.Create(_doc, trayType.Id, start, end, level.Id);

                    if (tray != null)
                    {
                        // Apply the requested width/height (values are in feet).
                        Parameter wParam = tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                        Parameter hParam = tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                        if (wParam != null && !wParam.IsReadOnly) wParam.Set(width);
                        if (hParam != null && !hParam.IsReadOnly) hParam.Set(height);

                        t.Commit();
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, tray.Id, "Cable Tray", "Cable Tray Fitter"); } catch { }
                        return FitterResult.OK(tray.Id, "CableTray",
                            $"Cable tray created: Width={width * 12.0:F1}\", Height={height * 12.0:F1}\", Length={start.DistanceTo(end):F1}ft");
                    }

                    t.RollBack();
                    return FitterResult.Fail("CableTray.Create returned null.");
                }
            }
            catch (Exception ex)
            {
                return FitterResult.Fail("Cable tray fitting error: " + ex.Message);
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Calculates the best-fit line through a set of points using least-squares.
        /// </summary>
        public XYZ CalculateBestFitDirection(List<XYZ> points)
        {
            if (points == null || points.Count < 2) return XYZ.BasisX;

            XYZ sum = XYZ.Zero;
            for (int i = 1; i < points.Count; i++)
            {
                sum += (points[i] - points[i - 1]).Normalize();
            }

            return sum.Normalize();
        }

        /// <summary>
        /// Estimates the wall thickness from point cloud points by analyzing
        /// point density perpendicular to the wall direction.
        /// </summary>
        public double EstimateThickness(List<XYZ> points, XYZ wallDirection, XYZ wallOrigin)
        {
            // Project all points onto the normal to the wall and find the spread
            XYZ normal = wallDirection.CrossProduct(XYZ.BasisZ).Normalize();

            double minProj = double.MaxValue;
            double maxProj = double.MinValue;

            foreach (XYZ pt in points)
            {
                double proj = (pt - wallOrigin).DotProduct(normal);
                minProj = Math.Min(minProj, proj);
                maxProj = Math.Max(maxProj, proj);
            }

            return Math.Abs(maxProj - minProj);
        }

        #endregion
    }
}
