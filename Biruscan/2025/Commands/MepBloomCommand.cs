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
    /// <summary>
    /// Selection filter that permits picking elements with MEP connectors (FamilyInstances, Equipment, Fixtures, MEP Curves).
    /// </summary>
    public class MepConnectableSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
            {
                return fi.MEPModel.ConnectorManager.Connectors.Size > 0;
            }
            if (elem is MEPCurve curve && curve.ConnectorManager != null)
            {
                return curve.ConnectorManager.Connectors.Size > 0;
            }
            return false;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return true;
        }
    }

    /// <summary>
    /// Pull Command: Automatically draws short pipe and duct stubs off all open/unconnected
    /// MEP connectors on selected equipment, fixtures, or fittings.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MepPullCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                var selectionIds = uidoc.Selection.GetElementIds();
                var targetElements = new List<Element>();

                if (selectionIds != null && selectionIds.Count > 0)
                {
                    foreach (var id in selectionIds)
                    {
                        var elem = doc.GetElement(id);
                        if (elem != null && HasConnectors(elem))
                        {
                            targetElements.Add(elem);
                        }
                    }
                }

                if (targetElements.Count == 0)
                {
                    var filter = new MepConnectableSelectionFilter();
                    IList<Reference> pickedRefs;
                    try
                    {
                        pickedRefs = uidoc.Selection.PickObjects(
                            ObjectType.Element,
                            filter,
                            "Select MEP equipment, fixtures, or fittings to Bloom (Press Finish or Esc when done)"
                        );
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }

                    if (pickedRefs != null && pickedRefs.Count > 0)
                    {
                        foreach (var r in pickedRefs)
                        {
                            var elem = doc.GetElement(r.ElementId);
                            if (elem != null && HasConnectors(elem))
                            {
                                targetElements.Add(elem);
                            }
                        }
                    }
                }

                if (targetElements.Count == 0)
                {
                    TaskDialog.Show("Bloom", "No MEP equipment, fixtures, or fittings selected.");
                    return Result.Cancelled;
                }

                int createdPipes = 0;
                int createdDucts = 0;
                int createdConduits = 0;
                int totalOpenConnectors = 0;

                using (Transaction t = new Transaction(doc, "Bloom MEP Connectors"))
                {
                    Biruscan.Services.MepOperationService.AllowNetworkDisconnects(t);
                    t.Start();

                    foreach (var elem in targetElements)
                    {
                        var openConnectors = GetUnconnectedConnectors(elem);
                        totalOpenConnectors += openConnectors.Count;

                        foreach (var conn in openConnectors)
                        {
                            XYZ origin;
                            XYZ dir;
                            int connId;
                            try
                            {
                                origin = conn.Origin;
                                dir = conn.CoordinateSystem != null ? conn.CoordinateSystem.BasisZ : XYZ.BasisZ;
                                connId = conn.Id;
                            }
                            catch { continue; }
                            if (dir.GetLength() < 0.001) dir = XYZ.BasisZ;
                            else dir = dir.Normalize();

                            ElementId levelId = GetLevelId(doc, elem, origin);

                            var settings = Biruscan.Models.MepSettings.Instance;

                            // Handle Piping Connectors
                            if (conn.Domain == Domain.DomainPiping)
                            {
                                double dia = conn.Radius * 2.0;
                                if (dia <= 0.001) dia = 1.0 / 12.0; // 1 inch default

                                double stubLength = Math.Max(settings.PipeExtensionLengthFeet, dia * 2.5);
                                XYZ endPoint = origin + dir * stubLength;

                                var pipeType = GetPipeType(doc, elem, conn, settings.PreferredPipeTypeName);
                                var sysTypeId = GetPipingSystemTypeId(doc, conn, elem);

                                if (pipeType != null && sysTypeId != ElementId.InvalidElementId && levelId != ElementId.InvalidElementId)
                                {
                                    try
                                    {
                                        Pipe pipe = Pipe.Create(doc, sysTypeId, pipeType.Id, levelId, origin, endPoint);
                                        if (pipe != null)
                                        {
                                            Parameter diaParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                                            if (diaParam != null && !diaParam.IsReadOnly)
                                            {
                                                diaParam.Set(dia);
                                            }

                                            doc.Regenerate();

                                            if (settings.AutoConnectStubs)
                                            {
                                                Connector liveEquip = FindConnectorById(doc.GetElement(elem.Id), connId);
                                                Connector nearConn = FindOpenNearOrigin(pipe.ConnectorManager, origin);
                                                if (nearConn != null && liveEquip != null)
                                                {
                                                    try { nearConn.ConnectTo(liveEquip); } catch { }
                                                }
                                            }

                                            createdPipes++;
                                        }
                                    }
                                    catch { }
                                }
                            }
                            // Handle HVAC / Duct Connectors
                            else if (conn.Domain == Domain.DomainHvac)
                            {
                                var ductType = GetDuctType(doc, elem, conn, conn.Shape, settings.PreferredRectDuctTypeName, settings.PreferredRoundDuctTypeName);
                                var sysTypeId = GetDuctSystemTypeId(doc, conn, elem);

                                if (ductType != null && sysTypeId != ElementId.InvalidElementId && levelId != ElementId.InvalidElementId)
                                {
                                    if (conn.Shape == ConnectorProfileType.Round)
                                    {
                                        double dia = conn.Radius * 2.0;
                                        if (dia <= 0.001) dia = 1.0; // 1 ft default

                                        double stubLength = Math.Max(settings.DuctExtensionLengthFeet, dia * 1.5);
                                        XYZ endPoint = origin + dir * stubLength;

                                        try
                                        {
                                            Duct duct = Duct.Create(doc, sysTypeId, ductType.Id, levelId, origin, endPoint);
                                            if (duct != null)
                                            {
                                                Parameter diaParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                                                if (diaParam != null && !diaParam.IsReadOnly)
                                                {
                                                    diaParam.Set(dia);
                                                }

                                                doc.Regenerate();

                                                if (settings.AutoConnectStubs)
                                                {
                                                    Connector liveEquip = FindConnectorById(doc.GetElement(elem.Id), connId);
                                                    Connector nearConn = FindOpenNearOrigin(duct.ConnectorManager, origin);
                                                    if (nearConn != null && liveEquip != null)
                                                    {
                                                        try { nearConn.ConnectTo(liveEquip); } catch { }
                                                    }
                                                }

                                                createdDucts++;
                                            }
                                        }
                                        catch { }
                                    }
                                    else
                                    {
                                        double cWidth = conn.Width > 0.001 ? conn.Width : 1.0;
                                        double cHeight = conn.Height > 0.001 ? conn.Height : 1.0;

                                        double ductW = cWidth;
                                        double ductH = cHeight;

                                        if (conn.CoordinateSystem != null)
                                        {
                                            if (Math.Abs(dir.Z) < 0.5)
                                            {
                                                double xZ = Math.Abs(conn.CoordinateSystem.BasisX.Z);
                                                double yZ = Math.Abs(conn.CoordinateSystem.BasisY.Z);
                                                if (xZ > yZ)
                                                {
                                                    ductW = cHeight;
                                                    ductH = cWidth;
                                                }
                                            }
                                            else
                                            {
                                                double xX = Math.Abs(conn.CoordinateSystem.BasisX.X);
                                                double yX = Math.Abs(conn.CoordinateSystem.BasisY.X);
                                                if (yX > xX)
                                                {
                                                    ductW = cHeight;
                                                    ductH = cWidth;
                                                }
                                            }
                                        }

                                        double stubLength = Math.Max(settings.DuctExtensionLengthFeet, Math.Max(ductW, ductH) * 1.5);
                                        XYZ endPoint = origin + dir * stubLength;

                                        try
                                        {
                                            Duct duct = Duct.Create(doc, sysTypeId, ductType.Id, levelId, origin, endPoint);
                                            if (duct != null)
                                            {
                                                Parameter wParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                                                Parameter hParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

                                                if (wParam != null && !wParam.IsReadOnly) wParam.Set(ductW);
                                                if (hParam != null && !hParam.IsReadOnly) hParam.Set(ductH);

                                                doc.Regenerate();

                                                if (settings.AutoConnectStubs)
                                                {
                                                    Connector liveEquip = FindConnectorById(doc.GetElement(elem.Id), connId);
                                                    Connector nearConn = FindOpenNearOrigin(duct.ConnectorManager, origin);
                                                    if (nearConn != null && liveEquip != null)
                                                    {
                                                        try { nearConn.ConnectTo(liveEquip); } catch { }
                                                    }
                                                }
                                                
                                                if (wParam != null && !wParam.IsReadOnly) wParam.Set(ductW);
                                                if (hParam != null && !hParam.IsReadOnly) hParam.Set(ductH);

                                                createdDucts++;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            // Handle Electrical / Conduit Connectors
                            else if (conn.Domain == Domain.DomainElectrical || conn.Domain == Domain.DomainCableTrayConduit)
                            {
                                var conduitType = GetConduitType(doc, elem, conn);
                                if (conduitType != null && levelId != ElementId.InvalidElementId)
                                {
                                    double stubLength = settings.ConduitExtensionLengthFeet;
                                    XYZ endPoint = origin + dir * stubLength;

                                    try
                                    {
                                        Conduit conduit = Conduit.Create(doc, conduitType.Id, origin, endPoint, levelId);
                                        if (conduit != null)
                                        {
                                            double dia = conn.Radius > 0.001 ? conn.Radius * 2.0 : 0.75 / 12.0;
                                            Parameter diaParam = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                                            if (diaParam != null && !diaParam.IsReadOnly)
                                            {
                                                diaParam.Set(dia);
                                            }

                                            createdConduits++;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }

                    t.Commit();
                }

                int totalCreated = createdPipes + createdDucts + createdConduits;
                if (totalCreated > 0)
                {
                    TaskDialog.Show(
                        "Pull",
                        $"Pull generated {totalCreated} stubs ({createdPipes} Pipes, {createdDucts} Ducts, {createdConduits} Conduits) across {targetElements.Count} element(s)."
                    );
                    return Result.Succeeded;
                }
                else
                {
                    if (totalOpenConnectors == 0)
                    {
                        TaskDialog.Show("Pull", "Selected element(s) have no open or unconnected MEP connectors.");
                    }
                    else
                    {
                        TaskDialog.Show("Pull", $"Found {totalOpenConnectors} open connector(s), but could not generate stubs. Ensure appropriate MEP systems (Piping/Duct Types) are loaded in the model.");
                    }
                    return Result.Succeeded;
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Pull Error", ex.Message);
                return Result.Failed;
            }
        }

        [Transaction(TransactionMode.Manual)]
        public class MepBloomCommand : MepPullCommand { }

        private static bool HasConnectors(Element elem)
        {
            if (elem is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
            {
                return fi.MEPModel.ConnectorManager.Connectors.Size > 0;
            }
            if (elem is MEPCurve curve && curve.ConnectorManager != null)
            {
                return curve.ConnectorManager.Connectors.Size > 0;
            }
            return false;
        }

        private static List<Connector> GetUnconnectedConnectors(Element elem)
        {
            var list = new List<Connector>();
            ConnectorSet set = null;

            if (elem is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
            {
                set = fi.MEPModel.ConnectorManager.Connectors;
            }
            else if (elem is MEPCurve curve && curve.ConnectorManager != null)
            {
                set = curve.ConnectorManager.Connectors;
            }

            if (set != null)
            {
                foreach (Connector c in set)
                {
                    if (!c.IsConnected && c.ConnectorType != ConnectorType.Logical)
                    {
                        list.Add(c);
                    }
                }
            }

            return list;
        }

        private static Connector FindConnectorById(Element elem, int connectorId)
        {
            ConnectorSet set = null;
            if (elem is FamilyInstance fi)
                set = fi.MEPModel?.ConnectorManager?.Connectors;
            else if (elem is MEPCurve curve)
                set = curve.ConnectorManager?.Connectors;
            if (set == null) return null;
            foreach (Connector c in set)
            {
                try { if (c.Id == connectorId) return c; } catch { }
            }
            return null;
        }

        private static Connector FindOpenNearOrigin(ConnectorManager cm, XYZ origin)
        {
            if (cm == null || origin == null) return null;
            Connector best = null;
            double bestDist = double.MaxValue;
            foreach (Connector c in cm.Connectors)
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

        private static PipeType GetPipeType(Document doc, Element elem, Connector connector, string preferredTypeName)
        {
            if (elem is Pipe pipe)
            {
                return doc.GetElement(pipe.GetTypeId()) as PipeType;
            }

            if (elem is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
            {
                foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                {
                    if (c.IsConnected && c.Domain == Domain.DomainPiping && connector.Domain == Domain.DomainPiping && c.PipeSystemType == connector.PipeSystemType)
                    {
                        foreach (Connector refConn in c.AllRefs)
                        {
                            if (refConn.Owner is Pipe connectedPipe)
                                return doc.GetElement(connectedPipe.GetTypeId()) as PipeType;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(preferredTypeName))
            {
                var pref = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipeType))
                    .Cast<PipeType>()
                    .FirstOrDefault(pt => pt.Name.Equals(preferredTypeName, StringComparison.OrdinalIgnoreCase));
                if (pref != null) return pref;
            }

            return new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .FirstOrDefault();
        }

        private static ElementId GetPipingSystemTypeId(Document doc, Connector connector, Element elem)
        {
            if (connector.MEPSystem != null && connector.MEPSystem.GetTypeId() != ElementId.InvalidElementId)
                return connector.MEPSystem.GetTypeId();

            var manager = (elem as FamilyInstance)?.MEPModel?.ConnectorManager ?? (elem as MEPCurve)?.ConnectorManager;
            if (manager != null)
            {
                foreach (Connector c in manager.Connectors)
                {
                    if (c.IsConnected && c.Domain == Domain.DomainPiping && connector.Domain == Domain.DomainPiping && c.PipeSystemType == connector.PipeSystemType)
                    {
                        foreach (Connector refConn in c.AllRefs)
                        {
                            if (refConn.Owner is MEPCurve curve && curve.MEPSystem != null)
                                return curve.MEPSystem.GetTypeId();
                        }
                    }
                }
            }

            try
            {
                string pType = connector.PipeSystemType.ToString();
                if (pType != "Undefined" && pType != "Global")
                {
                    var sys = new FilteredElementCollector(doc)
                        .OfClass(typeof(PipingSystemType))
                        .Cast<PipingSystemType>()
                        .FirstOrDefault(s => s.SystemClassification.ToString() == pType);

                    if (sys != null) return sys.Id;
                }
            }
            catch { }

            var defaultSys = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .FirstOrDefault();

            return defaultSys?.Id ?? ElementId.InvalidElementId;
        }

        private static DuctType GetDuctType(Document doc, Element elem, Connector connector, ConnectorProfileType shape, string preferredRectTypeName, string preferredRoundTypeName)
        {
            if (elem is Duct duct)
            {
                var dt = doc.GetElement(duct.GetTypeId()) as DuctType;
                if (dt != null && (dt.Shape == shape || (shape != ConnectorProfileType.Round && shape != ConnectorProfileType.Rectangular))) 
                    return dt;
            }

            if (elem is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
            {
                foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                {
                    if (c.IsConnected && c.Domain == Domain.DomainHvac && connector.Domain == Domain.DomainHvac && c.DuctSystemType == connector.DuctSystemType)
                    {
                        foreach (Connector refConn in c.AllRefs)
                        {
                            if (refConn.Owner is Duct connectedDuct)
                            {
                                var dt = doc.GetElement(connectedDuct.GetTypeId()) as DuctType;
                                if (dt != null && dt.Shape == shape) return dt;
                            }
                        }
                    }
                }
            }

            string preferredTypeName = shape == ConnectorProfileType.Round ? preferredRoundTypeName : preferredRectTypeName;
            if (!string.IsNullOrEmpty(preferredTypeName))
            {
                var pref = new FilteredElementCollector(doc)
                    .OfClass(typeof(DuctType))
                    .Cast<DuctType>()
                    .FirstOrDefault(dt => dt.Name.Equals(preferredTypeName, StringComparison.OrdinalIgnoreCase) && dt.Shape == shape);
                if (pref != null) return pref;
            }

            var ductTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(DuctType))
                .Cast<DuctType>()
                .ToList();

            if (shape == ConnectorProfileType.Round)
            {
                var round = ductTypes.FirstOrDefault(dt => dt.Shape == ConnectorProfileType.Round || dt.Name.ToLower().Contains("round"));
                if (round != null) return round;
            }
            else
            {
                var rect = ductTypes.FirstOrDefault(dt => dt.Shape == ConnectorProfileType.Rectangular || dt.Name.ToLower().Contains("rect"));
                if (rect != null) return rect;
            }

            return ductTypes.FirstOrDefault();
        }

        private static ElementId GetDuctSystemTypeId(Document doc, Connector connector, Element elem)
        {
            if (connector.MEPSystem != null && connector.MEPSystem.GetTypeId() != ElementId.InvalidElementId)
                return connector.MEPSystem.GetTypeId();

            var manager = (elem as FamilyInstance)?.MEPModel?.ConnectorManager ?? (elem as MEPCurve)?.ConnectorManager;
            if (manager != null)
            {
                foreach (Connector c in manager.Connectors)
                {
                    if (c.IsConnected && c.Domain == Domain.DomainHvac && connector.Domain == Domain.DomainHvac && c.DuctSystemType == connector.DuctSystemType)
                    {
                        foreach (Connector refConn in c.AllRefs)
                        {
                            if (refConn.Owner is MEPCurve curve && curve.MEPSystem != null)
                                return curve.MEPSystem.GetTypeId();
                        }
                    }
                }
            }

            try
            {
                string dType = connector.DuctSystemType.ToString();
                if (dType != "Undefined" && dType != "Global")
                {
                    var sys = new FilteredElementCollector(doc)
                        .OfClass(typeof(MechanicalSystemType))
                        .Cast<MechanicalSystemType>()
                        .FirstOrDefault(s => s.SystemClassification.ToString() == dType);

                    if (sys != null) return sys.Id;
                }
            }
            catch { }

            var defaultSys = new FilteredElementCollector(doc)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .FirstOrDefault();

            return defaultSys?.Id ?? ElementId.InvalidElementId;
        }

        private static ConduitType GetConduitType(Document doc, Element elem, Connector connector)
        {
            if (elem is Conduit conduit)
            {
                return doc.GetElement(conduit.GetTypeId()) as ConduitType;
            }

            if (elem is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
            {
                foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                {
                    if (c.IsConnected && c.Domain == connector.Domain)
                    {
                        foreach (Connector refConn in c.AllRefs)
                        {
                            if (refConn.Owner is Conduit connectedConduit)
                                return doc.GetElement(connectedConduit.GetTypeId()) as ConduitType;
                        }
                    }
                }
            }

            return new FilteredElementCollector(doc)
                .OfClass(typeof(ConduitType))
                .Cast<ConduitType>()
                .FirstOrDefault();
        }
    }
}
