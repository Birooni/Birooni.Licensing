using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Biruscan.Commands
{
    public enum ElbowDirection
    {
        Up,
        Down,
        Left,
        Right,
        Up45,
        Down45,
        Left45,
        Right45
    }

    /// <summary>
    /// Shared execution engine for directional elbow placement (Up, Down, Left, Right).
    /// </summary>
    public static class DirectionalElbowEngine
    {
        public static Result Execute(ExternalCommandData commandData, ElbowDirection direction, ref string message)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                var selectionIds = uidoc.Selection.GetElementIds();
                MEPCurve mepCurve = null;
                XYZ clickPoint = null;

                if (selectionIds != null && selectionIds.Count > 0)
                {
                    mepCurve = selectionIds.Select(id => doc.GetElement(id)).OfType<MEPCurve>().FirstOrDefault();
                }

                if (mepCurve == null)
                {
                    var filter = new MepCurveSelectionFilter();
                    Reference r;
                    try
                    {
                        r = uidoc.Selection.PickObject(
                            ObjectType.Element,
                            filter,
                            $"Pick open-ended Pipe, Duct, or Conduit to add Elbow {direction}"
                        );
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }

                    if (r != null)
                    {
                        mepCurve = doc.GetElement(r.ElementId) as MEPCurve;
                        clickPoint = r.GlobalPoint;
                    }
                }

                if (mepCurve == null)
                {
                    TaskDialog.Show("Elbow Command", "No valid MEP curve (Pipe, Duct, Conduit) selected.");
                    return Result.Cancelled;
                }

                // Find the appropriate open connector
                var connectors = mepCurve.ConnectorManager?.Connectors.Cast<Connector>()
                    .Where(c => c.ConnectorType != ConnectorType.Logical)
                    .ToList();

                if (connectors == null || connectors.Count == 0)
                {
                    TaskDialog.Show("Elbow Command", "Selected element has no MEP connectors.");
                    return Result.Failed;
                }

                Connector targetConn = null;
                if (clickPoint != null)
                {
                    // Sort by distance to click point, picking open connector
                    var sorted = connectors.OrderBy(c => c.Origin.DistanceTo(clickPoint)).ToList();
                    targetConn = sorted.FirstOrDefault(c => !c.IsConnected) ?? sorted.FirstOrDefault();
                }
                else
                {
                    targetConn = connectors.FirstOrDefault(c => !c.IsConnected) ?? connectors.FirstOrDefault();
                }

                if (targetConn == null)
                {
                    TaskDialog.Show("Elbow Command", "Could not find a connector on the selected element.");
                    return Result.Failed;
                }

                if (targetConn.IsConnected)
                {
                    // Check if other end is unconnected
                    var otherConn = connectors.FirstOrDefault(c => c.Id != targetConn.Id && !c.IsConnected);
                    if (otherConn != null)
                    {
                        targetConn = otherConn;
                    }
                    else
                    {
                        TaskDialog.Show("Elbow Command", "Both ends of the selected MEP element are already connected.");
                        return Result.Failed;
                    }
                }

                // Determine run direction (from element center towards target connector)
                XYZ origin = targetConn.Origin;
                XYZ runDir = XYZ.BasisX;

                if (mepCurve.Location is LocationCurve lc && lc.Curve is Line line)
                {
                    XYZ p0 = line.GetEndPoint(0);
                    XYZ p1 = line.GetEndPoint(1);
                    if (origin.DistanceTo(p1) < origin.DistanceTo(p0))
                        runDir = SafeDir(p1 - p0, XYZ.BasisX);
                    else
                        runDir = SafeDir(p0 - p1, XYZ.BasisX);
                }

                // Compute bend direction vector
                XYZ bendDir = GetBendVector(doc, runDir, direction);

                // Compute standard extension length
                var settings = Biruscan.Models.MepSettings.Instance;
                double stubLength = settings.ElbowExtensionLengthFeet;
                if (mepCurve is Pipe)
                    stubLength = Math.Max(settings.ElbowExtensionLengthFeet, targetConn.Radius * 4.0);
                else if (mepCurve is Duct)
                    stubLength = Math.Max(settings.ElbowExtensionLengthFeet, Math.Max(targetConn.Width, targetConn.Height) * 2.0);

                double connRadius = 0, connWidth = 0, connHeight = 0;
                ConnectorProfileType connShape = ConnectorProfileType.Round;
                CaptureConnectorSize(targetConn, out connRadius, out connWidth, out connHeight, out connShape, out _, out _);

                XYZ endPoint = origin + bendDir * stubLength;
                ElementId levelId = GetLevelId(doc, mepCurve, origin);
                ElementId existId = mepCurve.Id;

                using (Transaction t = new Transaction(doc, $"Place Elbow {direction}"))
                {
                    Biruscan.Services.MepOperationService.AllowNetworkDisconnects(t);
                    t.Start();

                    MEPCurve host = doc.GetElement(existId) as MEPCurve ?? mepCurve;
                    MEPCurve newCurve = TryCreateStub(doc, host, origin, endPoint, levelId,
                        connRadius, connWidth, connHeight, connShape, connWidth, connHeight);

                    if (newCurve == null)
                    {
                        t.RollBack();
                        TaskDialog.Show("Elbow Command", "Could not create connected MEP segment.");
                        return Result.Failed;
                    }

                    doc.Regenerate();

                    host = doc.GetElement(existId) as MEPCurve ?? host;
                    Connector liveOpen = FindLiveOpenNear(host, origin);

                    var ops = new Biruscan.Services.MepOperationService(doc);
                    ops.FinishOpenEndElbow(host, liveOpen, newCurve);

                    t.Commit();
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show($"Elbow {direction} Error", ex.Message);
                return Result.Failed;
            }
        }

        private static XYZ GetBendVector(Document doc, XYZ runDir, ElbowDirection direction)
        {
            XYZ up = XYZ.BasisZ;
            if (Math.Abs(runDir.Z) > 0.95)
            {
                up = doc.ActiveView?.UpDirection ?? XYZ.BasisY;
                if (Math.Abs(runDir.DotProduct(up)) > 0.9)
                    up = doc.ActiveView?.RightDirection ?? XYZ.BasisX;
            }
            up = SafeDir(up, XYZ.BasisZ);

            XYZ left = SafeDir(up.CrossProduct(runDir), XYZ.BasisY);
            XYZ right = SafeDir(runDir.CrossProduct(up), XYZ.BasisX);

            switch (direction)
            {
                case ElbowDirection.Up:
                    return Math.Abs(runDir.Z) > 0.95 ? up : XYZ.BasisZ;

                case ElbowDirection.Down:
                    return Math.Abs(runDir.Z) > 0.95 ? up.Negate() : -XYZ.BasisZ;

                case ElbowDirection.Left:
                    return left;

                case ElbowDirection.Right:
                    return right;

                case ElbowDirection.Up45:
                    return SafeDir(runDir + (Math.Abs(runDir.Z) > 0.95 ? up : XYZ.BasisZ), XYZ.BasisZ);

                case ElbowDirection.Down45:
                    return SafeDir(runDir - (Math.Abs(runDir.Z) > 0.95 ? up : XYZ.BasisZ), -XYZ.BasisZ);

                case ElbowDirection.Left45:
                    return SafeDir(runDir + left, left);

                case ElbowDirection.Right45:
                    return SafeDir(runDir + right, right);

                default:
                    return XYZ.BasisZ;
            }
        }

        


        public static Result ExecuteCustom(ExternalCommandData commandData, ref string message)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                var selectionIds = uidoc.Selection.GetElementIds();
                MEPCurve mepCurve = null;
                XYZ clickPoint = null;

                if (selectionIds != null && selectionIds.Count > 0)
                {
                    mepCurve = selectionIds.Select(id => doc.GetElement(id)).OfType<MEPCurve>().FirstOrDefault();
                }

                if (mepCurve == null)
                {
                    var filter = new MepCurveSelectionFilter();
                    Reference r;
                    try
                    {
                        r = uidoc.Selection.PickObject(
                            ObjectType.Element,
                            filter,
                            $"Pick open-ended Pipe, Duct, or Conduit to add Custom Elbow"
                        );
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }

                    if (r != null)
                    {
                        mepCurve = doc.GetElement(r.ElementId) as MEPCurve;
                        clickPoint = r.GlobalPoint;
                    }
                }

                if (mepCurve == null)
                {
                    TaskDialog.Show("Elbow Command", "No valid MEP curve (Pipe, Duct, Conduit) selected.");
                    return Result.Cancelled;
                }

                var connectors = mepCurve.ConnectorManager?.Connectors.Cast<Connector>()
                    .Where(c => c.ConnectorType != ConnectorType.Logical)
                    .ToList();

                if (connectors == null || connectors.Count == 0)
                    return Result.Failed;

                Connector targetConn = null;
                if (clickPoint != null)
                {
                    var sorted = connectors.OrderBy(c => c.Origin.DistanceTo(clickPoint)).ToList();
                    targetConn = sorted.FirstOrDefault(c => !c.IsConnected) ?? sorted.FirstOrDefault();
                }
                else
                {
                    targetConn = connectors.FirstOrDefault(c => !c.IsConnected) ?? connectors.FirstOrDefault();
                }

                if (targetConn == null) return Result.Failed;

                if (targetConn.IsConnected)
                {
                    var otherConn = connectors.FirstOrDefault(c => c.Id != targetConn.Id && !c.IsConnected);
                    if (otherConn != null) targetConn = otherConn;
                    else return Result.Failed;
                }

                XYZ origin = targetConn.Origin;
                XYZ runDir = XYZ.BasisX;
                if (mepCurve.Location is LocationCurve lc && lc.Curve is Line line)
                {
                    XYZ p0 = line.GetEndPoint(0);
                    XYZ p1 = line.GetEndPoint(1);
                    runDir = (origin.DistanceTo(p1) < origin.DistanceTo(p0))
                        ? SafeDir(p1 - p0, XYZ.BasisX)
                        : SafeDir(p0 - p1, XYZ.BasisX);
                }

                ElementId levelId = GetLevelId(doc, mepCurve, origin);
                ElementId existId = mepCurve.Id;
                var settings = Biruscan.Models.MepSettings.Instance;

                double connRadius = 0, connWidth = 0, connHeight = 0;
                ConnectorProfileType connShape = ConnectorProfileType.Round;
                XYZ basisX = XYZ.BasisX, basisY = XYZ.BasisY;
                CaptureConnectorSize(targetConn, out connRadius, out connWidth, out connHeight, out connShape, out basisX, out basisY);

                using (TransactionGroup tg = new TransactionGroup(doc, $"Custom Elbow"))
                {
                    tg.Start();

                    ElementId lastCurveId = ElementId.InvalidElementId;
                    ElementId lastElbowId = ElementId.InvalidElementId;

                    Action<double, double> onAngleChanged = (bendAngle, axialAngle) =>
                    {
                        using (Transaction t = new Transaction(doc, "Preview Angle"))
                        {
                            Biruscan.Services.MepOperationService.AllowNetworkDisconnects(t);
                            t.Start();
                            try
                            {
                                SafeDelete(doc, ref lastCurveId);
                                SafeDelete(doc, ref lastElbowId);

                                XYZ bendDir = GetBendVectorUniversal(doc, runDir, bendAngle, axialAngle);
                                double stubLength = settings.ElbowExtensionLengthFeet;

                                double ductW = connWidth > 0.001 ? connWidth : 1.0;
                                double ductH = connHeight > 0.001 ? connHeight : 1.0;

                                MEPCurve host = doc.GetElement(existId) as MEPCurve ?? mepCurve;
                                if (host is Pipe)
                                    stubLength = Math.Max(settings.ElbowExtensionLengthFeet, connRadius * 4.0);
                                else if (host is Duct)
                                {
                                    if (Math.Abs(bendDir.Z) < 0.5)
                                    {
                                        if (Math.Abs(basisX.Z) > Math.Abs(basisY.Z)) { ductW = connHeight > 0.001 ? connHeight : 1.0; ductH = connWidth > 0.001 ? connWidth : 1.0; }
                                    }
                                    else
                                    {
                                        if (Math.Abs(basisY.X) > Math.Abs(basisX.X)) { ductW = connHeight > 0.001 ? connHeight : 1.0; ductH = connWidth > 0.001 ? connWidth : 1.0; }
                                    }
                                    stubLength = Math.Max(settings.ElbowExtensionLengthFeet, Math.Max(ductW, ductH) * 2.0);
                                }

                                XYZ endPoint = origin + bendDir * stubLength;
                                MEPCurve newCurve = TryCreateStub(doc, host, origin, endPoint, levelId,
                                    connRadius, connWidth, connHeight, connShape, ductW, ductH);

                                if (newCurve != null)
                                {
                                    lastCurveId = newCurve.Id;
                                    doc.Regenerate();

                                    host = doc.GetElement(existId) as MEPCurve ?? host;
                                    Connector liveOpen = FindLiveOpenNear(host, origin);
                                    var ops = new Biruscan.Services.MepOperationService(doc);
                                    FamilyInstance elbow = ops.FinishOpenEndElbow(host, liveOpen, newCurve);
                                    if (elbow != null)
                                        lastElbowId = elbow.Id;

                                    if (host is Duct && connShape != ConnectorProfileType.Round)
                                    {
                                        Parameter wParam = newCurve.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                                        Parameter hParam = newCurve.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                                        if (wParam != null && !wParam.IsReadOnly) wParam.Set(ductW);
                                        if (hParam != null && !hParam.IsReadOnly) hParam.Set(ductH);
                                    }
                                }

                                t.Commit();
                                uidoc.RefreshActiveView();
                            }
                            catch
                            {
                                if (t.HasStarted())
                                    t.RollBack();
                            }
                        }
                    };

                    var win = new Biruscan.UI.Views.MepCustomAngleWindow(onAngleChanged, 30, 0);
                    try { new System.Windows.Interop.WindowInteropHelper(win).Owner = commandData.Application.MainWindowHandle; } catch { }

                    onAngleChanged(30, 0);

                    if (win.ShowDialog() == true)
                    {
                        tg.Assimilate();
                        return Result.Succeeded;
                    }
                    else
                    {
                        tg.RollBack();
                        return Result.Cancelled;
                    }
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static XYZ GetBendVectorUniversal(Document doc, XYZ runDir, double bendAngle, double axialRotation)
        {
            XYZ up = XYZ.BasisZ;
            if (Math.Abs(runDir.Z) > 0.95)
            {
                up = doc.ActiveView?.UpDirection ?? XYZ.BasisY;
                if (Math.Abs(runDir.DotProduct(up)) > 0.9)
                    up = doc.ActiveView?.RightDirection ?? XYZ.BasisX;
            }
            up = SafeDir(up, XYZ.BasisZ);

            XYZ refUp = SafeDir(up.Subtract(runDir.Multiply(runDir.DotProduct(up))), XYZ.BasisY);
            XYZ right = SafeDir(runDir.CrossProduct(refUp), XYZ.BasisX);

            double axialRad = axialRotation * Math.PI / 180.0;
            XYZ T = SafeDir(refUp * Math.Cos(axialRad) + right * Math.Sin(axialRad), refUp);

            double bendRad = bendAngle * Math.PI / 180.0;
            return SafeDir(runDir * Math.Cos(bendRad) + T * Math.Sin(bendRad), T);
        }

        private static XYZ SafeDir(XYZ v, XYZ fallback)
        {
            if (v == null || v.GetLength() < 1e-9) return fallback ?? XYZ.BasisZ;
            return v.Normalize();
        }

        private static Connector FindLiveOpenNear(MEPCurve curve, XYZ origin)
        {
            if (curve?.ConnectorManager == null || origin == null) return null;
            try
            {
                Connector best = null;
                double bestDist = double.MaxValue;
                foreach (Connector c in curve.ConnectorManager.Connectors)
                {
                    try
                    {
                        if (c.IsConnected || c.ConnectorType == ConnectorType.Logical) continue;
                        double d = c.Origin.DistanceTo(origin);
                        if (d < bestDist) { bestDist = d; best = c; }
                    }
                    catch { }
                }
                return best;
            }
            catch { return null; }
        }

        private static void CaptureConnectorSize(Connector conn,
            out double radius, out double width, out double height, out ConnectorProfileType shape,
            out XYZ basisX, out XYZ basisY)
        {
            radius = 0; width = 1.0; height = 1.0;
            shape = ConnectorProfileType.Round;
            basisX = XYZ.BasisX; basisY = XYZ.BasisY;
            if (conn == null) return;
            try { shape = conn.Shape; } catch { }
            try { radius = conn.Radius; } catch { }
            try { if (shape != ConnectorProfileType.Round) { width = conn.Width; height = conn.Height; } } catch { }
            try
            {
                if (conn.CoordinateSystem != null)
                {
                    basisX = conn.CoordinateSystem.BasisX;
                    basisY = conn.CoordinateSystem.BasisY;
                }
            }
            catch { }
        }

        private static void SafeDelete(Document doc, ref ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return;
            try
            {
                if (doc.GetElement(id) != null)
                    doc.Delete(id);
            }
            catch { }
            id = ElementId.InvalidElementId;
        }

        private static MEPCurve TryCreateStub(Document doc, MEPCurve host, XYZ origin, XYZ endPoint, ElementId levelId,
            double radius, double width, double height, ConnectorProfileType shape, double ductW, double ductH)
        {
            if (host == null || origin == null || endPoint == null || origin.DistanceTo(endPoint) < 0.01)
                return null;
            try
            {
                if (host is Pipe pipe)
                {
                    ElementId sysTypeId = pipe.MEPSystem?.GetTypeId();
                    if (sysTypeId == null || sysTypeId == ElementId.InvalidElementId)
                    {
                        sysTypeId = new FilteredElementCollector(doc)
                            .OfClass(typeof(PipingSystemType))
                            .Cast<PipingSystemType>()
                            .FirstOrDefault()?.Id ?? ElementId.InvalidElementId;
                    }
                    if (sysTypeId == ElementId.InvalidElementId || levelId == ElementId.InvalidElementId)
                        return null;
                    Pipe newPipe = Pipe.Create(doc, sysTypeId, pipe.GetTypeId(), levelId, origin, endPoint);
                    if (newPipe == null) return null;
                    if (radius > 0.001)
                    {
                        Parameter diaParam = newPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (diaParam != null && !diaParam.IsReadOnly) diaParam.Set(radius * 2.0);
                    }
                    return newPipe;
                }
                if (host is Duct duct)
                {
                    ElementId sysTypeId = duct.MEPSystem?.GetTypeId();
                    if (sysTypeId == null || sysTypeId == ElementId.InvalidElementId)
                    {
                        sysTypeId = new FilteredElementCollector(doc)
                            .OfClass(typeof(MechanicalSystemType))
                            .Cast<MechanicalSystemType>()
                            .FirstOrDefault()?.Id ?? ElementId.InvalidElementId;
                    }
                    if (sysTypeId == ElementId.InvalidElementId || levelId == ElementId.InvalidElementId)
                        return null;
                    Duct newDuct = Duct.Create(doc, sysTypeId, duct.GetTypeId(), levelId, origin, endPoint);
                    if (newDuct == null) return null;
                    if (shape == ConnectorProfileType.Round)
                    {
                        Parameter diaParam = newDuct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                        if (diaParam != null && !diaParam.IsReadOnly && radius > 0.001)
                            diaParam.Set(radius * 2.0);
                    }
                    else
                    {
                        Parameter wParam = newDuct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                        Parameter hParam = newDuct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                        if (wParam != null && !wParam.IsReadOnly) wParam.Set(ductW > 0.001 ? ductW : width);
                        if (hParam != null && !hParam.IsReadOnly) hParam.Set(ductH > 0.001 ? ductH : height);
                    }
                    return newDuct;
                }
                if (host is Conduit conduit)
                {
                    if (levelId == ElementId.InvalidElementId) return null;
                    Conduit newConduit = Conduit.Create(doc, conduit.GetTypeId(), origin, endPoint, levelId);
                    if (newConduit == null) return null;
                    if (radius > 0.001)
                    {
                        Parameter diaParam = newConduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                        if (diaParam != null && !diaParam.IsReadOnly) diaParam.Set(radius * 2.0);
                    }
                    return newConduit;
                }
            }
            catch { }
            return null;
        }

        private static ElementId GetLevelId(Document doc, Element elem, XYZ point)
        {
            if (elem.LevelId != null && elem.LevelId != ElementId.InvalidElementId)
                return elem.LevelId;

            if (doc.ActiveView != null && doc.ActiveView.GenLevel != null)
                return doc.ActiveView.GenLevel.Id;

            Level nearestLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - point.Z))
                .FirstOrDefault();

            return nearestLevel?.Id ?? ElementId.InvalidElementId;
        }
}

    [Transaction(TransactionMode.Manual)]
    public class MepElbowCustomCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            return DirectionalElbowEngine.ExecuteCustom(commandData, ref message);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class MepElbowUpCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            return DirectionalElbowEngine.Execute(commandData, ElbowDirection.Up, ref message);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class MepElbowDownCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            return DirectionalElbowEngine.Execute(commandData, ElbowDirection.Down, ref message);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class MepElbowLeftCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            return DirectionalElbowEngine.Execute(commandData, ElbowDirection.Left, ref message);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class MepElbowRightCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            return DirectionalElbowEngine.Execute(commandData, ElbowDirection.Right, ref message);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class MepElbowUp45Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            return DirectionalElbowEngine.Execute(commandData, ElbowDirection.Up45, ref message);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class MepElbowDown45Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            return DirectionalElbowEngine.Execute(commandData, ElbowDirection.Down45, ref message);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class MepElbowLeft45Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            return DirectionalElbowEngine.Execute(commandData, ElbowDirection.Left45, ref message);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class MepElbowRight45Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            return DirectionalElbowEngine.Execute(commandData, ElbowDirection.Right45, ref message);
        }
    }
}
