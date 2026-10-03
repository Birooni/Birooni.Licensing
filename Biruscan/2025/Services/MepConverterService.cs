using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Biruscan.Services
{
    /// <summary>
    /// Supported MEP Conversion Modes.
    /// </summary>
    public enum MepConversionMode
    {
        RoundDuctToPipe,
        PipeToConduit,
        ConduitToPipe,
        PipeToRoundDuct
    }

    /// <summary>
    /// Configuration options for the MEP network conversion.
    /// </summary>
    public class MepConversionOptions
    {
        public MepConversionMode Mode { get; set; } = MepConversionMode.RoundDuctToPipe;
        public ElementId TargetTypeId { get; set; } = ElementId.InvalidElementId;
        public ElementId TargetSystemTypeId { get; set; } = ElementId.InvalidElementId;
        public bool RebuildFittings { get; set; } = true;
        public bool DeleteOriginal { get; set; } = true;
        public bool CopyParameters { get; set; } = true;
        public bool TraverseConnectedNetwork { get; set; } = true;
    }

    /// <summary>
    /// Result metrics returned after network conversion.
    /// </summary>
    public class MepConversionResult
    {
        public bool Success { get; set; }
        public int CurvesConverted { get; set; }
        public int FittingsRebuilt { get; set; }
        public int ElementsDeleted { get; set; }
        public List<string> Messages { get; } = new List<string>();
        public List<ElementId> CreatedElementIds { get; } = new List<ElementId>();

        public string Summary
        {
            get
            {
                if (!Success && CurvesConverted == 0)
                    return string.Join("\n", Messages);

                string status = $"Successfully converted {CurvesConverted} run(s) and created/rebuilt {FittingsRebuilt} fitting(s).\n" +
                                (DeleteOriginal ? $"Replaced {ElementsDeleted} original element(s)." : "Original elements retained.");

                if (Messages.Count > 0)
                {
                    status += "\n\nNotes:\n" + string.Join("\n", Messages.Take(10));
                    if (Messages.Count > 10) status += $"\n... and {Messages.Count - 10} more notes.";
                }

                return status;
            }
        }

        public bool DeleteOriginal { get; set; } = true;
    }

    /// <summary>
    /// Service for converting entire connected MEP networks (curves + fittings)
    /// between Round Ducts, Pipes, and Conduits with true fitting reconstruction.
    /// </summary>
    public class MepConverterService
    {
        private readonly Document _doc;

        private static readonly ElementId PipeFittingCatId = new ElementId(BuiltInCategory.OST_PipeFitting);
        private static readonly ElementId DuctFittingCatId = new ElementId(BuiltInCategory.OST_DuctFitting);
        private static readonly ElementId ConduitFittingCatId = new ElementId(BuiltInCategory.OST_ConduitFitting);

        public MepConverterService(Document doc)
        {
            _doc = doc;
        }

        private class NetworkSegment
        {
            public ElementId OriginalId { get; set; }
            public MEPCurve OriginalCurve { get; set; }
            public XYZ Start { get; set; }
            public XYZ End { get; set; }
            public double Diameter { get; set; }
            public ElementId LevelId { get; set; }
            public MEPCurve CreatedCurve { get; set; }
        }

        private class NetworkJunction
        {
            public string Kind { get; set; } = "elbow"; // elbow, tee, cross, transition, union
            public XYZ Center { get; set; }
            public List<NetworkSegment> ConnectedSegments { get; } = new List<NetworkSegment>();
            public FamilyInstance OriginalFitting { get; set; }
        }

        /// <summary>
        /// Converts the given elements and their connected network according to the specified options.
        /// </summary>
        public MepConversionResult ConvertNetwork(ICollection<ElementId> initialIds, MepConversionOptions options)
        {
            var result = new MepConversionResult { DeleteOriginal = options.DeleteOriginal };

            if (initialIds == null || initialIds.Count == 0)
            {
                result.Success = false;
                result.Messages.Add("No elements selected for conversion.");
                return result;
            }

            // 1. Discover all connected network elements (curves + fittings)
            HashSet<ElementId> networkIds;
            if (options.TraverseConnectedNetwork)
            {
                networkIds = TraverseNetwork(initialIds, options.Mode);
            }
            else
            {
                networkIds = new HashSet<ElementId>(initialIds);
            }

            if (networkIds.Count == 0)
            {
                result.Success = false;
                result.Messages.Add("No matching MEP elements found for the selected conversion mode.");
                return result;
            }

            var sourceCurves = new List<MEPCurve>();
            var sourceFittings = new List<FamilyInstance>();

            foreach (var id in networkIds)
            {
                Element elem = _doc.GetElement(id);
                if (elem == null) continue;

                if (elem is MEPCurve mepCurve && IsCompatibleCurve(mepCurve, options.Mode))
                {
                    sourceCurves.Add(mepCurve);
                }
                else if (elem is FamilyInstance fi && IsMepFitting(fi))
                {
                    sourceFittings.Add(fi);
                }
            }

            if (sourceCurves.Count == 0)
            {
                result.Success = false;
                result.Messages.Add($"No compatible source curves found for mode '{options.Mode}'.");
                return result;
            }

            // 2. Resolve default target types if not supplied
            ResolveTargetTypes(options);

            if (options.TargetTypeId == ElementId.InvalidElementId)
            {
                result.Success = false;
                result.Messages.Add("No valid target MEP Type found in the project. Please load or select a target family type.");
                return result;
            }

            // 3. Build topological segments and junction network with corner extension
            var (segments, junctions) = BuildNetworkTopology(sourceCurves, sourceFittings);

            // 4. Execute conversion in a Transaction
            using (Transaction t = new Transaction(_doc, "Convert MEP Network"))
            {
                MepOperationService.AllowNetworkDisconnects(t);
                t.Start();

                var newCurves = new List<MEPCurve>();

                // Create target curves
                foreach (var seg in segments)
                {
                    try
                    {
                        MEPCurve newCurve = CreateTargetCurve(seg, options);
                        if (newCurve != null)
                        {
                            seg.CreatedCurve = newCurve;
                            newCurves.Add(newCurve);
                            result.CreatedElementIds.Add(newCurve.Id);
                            result.CurvesConverted++;

                            if (options.CopyParameters && seg.OriginalCurve != null)
                            {
                                CopyMepParameters(seg.OriginalCurve, newCurve);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Messages.Add($"Error creating curve: {ex.Message}");
                    }
                }

                // 5. Rebuild fittings on the new curves
                if (options.RebuildFittings && newCurves.Count > 0)
                {
                    _doc.Regenerate();

                    int rebuilt = RebuildFittingsAtJunctions(junctions, newCurves, result.Messages);
                    result.FittingsRebuilt += rebuilt;

                    int autoConnected = AutoConnectRemainingEndpoints(newCurves);
                    result.FittingsRebuilt += autoConnected;
                }

                // 6. Delete original elements
                if (options.DeleteOriginal)
                {
                    var toDelete = new List<ElementId>();
                    foreach (var c in sourceCurves) toDelete.Add(c.Id);
                    foreach (var f in sourceFittings) toDelete.Add(f.Id);

                    try
                    {
                        var deleted = _doc.Delete(toDelete);
                        result.ElementsDeleted = deleted?.Count ?? toDelete.Count;
                    }
                    catch (Exception ex)
                    {
                        result.Messages.Add("Note on element cleanup: " + ex.Message);
                    }
                }

                t.Commit();
            }

            result.Success = result.CurvesConverted > 0;
            return result;
        }

        #region Topology Building and Endpoint Extension

        private (List<NetworkSegment> segments, List<NetworkJunction> junctions) BuildNetworkTopology(
            List<MEPCurve> curves, List<FamilyInstance> fittings)
        {
            var segments = new List<NetworkSegment>();
            var curveSegMap = new Dictionary<ElementId, NetworkSegment>();

            foreach (var c in curves)
            {
                if (!(c.Location is LocationCurve lc) || !(lc.Curve is Line line))
                    continue;

                XYZ start = line.GetEndPoint(0);
                XYZ end = line.GetEndPoint(1);
                double dia = GetDiameter(c);

                ElementId levelId = c.ReferenceLevel?.Id ?? c.LevelId;
                if (levelId == ElementId.InvalidElementId)
                {
                    var lvl = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>()
                        .OrderBy(l => Math.Abs(l.Elevation - start.Z)).FirstOrDefault();
                    if (lvl != null) levelId = lvl.Id;
                }

                var seg = new NetworkSegment
                {
                    OriginalId = c.Id,
                    OriginalCurve = c,
                    Start = start,
                    End = end,
                    Diameter = dia,
                    LevelId = levelId
                };
                segments.Add(seg);
                curveSegMap[c.Id] = seg;
            }

            var junctions = new List<NetworkJunction>();

            // 1. Build junctions from existing fittings
            foreach (var fitting in fittings)
            {
                if (fitting.MEPModel?.ConnectorManager == null) continue;

                var connSegs = new List<NetworkSegment>();
                XYZ center = (fitting.Location as LocationPoint)?.Point;

                var fittingConnectors = new List<Connector>();
                foreach (Connector fc in fitting.MEPModel.ConnectorManager.Connectors)
                {
                    fittingConnectors.Add(fc);
                    foreach (Connector linked in fc.AllRefs)
                    {
                        if (linked.Owner is MEPCurve curve && curveSegMap.TryGetValue(curve.Id, out NetworkSegment seg))
                        {
                            if (!connSegs.Contains(seg))
                                connSegs.Add(seg);
                        }
                    }
                }

                if (connSegs.Count >= 2)
                {
                    // Compute accurate intersection apex center if needed
                    if (center == null || fittingConnectors.Count >= 2)
                    {
                        XYZ apex = ComputeJunctionApex(connSegs, center);
                        if (apex != null) center = apex;
                    }

                    if (center != null)
                    {
                        string kind = ClassifyFittingKind(fitting, connSegs.Count);
                        var junc = new NetworkJunction
                        {
                            Kind = kind,
                            Center = center,
                            OriginalFitting = fitting
                        };
                        junc.ConnectedSegments.AddRange(connSegs);
                        junctions.Add(junc);

                        // Extend connected segment endpoints to meeting apex Center
                        foreach (var seg in connSegs)
                        {
                            SnapSegmentEndpointToPoint(seg, center);
                        }
                    }
                }
            }

            // 2. Build junctions from intersecting / meeting segment ends without fitting instances
            for (int i = 0; i < segments.Count; i++)
            {
                for (int j = i + 1; j < segments.Count; j++)
                {
                    var segA = segments[i];
                    var segB = segments[j];

                    // Check if already in same junction
                    if (junctions.Any(junc => junc.ConnectedSegments.Contains(segA) && junc.ConnectedSegments.Contains(segB)))
                        continue;

                    // Find closest approach of segment axes
                    XYZ dirA = (segA.End - segA.Start).Normalize();
                    XYZ dirB = (segB.End - segB.Start).Normalize();

                    if (LineClosestApproach(segA.Start, dirA, segB.Start, dirB,
                        out double ta, out double tb, out double gap, out XYZ pa, out XYZ pb))
                    {
                        double maxGap = Math.Max(segA.Diameter, segB.Diameter) * 2.5 + 0.5; // ft
                        if (gap <= maxGap)
                        {
                            XYZ apex = (pa + pb).Multiply(0.5);

                            double lenA = segA.Start.DistanceTo(segA.End);
                            double lenB = segB.Start.DistanceTo(segB.End);

                            // Check if apex is near endpoints
                            bool nearA = ta < 0.5 || ta > lenA - 0.5;
                            bool nearB = tb < 0.5 || tb > lenB - 0.5;

                            if (nearA && nearB && pa.DistanceTo(pb) < 1.0)
                            {
                                SnapSegmentEndpointToPoint(segA, apex);
                                SnapSegmentEndpointToPoint(segB, apex);

                                double dot = Math.Abs(dirA.DotProduct(dirB));
                                string kind = dot > 0.96 ? (Math.Abs(segA.Diameter - segB.Diameter) > 0.01 ? "transition" : "union") : "elbow";

                                var junc = new NetworkJunction
                                {
                                    Kind = kind,
                                    Center = apex
                                };
                                junc.ConnectedSegments.Add(segA);
                                junc.ConnectedSegments.Add(segB);
                                junctions.Add(junc);
                            }
                        }
                    }
                }
            }

            return (segments, junctions);
        }

        private static void SnapSegmentEndpointToPoint(NetworkSegment seg, XYZ pt)
        {
            double dStart = seg.Start.DistanceTo(pt);
            double dEnd = seg.End.DistanceTo(pt);

            if (dStart < dEnd)
            {
                if (dStart < 3.0) seg.Start = pt;
            }
            else
            {
                if (dEnd < 3.0) seg.End = pt;
            }
        }

        private static XYZ ComputeJunctionApex(List<NetworkSegment> segs, XYZ fallback)
        {
            if (segs == null || segs.Count < 2) return fallback;

            var segA = segs[0];
            var segB = segs[1];
            XYZ dirA = (segA.End - segA.Start).Normalize();
            XYZ dirB = (segB.End - segB.Start).Normalize();

            if (LineClosestApproach(segA.Start, dirA, segB.Start, dirB,
                out _, out _, out double gap, out XYZ pa, out XYZ pb))
            {
                if (gap < 1.5)
                {
                    return (pa + pb).Multiply(0.5);
                }
            }

            return fallback;
        }

        private static bool LineClosestApproach(XYZ p1, XYZ d1, XYZ p2, XYZ d2,
            out double t, out double s, out double gap, out XYZ on1, out XYZ on2)
        {
            t = s = 0;
            gap = double.MaxValue;
            on1 = p1; on2 = p2;
            XYZ cross = d1.CrossProduct(d2);
            double den = cross.DotProduct(cross);
            if (den < 1e-10) return false;
            XYZ diff = p2 - p1;
            t = diff.CrossProduct(d2).DotProduct(cross) / den;
            s = diff.CrossProduct(d1).DotProduct(cross) / den;
            on1 = p1 + d1.Multiply(t);
            on2 = p2 + d2.Multiply(s);
            gap = on1.DistanceTo(on2);
            return true;
        }

        #endregion

        #region Target Curve and Fitting Creation

        private MEPCurve CreateTargetCurve(NetworkSegment seg, MepConversionOptions options)
        {
            XYZ start = seg.Start;
            XYZ end = seg.End;

            if (start.DistanceTo(end) < 0.05)
                return null;

            MEPCurve created = null;

            switch (options.Mode)
            {
                case MepConversionMode.RoundDuctToPipe:
                case MepConversionMode.ConduitToPipe:
                    {
                        var pipe = Pipe.Create(_doc, options.TargetSystemTypeId, options.TargetTypeId, seg.LevelId, start, end);
                        if (pipe != null)
                        {
                            SetDiameterParameter(pipe, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, seg.Diameter);
                            created = pipe;
                        }
                    }
                    break;

                case MepConversionMode.PipeToConduit:
                    {
                        var conduit = Conduit.Create(_doc, options.TargetTypeId, start, end, seg.LevelId);
                        if (conduit != null)
                        {
                            SetDiameterParameter(conduit, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, seg.Diameter);
                            created = conduit;
                        }
                    }
                    break;

                case MepConversionMode.PipeToRoundDuct:
                    {
                        var duct = Duct.Create(_doc, options.TargetSystemTypeId, options.TargetTypeId, seg.LevelId, start, end);
                        if (duct != null)
                        {
                            SetDiameterParameter(duct, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, seg.Diameter);
                            var wParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                            if (wParam != null && !wParam.IsReadOnly) wParam.Set(seg.Diameter);
                            var hParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                            if (hParam != null && !hParam.IsReadOnly) hParam.Set(seg.Diameter);
                            created = duct;
                        }
                    }
                    break;
            }

            return created;
        }

        private int RebuildFittingsAtJunctions(List<NetworkJunction> junctions, List<MEPCurve> allCurves, List<string> messages)
        {
            int placedCount = 0;
            const double tolFt = 0.85; // search around junction apex

            foreach (var junc in junctions)
            {
                var connectors = new List<Connector>();
                var usedCurves = new HashSet<ElementId>();

                // 1. Gather open connectors from the created segments at this junction
                foreach (var seg in junc.ConnectedSegments)
                {
                    if (seg.CreatedCurve?.ConnectorManager == null) continue;
                    foreach (Connector c in seg.CreatedCurve.ConnectorManager.Connectors)
                    {
                        try
                        {
                            if (c.IsConnected) continue;
                            if (c.Origin.DistanceTo(junc.Center) < tolFt && !usedCurves.Contains(c.Owner.Id))
                            {
                                connectors.Add(c);
                                usedCurves.Add(c.Owner.Id);
                                break;
                            }
                        }
                        catch { }
                    }
                }

                // 2. If not enough found from mapped segments, search all new curves near junction
                if (connectors.Count < 2)
                {
                    foreach (var curve in allCurves)
                    {
                        if (curve?.ConnectorManager == null || usedCurves.Contains(curve.Id)) continue;
                        foreach (Connector c in curve.ConnectorManager.Connectors)
                        {
                            try
                            {
                                if (c.IsConnected) continue;
                                if (c.Origin.DistanceTo(junc.Center) < tolFt)
                                {
                                    connectors.Add(c);
                                    usedCurves.Add(curve.Id);
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }

                if (connectors.Count < 2) continue;

                try
                {
                    FamilyInstance placedFitting = null;

                    switch (junc.Kind)
                    {
                        case "elbow":
                            if (connectors.Count >= 2)
                            {
                                try
                                {
                                    placedFitting = _doc.Create.NewElbowFitting(connectors[0], connectors[1]);
                                }
                                catch (Exception ex)
                                {
                                    try { connectors[0].ConnectTo(connectors[1]); placedCount++; }
                                    catch { messages.Add($"Elbow at ({junc.Center.X:F1},{junc.Center.Y:F1}): {ex.Message}"); }
                                }
                            }
                            break;

                        case "tee":
                            if (connectors.Count >= 3)
                            {
                                Connector branch = PickBranchConnector(connectors);
                                var mains = new List<Connector>(connectors);
                                mains.Remove(branch);
                                try
                                {
                                    placedFitting = _doc.Create.NewTeeFitting(mains[0], mains[1], branch);
                                }
                                catch
                                {
                                    try { placedFitting = _doc.Create.NewElbowFitting(mains[0], branch); } catch { }
                                }
                            }
                            else if (connectors.Count == 2)
                            {
                                try { placedFitting = _doc.Create.NewElbowFitting(connectors[0], connectors[1]); } catch { }
                            }
                            break;

                        case "cross":
                            if (connectors.Count >= 4)
                            {
                                try
                                {
                                    placedFitting = _doc.Create.NewCrossFitting(connectors[0], connectors[1], connectors[2], connectors[3]);
                                }
                                catch
                                {
                                    try
                                    {
                                        Connector branch = PickBranchConnector(connectors);
                                        var mains = new List<Connector>(connectors);
                                        mains.Remove(branch);
                                        placedFitting = _doc.Create.NewTeeFitting(mains[0], mains[1], branch);
                                    }
                                    catch { }
                                }
                            }
                            break;

                        case "transition":
                        case "union":
                            if (connectors.Count >= 2)
                            {
                                try
                                {
                                    placedFitting = _doc.Create.NewTransitionFitting(connectors[0], connectors[1]);
                                }
                                catch
                                {
                                    try { placedFitting = _doc.Create.NewUnionFitting(connectors[0], connectors[1]); }
                                    catch { try { connectors[0].ConnectTo(connectors[1]); } catch { } }
                                }
                            }
                            break;
                    }

                    if (placedFitting != null)
                    {
                        placedCount++;
                    }
                }
                catch (Exception ex)
                {
                    messages.Add($"Junction fitting note: {ex.Message}");
                }
            }

            return placedCount;
        }

        private int AutoConnectRemainingEndpoints(List<MEPCurve> curves)
        {
            var openConns = new List<Connector>();
            foreach (var c in curves)
            {
                if (c?.ConnectorManager == null) continue;
                foreach (Connector conn in c.ConnectorManager.Connectors)
                {
                    if (!conn.IsConnected) openConns.Add(conn);
                }
            }

            const double tolFt = 0.50; // ~150mm
            int connected = 0;
            var used = new HashSet<int>();

            for (int i = 0; i < openConns.Count; i++)
            {
                if (used.Contains(i)) continue;
                var ci = openConns[i];
                XYZ oi;
                try { if (ci.IsConnected) continue; oi = ci.Origin; }
                catch { continue; }

                for (int j = i + 1; j < openConns.Count; j++)
                {
                    if (used.Contains(j)) continue;
                    var cj = openConns[j];
                    XYZ oj;
                    try
                    {
                        if (cj.IsConnected || ci.Owner.Id == cj.Owner.Id) continue;
                        oj = cj.Origin;
                    }
                    catch { continue; }

                    if (oi.DistanceTo(oj) <= tolFt)
                    {
                        try
                        {
                            var elbow = _doc.Create.NewElbowFitting(ci, cj);
                            if (elbow != null)
                            {
                                used.Add(i);
                                used.Add(j);
                                connected++;
                                break;
                            }
                        }
                        catch
                        {
                            try
                            {
                                var transition = _doc.Create.NewTransitionFitting(ci, cj);
                                if (transition != null)
                                {
                                    used.Add(i); used.Add(j); connected++; break;
                                }
                            }
                            catch
                            {
                                try
                                {
                                    ci.ConnectTo(cj);
                                    used.Add(i); used.Add(j); connected++; break;
                                }
                                catch { }
                            }
                        }
                    }
                }
            }

            return connected;
        }

        private static Connector PickBranchConnector(List<Connector> conns)
        {
            Connector best = conns[0];
            double bestAlign = -1.0;

            for (int i = 0; i < conns.Count; i++)
            {
                var others = new List<Connector>(conns);
                others.RemoveAt(i);
                if (others.Count < 2) continue;

                double align = Math.Abs(others[0].CoordinateSystem.BasisZ.DotProduct(others[1].CoordinateSystem.BasisZ));
                if (align > bestAlign)
                {
                    bestAlign = align;
                    best = conns[i];
                }
            }

            return best;
        }

        #endregion

        #region Network Traversal & Filtering

        public HashSet<ElementId> TraverseNetwork(ICollection<ElementId> seedIds, MepConversionMode mode)
        {
            var visited = new HashSet<ElementId>();
            var queue = new Queue<ElementId>();

            foreach (var id in seedIds)
            {
                Element elem = _doc.GetElement(id);
                if (elem != null)
                {
                    queue.Enqueue(id);
                    visited.Add(id);
                }
            }

            while (queue.Count > 0)
            {
                ElementId currentId = queue.Dequeue();
                Element currentElem = _doc.GetElement(currentId);
                if (currentElem == null) continue;

                ConnectorSet connectors = null;
                if (currentElem is MEPCurve curve)
                {
                    connectors = curve.ConnectorManager?.Connectors;
                }
                else if (currentElem is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
                {
                    connectors = fi.MEPModel.ConnectorManager.Connectors;
                }

                if (connectors == null) continue;

                foreach (Connector conn in connectors)
                {
                    List<Connector> refs = new List<Connector>();
                    try
                    {
                        foreach (Connector linked in conn.AllRefs)
                            refs.Add(linked);
                    }
                    catch { continue; }

                    foreach (Connector linked in refs)
                    {
                        Element neighbor = linked.Owner;
                        if (neighbor == null || visited.Contains(neighbor.Id)) continue;

                        if (neighbor is MEPCurve nc)
                        {
                            if (IsCompatibleCurve(nc, mode))
                            {
                                visited.Add(neighbor.Id);
                                queue.Enqueue(neighbor.Id);
                            }
                        }
                        else if (neighbor is FamilyInstance nfi && IsMepFitting(nfi))
                        {
                            visited.Add(neighbor.Id);
                            queue.Enqueue(neighbor.Id);
                        }
                    }
                }
            }

            return visited;
        }

        public bool IsCompatibleCurve(MEPCurve curve, MepConversionMode mode)
        {
            if (curve == null) return false;

            switch (mode)
            {
                case MepConversionMode.RoundDuctToPipe:
                    if (curve is Duct duct)
                    {
                        var shapeParam = duct.DuctType?.Shape;
                        if (shapeParam.HasValue && shapeParam.Value == ConnectorProfileType.Round)
                            return true;

                        var dParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                        if (dParam != null && dParam.HasValue && dParam.AsDouble() > 0)
                            return true;

                        string typeName = duct.DuctType?.Name?.ToLowerInvariant() ?? "";
                        if (typeName.Contains("round")) return true;

                        var wParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                        var hParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                        if (wParam != null && hParam != null && Math.Abs(wParam.AsDouble() - hParam.AsDouble()) < 0.01)
                            return true;
                    }
                    return false;

                case MepConversionMode.PipeToConduit:
                case MepConversionMode.PipeToRoundDuct:
                    return curve is Pipe;

                case MepConversionMode.ConduitToPipe:
                    return curve is Conduit;

                default:
                    return false;
            }
        }

        public static bool IsMepFitting(FamilyInstance fi)
        {
            if (fi == null || fi.Category == null) return false;
            var catId = fi.Category.Id;
            return catId == PipeFittingCatId ||
                   catId == DuctFittingCatId ||
                   catId == ConduitFittingCatId;
        }

        private static string ClassifyFittingKind(FamilyInstance fitting, int connectedCount)
        {
            string name = (fitting.Symbol?.Family?.Name ?? "") + " " + (fitting.Name ?? "");
            name = name.ToLowerInvariant();

            if (name.Contains("cross")) return "cross";
            if (name.Contains("tee") || name.Contains("wye") || name.Contains("branch")) return "tee";
            if (name.Contains("transition") || name.Contains("reducer")) return "transition";
            if (name.Contains("union") || name.Contains("coupling")) return "union";
            if (name.Contains("elbow") || name.Contains("bend")) return "elbow";

            if (connectedCount >= 4) return "cross";
            if (connectedCount == 3) return "tee";
            return "elbow";
        }

        #endregion

        #region Helpers

        private double GetDiameter(MEPCurve curve)
        {
            if (curve is Pipe pipe)
            {
                var p = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (p != null && p.HasValue) return p.AsDouble();
                return pipe.Diameter;
            }
            if (curve is Conduit conduit)
            {
                var p = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)
                     ?? conduit.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                     ?? conduit.LookupParameter("Diameter");
                if (p != null && p.HasValue) return p.AsDouble();
                return conduit.Diameter;
            }
            if (curve is Duct duct)
            {
                var p = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                     ?? duct.LookupParameter("Diameter");
                if (p != null && p.HasValue) return p.AsDouble();
                try { return duct.Diameter; } catch { }
                var w = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                if (w != null && w.HasValue) return w.AsDouble();
            }
            return 0.1667; // default 2 inches (2/12 ft)
        }

        private static void SetDiameterParameter(Element el, BuiltInParameter bip, double val)
        {
            if (val <= 0 || el == null) return;
            var param = el.get_Parameter(bip);
            if (param != null && !param.IsReadOnly)
            {
                try { param.Set(val); } catch { }
            }
            else
            {
                var fallback = el.LookupParameter("Diameter") ?? el.LookupParameter("Size");
                if (fallback != null && !fallback.IsReadOnly)
                {
                    try { fallback.Set(val); } catch { }
                }
            }
        }

        private void CopyMepParameters(MEPCurve src, MEPCurve target)
        {
            try
            {
                string comments = src.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                if (!string.IsNullOrEmpty(comments))
                {
                    target.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(comments);
                }

                string mark = src.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                if (!string.IsNullOrEmpty(mark))
                {
                    target.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
                }
            }
            catch { }
        }

        private void ResolveTargetTypes(MepConversionOptions options)
        {
            switch (options.Mode)
            {
                case MepConversionMode.RoundDuctToPipe:
                case MepConversionMode.ConduitToPipe:
                    if (options.TargetTypeId == ElementId.InvalidElementId)
                    {
                        var pt = new FilteredElementCollector(_doc).OfClass(typeof(PipeType)).Cast<PipeType>().FirstOrDefault();
                        if (pt != null) options.TargetTypeId = pt.Id;
                    }
                    if (options.TargetSystemTypeId == ElementId.InvalidElementId)
                    {
                        var pst = new FilteredElementCollector(_doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>()
                            .FirstOrDefault(st => st.SystemClassification == MEPSystemClassification.SupplyHydronic)
                            ?? new FilteredElementCollector(_doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().FirstOrDefault();
                        if (pst != null) options.TargetSystemTypeId = pst.Id;
                    }
                    break;

                case MepConversionMode.PipeToConduit:
                    if (options.TargetTypeId == ElementId.InvalidElementId)
                    {
                        var ct = new FilteredElementCollector(_doc).OfClass(typeof(ConduitType)).Cast<ConduitType>().FirstOrDefault();
                        if (ct != null) options.TargetTypeId = ct.Id;
                    }
                    break;

                case MepConversionMode.PipeToRoundDuct:
                    if (options.TargetTypeId == ElementId.InvalidElementId)
                    {
                        var dt = new FilteredElementCollector(_doc).OfClass(typeof(DuctType)).Cast<DuctType>()
                            .FirstOrDefault(d => d.Shape == ConnectorProfileType.Round || d.Name.ToLowerInvariant().Contains("round"))
                            ?? new FilteredElementCollector(_doc).OfClass(typeof(DuctType)).Cast<DuctType>().FirstOrDefault();
                        if (dt != null) options.TargetTypeId = dt.Id;
                    }
                    if (options.TargetSystemTypeId == ElementId.InvalidElementId)
                    {
                        var mst = new FilteredElementCollector(_doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>()
                            .FirstOrDefault(st => st.SystemClassification == MEPSystemClassification.SupplyAir || st.SystemClassification == MEPSystemClassification.ReturnAir)
                            ?? new FilteredElementCollector(_doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().FirstOrDefault();
                        if (mst != null) options.TargetSystemTypeId = mst.Id;
                    }
                    break;
            }
        }

        #endregion
    }
}
