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
    /// Shared sacred-geometry helpers for every MEP operation tool.
    /// Rules: far end of an anchored run never moves; only the near end trims/extends;
    /// double-anchored runs try native connect first, then dummy/logical if Revit would snap.
    /// </summary>
    public partial class MepOperationService
    {
        private const double DisplacementTolFt = 0.0033; // 1 mm
        private const double MinRunLenFt = 0.05;

        /// <summary>
        /// Auto-picks Disconnect on Revit's network-connectivity errors so dummy/logical
        /// reconnect can proceed without a modal dialog.
        /// </summary>
        private sealed class DisconnectNetworkFailures : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                IList<FailureMessageAccessor> fails = accessor.GetFailureMessages();
                if (fails == null || fails.Count == 0)
                    return FailureProcessingResult.Continue;

                bool resolvedError = false;
                foreach (FailureMessageAccessor f in fails)
                {
                    try
                    {
                        if (f.GetSeverity() == FailureSeverity.Warning)
                        {
                            accessor.DeleteWarning(f);
                            continue;
                        }

                        FailureDefinitionId fid = f.GetFailureDefinitionId();
                        string text = "";
                        try { text = f.GetDescriptionText() ?? ""; } catch { }
                        bool opposite =
                            fid == BuiltInFailures.PipingFailures.DuctPipeModified
                            || fid == BuiltInFailures.ElectricalFailures.ConduitModified
                            || fid == BuiltInFailures.ElectricalFailures.CableTrayModified
                            || text.IndexOf("opposite direction", StringComparison.OrdinalIgnoreCase) >= 0
                            || text.IndexOf("connections to be invalid", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool network = opposite
                            || fid == BuiltInFailures.ConnectorFailures.NetworkValidityErrorWasDisconnected
                            || fid == BuiltInFailures.ConnectorFailures.ElementsNeedToBeDisconnected
                            || fid == BuiltInFailures.ConnectorFailures.ElementsAreDisconnected
                            || fid == BuiltInFailures.ConnectorFailures.DisconnectedElements
                            || text.IndexOf("no longer keep the connectivity", StringComparison.OrdinalIgnoreCase) >= 0;

                        // Never DeleteElements. Default on opposite-direction is Delete.
                        // Other network errors may only offer Default — take that.
                        if (network && f.HasResolutions())
                        {
                            bool picked = false;
                            if (f.HasResolutionOfType(FailureResolutionType.DetachElements))
                            {
                                f.SetCurrentResolutionType(FailureResolutionType.DetachElements);
                                picked = true;
                            }
                            else if (f.HasResolutionOfType(FailureResolutionType.SkipElements))
                            {
                                f.SetCurrentResolutionType(FailureResolutionType.SkipElements);
                                picked = true;
                            }
                            else if (f.HasResolutionOfType(FailureResolutionType.FixElements))
                            {
                                f.SetCurrentResolutionType(FailureResolutionType.FixElements);
                                picked = true;
                            }
                            else if (!opposite && f.HasResolutionOfType(FailureResolutionType.Default))
                            {
                                f.SetCurrentResolutionType(FailureResolutionType.Default);
                                picked = true;
                            }
                            if (picked)
                            {
                                accessor.ResolveFailure(f);
                                resolvedError = true;
                            }
                        }
                    }
                    catch { }
                }

                return resolvedError
                    ? FailureProcessingResult.ProceedWithCommit
                    : FailureProcessingResult.Continue;
            }
        }

        public static void AllowNetworkDisconnects(Transaction t)
        {
            if (t == null) return;
            FailureHandlingOptions opts = t.GetFailureHandlingOptions();
            opts.SetFailuresPreprocessor(new DisconnectNetworkFailures());
            opts.SetClearAfterRollback(true);
            opts.SetForcedModalHandling(false);
            t.SetFailureHandlingOptions(opts);
        }

        private void DisconnectFittingFromRuns(FamilyInstance fitting)
        {
            if (!IsLive(fitting) || fitting.MEPModel?.ConnectorManager == null) return;
            foreach (Connector fc in fitting.MEPModel.ConnectorManager.Connectors)
            {
                var refs = new List<Connector>();
                try
                {
                    foreach (Connector r in fc.AllRefs)
                    {
                        if (r.Owner != null && r.Owner.Id != fitting.Id)
                            refs.Add(r);
                    }
                }
                catch { }
                foreach (var r in refs)
                {
                    try { fc.DisconnectFrom(r); } catch { }
                    try { r.DisconnectFrom(fc); } catch { }
                }
            }
            try { _doc.Regenerate(); } catch { }
        }

        private static bool TryGetLine(MEPCurve curve, out LocationCurve lc, out Line line, out XYZ p0, out XYZ p1)
        {
            lc = curve != null ? curve.Location as LocationCurve : null;
            line = lc != null ? lc.Curve as Line : null;
            if (line == null)
            {
                p0 = null;
                p1 = null;
                return false;
            }
            p0 = line.GetEndPoint(0);
            p1 = line.GetEndPoint(1);
            return true;
        }

        private static XYZ PickFarEnd(XYZ p0, XYZ p1, XYZ apex, XYZ click)
        {
            bool isInside = (p0.DistanceTo(apex) + p1.DistanceTo(apex)) - p0.DistanceTo(p1) < 0.05;
            if (isInside && click != null)
            {
                XYZ vClick = (click - apex).Normalize();
                XYZ v0 = (p0 - apex).Normalize();
                XYZ v1 = (p1 - apex).Normalize();
                return vClick.DotProduct(v0) > vClick.DotProduct(v1) ? p0 : p1;
            }
            return p0.DistanceTo(apex) >= p1.DistanceTo(apex) ? p0 : p1;
        }

        private static bool P0IsFar(XYZ p0, XYZ farPt)
        {
            return p0.DistanceTo(farPt) < 0.001;
        }

        private static void SetCurvePreserveFar(LocationCurve lc, XYZ farPt, XYZ nearPt, bool p0IsFar)
        {
            if (lc == null || farPt == null || nearPt == null) return;
            if (farPt.DistanceTo(nearPt) < 1e-6) return;

            // Geometry is always {far, near}. Only the start→end order may vary.
            // Revit posts DuctPipeOpositDirection if that order is reversed on a
            // connected pipe — pick the candidate whose direction matches live.
            Line candFarStart = Line.CreateBound(farPt, nearPt);
            Line candNearStart = Line.CreateBound(nearPt, farPt);

            Line live = lc.Curve as Line;
            Line chosen;
            if (live == null)
            {
                chosen = p0IsFar ? candFarStart : candNearStart;
            }
            else
            {
                XYZ liveDir = live.GetEndPoint(1) - live.GetEndPoint(0);
                XYZ dirFarStart = nearPt - farPt;
                chosen = liveDir.DotProduct(dirFarStart) >= 0 ? candFarStart : candNearStart;
                XYZ lp0 = live.GetEndPoint(0);
                XYZ lp1 = live.GetEndPoint(1);
                if (lp0.DistanceTo(chosen.GetEndPoint(0)) <= DisplacementTolFt
                    && lp1.DistanceTo(chosen.GetEndPoint(1)) <= DisplacementTolFt)
                    return;
            }
            lc.Curve = chosen;
        }

        /// <summary>
        /// Project a target onto the existing far→axis ray. Far point and angle stay fixed.
        /// </summary>
        private static XYZ ProjectNearOnSacredAxis(XYZ farPt, XYZ axisFromFar, XYZ target, double minLen)
        {
            double t = (target - farPt).DotProduct(axisFromFar);
            if (t < minLen) t = minLen;
            return farPt + axisFromFar.Multiply(t);
        }

        private static bool IsPhysicallyConnected(Connector c)
        {
            if (c == null || !c.IsConnected) return false;
            try
            {
                foreach (Connector r in c.AllRefs)
                {
                    if (r.Owner != null && r.Owner.Id != c.Owner.Id && r.ConnectorType != ConnectorType.Logical)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private List<XYZ> CollectAnchoredEndPoints(params MEPCurve[] curves)
        {
            var pts = new List<XYZ>();
            if (curves == null) return pts;
            foreach (var curve in curves)
            {
                if (!TryGetLine(curve, out _, out _, out XYZ p0, out XYZ p1)) continue;
                if (IsPhysicallyConnected(FindClosestConnector(curve.ConnectorManager, p0)))
                    pts.Add(p0);
                if (IsPhysicallyConnected(FindClosestConnector(curve.ConnectorManager, p1)))
                    pts.Add(p1);
            }
            return pts;
        }

        private bool AnchoredPointsPreserved(List<XYZ> original, params MEPCurve[] curves)
        {
            if (original == null || original.Count == 0) return true;
            foreach (var expected in original)
            {
                bool found = false;
                foreach (var curve in curves)
                {
                    if (curve == null) continue;
                    try
                    {
                        if (!TryGetLine(curve, out _, out _, out XYZ p0, out XYZ p1)) continue;
                        if (p0.DistanceTo(expected) <= DisplacementTolFt || p1.DistanceTo(expected) <= DisplacementTolFt)
                        {
                            found = true;
                            break;
                        }
                    }
                    catch { }
                }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>
        /// Preemptive Geometry Shield: run native fitting in a SubTransaction and
        /// roll it back if any anchored far end moved by ≥ 1 mm.
        /// </summary>
        private bool TryNativeFitting(Func<FamilyInstance> create, List<XYZ> anchoredPoints, MEPCurve[] involved, out FamilyInstance fitting)
        {
            fitting = null;
            using (SubTransaction sub = new SubTransaction(_doc))
            {
                sub.Start();
                try
                {
                    fitting = create();
                    _doc.Regenerate();
                    if (fitting == null || !AnchoredPointsPreserved(anchoredPoints, involved))
                    {
                        fitting = null;
                        sub.RollBack();
                        return false;
                    }
                    sub.Commit();
                    return true;
                }
                catch
                {
                    fitting = null;
                    try { sub.RollBack(); } catch { }
                    return false;
                }
            }
        }

        private MEPCurve CreateDummyCurve(MEPCurve template, XYZ start, XYZ end)
        {
            if (template == null || start == null || end == null) return null;
            if (start.DistanceTo(end) < MinRunLenFt) return null;

            // Always a NEW segment. Never CopyElement of a live networked pipe
            // (copying keeps system ties; deleting the copy can delete the original).
            try
            {
                MEPCurve dummy = CreateDuplicateCurveSegment(template, start, end);
                if (dummy != null)
                {
                    DisconnectAllPhysical(dummy);
                    return dummy;
                }
            }
            catch { }

            return null;
        }

        private static void DisconnectAllPhysical(MEPCurve curve)
        {
            if (curve?.ConnectorManager == null) return;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                if (!c.IsConnected) continue;
                var refs = new List<Connector>();
                try
                {
                    foreach (Connector r in c.AllRefs)
                    {
                        if (r.Owner != null && r.Owner.Id != curve.Id && r.ConnectorType != ConnectorType.Logical)
                            refs.Add(r);
                    }
                }
                catch { }
                foreach (var r in refs)
                {
                    try { c.DisconnectFrom(r); } catch { }
                }
            }
        }

        private static void DeleteDummy(Document doc, MEPCurve dummy)
        {
            DeleteDummy(doc, dummy, null);
        }

        private static void DeleteDummy(Document doc, MEPCurve dummy, ICollection<ElementId> protect)
        {
            if (dummy == null) return;
            try
            {
                if (!dummy.IsValidObject) return;
                if (protect != null && protect.Contains(dummy.Id)) return;
                DisconnectAllPhysical(dummy);
                doc.Delete(dummy.Id);
                doc.Regenerate();
            }
            catch { }
        }

        private static bool IsLive(Element e)
        {
            try { return e != null && e.IsValidObject; }
            catch { return false; }
        }

        /// <summary>
        /// A physical connector pair, stored by id+point so it survives regenerate.
        /// </summary>
        private struct PhysicalLink
        {
            public ElementId RunId;
            public XYZ RunPt;
            public ElementId PartnerId;
            public XYZ PartnerPt;
        }

        private List<PhysicalLink> CapturePhysicalLinks(MEPCurve curve, ElementId skipPartner)
        {
            var list = new List<PhysicalLink>();
            if (curve?.ConnectorManager == null) return list;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                if (!c.IsConnected) continue;
                try
                {
                    foreach (Connector r in c.AllRefs)
                    {
                        if (r.Owner == null || r.Owner.Id == curve.Id) continue;
                        if (r.ConnectorType == ConnectorType.Logical) continue;
                        if (skipPartner != null && skipPartner != ElementId.InvalidElementId && r.Owner.Id == skipPartner)
                            continue;
                        list.Add(new PhysicalLink
                        {
                            RunId = curve.Id,
                            RunPt = c.Origin,
                            PartnerId = r.Owner.Id,
                            PartnerPt = r.Origin
                        });
                    }
                }
                catch { }
            }
            return list;
        }

        private Connector FindConnectorOnElement(Element e, XYZ pt)
        {
            if (e == null || pt == null) return null;
            ConnectorManager cm = null;
            try
            {
                if (e is MEPCurve mc) cm = mc.ConnectorManager;
                else if (e is FamilyInstance fi) cm = fi.MEPModel?.ConnectorManager;
            }
            catch { return null; }
            return FindClosestConnector(cm, pt);
        }

        private void DisconnectPhysicalLinks(IList<PhysicalLink> links)
        {
            if (links == null) return;
            foreach (PhysicalLink link in links)
            {
                try
                {
                    Element run = _doc.GetElement(link.RunId);
                    Element partner = _doc.GetElement(link.PartnerId);
                    if (!IsLive(run) || !IsLive(partner)) continue;
                    Connector a = FindConnectorOnElement(run, link.RunPt);
                    Connector b = FindConnectorOnElement(partner, link.PartnerPt);
                    if (a == null || b == null) continue;
                    try { a.DisconnectFrom(b); } catch { }
                    try { b.DisconnectFrom(a); } catch { }
                }
                catch { }
            }
            try { _doc.Regenerate(); } catch { }
        }

        private void ReconnectPhysicalLinks(IList<PhysicalLink> links)
        {
            if (links == null) return;
            foreach (PhysicalLink link in links)
            {
                try
                {
                    Element run = _doc.GetElement(link.RunId);
                    Element partner = _doc.GetElement(link.PartnerId);
                    if (!IsLive(run) || !IsLive(partner)) continue;
                    Connector a = FindConnectorOnElement(run, link.RunPt);
                    Connector b = FindConnectorOnElement(partner, link.PartnerPt);
                    if (a == null || b == null) continue;
                    try
                    {
                        if (!a.IsConnectedTo(b))
                            a.ConnectTo(b);
                    }
                    catch { }
                }
                catch { }
            }
            try { _doc.Regenerate(); } catch { }
        }

        /// <summary>
        /// Dummy continues the host through-run without reversing the host
        /// LocationCurve (that posts opposite-direction on an anchored far end).
        /// Host Bound(far, joint) → dummy Bound(joint, dummyFar).
        /// Host Bound(joint, far) → dummy Bound(dummyFar, joint).
        /// </summary>
        private static void OrientDummyAsHostContinuation(MEPCurve host, XYZ joint, MEPCurve dummy)
        {
            if (host == null || dummy == null || joint == null) return;
            if (!TryGetLine(host, out _, out _, out XYZ hp0, out XYZ hp1)) return;
            if (!TryGetLine(dummy, out LocationCurve dlc, out _, out XYZ dp0, out XYZ dp1)) return;

            bool hostP0AtJoint = hp0.DistanceTo(joint) <= hp1.DistanceTo(joint);
            bool dummyP0AtJoint = dp0.DistanceTo(joint) <= dp1.DistanceTo(joint);
            if (dummyP0AtJoint == hostP0AtJoint)
                dlc.Curve = Line.CreateBound(dp1, dp0);
        }

        private MEPCurve LiveCurve(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return null;
            try
            {
                var c = _doc.GetElement(id) as MEPCurve;
                return IsLive(c) ? c : null;
            }
            catch { return null; }
        }

        private FamilyInstance LiveFitting(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return null;
            try
            {
                var f = _doc.GetElement(id) as FamilyInstance;
                return IsLive(f) ? f : null;
            }
            catch { return null; }
        }

        private Connector LiveOpenNear(ElementId curveId, XYZ pt, double tol)
        {
            MEPCurve run = LiveCurve(curveId);
            if (run == null) return null;
            try { return FindOpenOnRunNear(run, pt, tol); }
            catch { return null; }
        }

        private static Connector FirstUnconnectedFittingConnector(FamilyInstance fitting)
        {
            if (fitting?.MEPModel?.ConnectorManager == null) return null;
            foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors)
            {
                if (!c.IsConnected) return c;
            }
            return null;
        }

        private static void LogicalConnect(Connector a, Connector b)
        {
            if (a == null || b == null) return;
            try { a.ConnectTo(b); } catch { }
        }

        private void SacredTrimToSocket(MEPCurve curve, XYZ farPt, bool p0IsFar, XYZ socketOrigin)
        {
            if (!TryGetLine(curve, out LocationCurve lc, out _, out XYZ p0, out XYZ p1)) return;
            XYZ axis = p0IsFar ? (p1 - p0) : (p0 - p1);
            if (axis.GetLength() < 1e-9) return;
            axis = axis.Normalize();
            XYZ near = ProjectNearOnSacredAxis(farPt, axis, socketOrigin, MinRunLenFt);
            SetCurvePreserveFar(lc, farPt, near, p0IsFar);
        }

        /// <summary>
        /// Stop each run at the fitting socket so the pipe does not travel through the
        /// elbow body. Only the near end moves, along the existing axis. The fitting is
        /// not nudged (auto-trim stays suppressed on anchored runs).
        /// </summary>
        private void TrimRunsToFittingSockets(FamilyInstance fitting, params MEPCurve[] runs)
        {
            if (fitting?.MEPModel?.ConnectorManager == null || runs == null) return;
            _doc.Regenerate();

            XYZ fitCenter = (fitting.Location as LocationPoint)?.Point;

            foreach (MEPCurve run in runs)
            {
                if (run == null) continue;
                if (!TryGetLine(run, out LocationCurve lc, out _, out XYZ p0, out XYZ p1)) continue;

                Connector fittingConn = null;
                Connector runConn = null;
                foreach (Connector fc in fitting.MEPModel.ConnectorManager.Connectors)
                {
                    try
                    {
                        if (!fc.IsConnected) continue;
                        foreach (Connector rc in fc.AllRefs)
                        {
                            if (rc.Owner != null && rc.Owner.Id == run.Id)
                            {
                                fittingConn = fc;
                                runConn = rc;
                                break;
                            }
                        }
                    }
                    catch { }
                    if (fittingConn != null) break;
                }

                if (fittingConn == null)
                {
                    double bestD = double.MaxValue;
                    foreach (Connector fc in fitting.MEPModel.ConnectorManager.Connectors)
                    {
                        double d = Math.Min(fc.Origin.DistanceTo(p0), fc.Origin.DistanceTo(p1));
                        if (d < bestD)
                        {
                            bestD = d;
                            fittingConn = fc;
                        }
                    }
                }

                if (fittingConn == null) continue;

                XYZ socketPt = GetSocketPoint(fitting, fittingConn, fitCenter);
                bool p0IsNear = p0.DistanceTo(socketPt) <= p1.DistanceTo(socketPt);
                XYZ farPt = p0IsNear ? p1 : p0;
                bool p0IsFar = !p0IsNear;
                XYZ currentNear = p0IsNear ? p0 : p1;
                XYZ axis = SafeNormalize(currentNear - farPt);
                XYZ newNear = ProjectNearOnSacredAxis(farPt, axis, socketPt, MinRunLenFt);

                if (currentNear.DistanceTo(newNear) <= DisplacementTolFt)
                    continue;

                bool wasConnected = false;
                if (runConn != null && fittingConn != null)
                {
                    try { wasConnected = runConn.IsConnectedTo(fittingConn); } catch { }
                    if (wasConnected)
                    {
                        try { runConn.DisconnectFrom(fittingConn); } catch { }
                    }
                }

                SetCurvePreserveFar(lc, farPt, newNear, p0IsFar);
                _doc.Regenerate();

                Connector liveRun = FindOpenConnectorNear(run.ConnectorManager, newNear, 1.0)
                    ?? FindClosestConnector(run.ConnectorManager, newNear);
                Connector liveFit = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                    .OrderBy(fc => fc.IsConnected ? 1 : 0)
                    .ThenBy(fc => fc.Origin.DistanceTo(socketPt))
                    .FirstOrDefault() ?? fittingConn;
                LogicalConnect(liveRun, liveFit);
                _doc.Regenerate();

                if (TryGetLine(run, out LocationCurve lcAfter, out _, out XYZ a, out XYZ b))
                {
                    XYZ curFar = a.DistanceTo(farPt) <= b.DistanceTo(farPt) ? a : b;
                    if (curFar.DistanceTo(farPt) > DisplacementTolFt)
                        SetCurvePreserveFar(lcAfter, farPt, newNear, p0IsFar);
                }
            }

            _doc.Regenerate();
        }

        private static XYZ GetSocketPoint(FamilyInstance fitting, Connector fittingConn, XYZ fitCenter)
        {
            XYZ origin = fittingConn.Origin;

            // Union / coupling / reducer: two opposite sockets. Origin is already the face.
            // Do not offset outward — that would leave a physical gap while Tab still works.
            try
            {
                var conns = fitting.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>().ToList();
                if (conns != null && conns.Count == 2)
                {
                    XYZ z0 = SafeNormalize(conns[0].CoordinateSystem.BasisZ);
                    XYZ z1 = SafeNormalize(conns[1].CoordinateSystem.BasisZ);
                    if (z0.DotProduct(z1) < -0.5)
                        return origin;
                }
            }
            catch { }

            XYZ outward = SafeNormalize(fittingConn.CoordinateSystem.BasisZ);
            double r = 0;
            try
            {
                if (fittingConn.Shape == ConnectorProfileType.Round)
                    r = fittingConn.Radius;
                else
                    r = Math.Max(fittingConn.Width, fittingConn.Height) * 0.5;
            }
            catch { }

            int nConn = 0;
            try { nConn = fitting.MEPModel.ConnectorManager.Connectors.Size; } catch { }

            // Tee / cross: stop the pipe at the socket face, not the fitting origin.
            if (nConn >= 3 && fitCenter != null)
            {
                double takeout = Math.Max(r > 1e-6 ? r * 1.35 : 0.12, 0.10);
                if (origin.DistanceTo(fitCenter) >= takeout * 0.9)
                    return origin;
                return fitCenter + outward.Multiply(takeout);
            }

            if (fitCenter != null && r > 1e-6 && origin.DistanceTo(fitCenter) < r * 0.75)
            {
                double takeout = Math.Max(r * 1.5, 0.08);
                return origin + outward.Multiply(takeout);
            }

            return origin;
        }

        private XYZ SocketOnAxis(FamilyInstance fitting, FarEndSnap snap, XYZ center, XYZ fallback)
        {
            XYZ hint = center ?? fallback ?? snap.Far;
            if (!IsLive(fitting) || fitting.MEPModel?.ConnectorManager == null)
                return fallback ?? hint;

            Connector fc = NearestFittingConnector(fitting, snap.Curve, hint);
            try
            {
                XYZ towardFar = SafeNormalize(snap.Far - hint);
                double best = -2;
                Connector byDir = null;
                foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors)
                {
                    double align = SafeNormalize(c.CoordinateSystem.BasisZ).DotProduct(towardFar);
                    if (align > best)
                    {
                        best = align;
                        byDir = c;
                    }
                }
                if (byDir != null && best > 0.25)
                    fc = byDir;
            }
            catch { }

            if (fc == null) return fallback ?? hint;
            return GetSocketPoint(fitting, fc, hint);
        }

        /// <summary>
        /// Rotate and translate a union/coupling so its axis matches the two pipe
        /// near-ends. Pipes are not dragged (fitting is disconnected first).
        /// Far ends stay where they are.
        /// </summary>
        private void AlignUnionToPipeEnds(FamilyInstance fitting, MEPCurve curve1, MEPCurve curve2)
        {
            if (fitting?.MEPModel?.ConnectorManager == null) return;
            if (!TryGetLine(curve1, out _, out _, out XYZ a0, out XYZ a1)) return;
            if (!TryGetLine(curve2, out _, out _, out XYZ b0, out XYZ b1)) return;

            DisconnectFittingFromRuns(fitting, curve1, curve2);
            _doc.Regenerate();

            var fcs = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                .Where(c => c.ConnectorType != ConnectorType.Logical)
                .ToList();
            if (fcs.Count < 2) return;

            XYZ f0 = fcs[0].Origin;
            XYZ f1 = fcs[1].Origin;
            XYZ fitMid = (f0 + f1) * 0.5;

            XYZ near1 = a0.DistanceTo(fitMid) <= a1.DistanceTo(fitMid) ? a0 : a1;
            XYZ near2 = b0.DistanceTo(fitMid) <= b1.DistanceTo(fitMid) ? b0 : b1;

            XYZ endDelta = near2 - near1;
            if (endDelta.GetLength() < 1e-6) return;
            XYZ desiredAxis = endDelta.Normalize();

            XYZ curAxis = SafeNormalize(f1 - f0);
            if (curAxis.DotProduct(desiredAxis) < 0)
                curAxis = curAxis.Negate();

            double ang = curAxis.AngleTo(desiredAxis);
            if (ang > 1e-6)
            {
                XYZ rotAxis = curAxis.CrossProduct(desiredAxis);
                if (rotAxis.GetLength() < 1e-8)
                {
                    rotAxis = curAxis.CrossProduct(XYZ.BasisZ);
                    if (rotAxis.GetLength() < 1e-8)
                        rotAxis = curAxis.CrossProduct(XYZ.BasisX);
                    ang = Math.PI;
                }
                rotAxis = rotAxis.Normalize();
                try
                {
                    ElementTransformUtils.RotateElement(_doc, fitting.Id, Line.CreateUnbound(fitMid, rotAxis), ang);
                }
                catch { }
                _doc.Regenerate();
            }

            fcs = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                .Where(c => c.ConnectorType != ConnectorType.Logical)
                .ToList();
            if (fcs.Count < 2) return;
            f0 = fcs[0].Origin;
            f1 = fcs[1].Origin;
            fitMid = (f0 + f1) * 0.5;

            XYZ targetMid = (near1 + near2) * 0.5;
            XYZ move = targetMid - fitMid;
            if (move.GetLength() > 1e-6)
            {
                try { ElementTransformUtils.MoveElement(_doc, fitting.Id, move); } catch { }
                _doc.Regenerate();
            }

            // If the union is 180° flipped relative to the pipes, sockets still sit
            // on the same axis; pairing is handled by EnsurePhysicalUnionConnections.
        }

        private void DisconnectFittingFromRuns(FamilyInstance fitting, params MEPCurve[] runs)
        {
            if (fitting?.MEPModel?.ConnectorManager == null || runs == null) return;
            var runIds = new HashSet<ElementId>(runs.Where(r => r != null).Select(r => r.Id));
            foreach (Connector fc in fitting.MEPModel.ConnectorManager.Connectors)
            {
                if (!fc.IsConnected) continue;
                var refs = new List<Connector>();
                try
                {
                    foreach (Connector r in fc.AllRefs)
                    {
                        if (r.Owner != null && runIds.Contains(r.Owner.Id))
                            refs.Add(r);
                    }
                }
                catch { }
                foreach (var r in refs)
                {
                    try { fc.DisconnectFrom(r); } catch { }
                }
            }
        }

        /// <summary>
        /// Pair each run's near connector to the matching union socket.
        /// System interconnection (Tab) is not enough — this is the physical ConnectTo.
        /// </summary>
        private void EnsurePhysicalUnionConnections(FamilyInstance fitting, MEPCurve curve1, MEPCurve curve2)
        {
            if (fitting?.MEPModel?.ConnectorManager == null) return;
            _doc.Regenerate();

            var fitConns = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>().ToList();
            if (fitConns.Count < 2) return;

            PairRunToNearestSocket(curve1, fitConns);
            PairRunToNearestSocket(curve2, fitConns);
            _doc.Regenerate();
        }

        private void PairRunToNearestSocket(MEPCurve run, List<Connector> fitConns)
        {
            if (run?.ConnectorManager == null) return;
            if (!TryGetLine(run, out _, out _, out XYZ p0, out XYZ p1)) return;

            XYZ fitCenter = XYZ.Zero;
            foreach (Connector fc in fitConns) fitCenter += fc.Origin;
            fitCenter = fitCenter / fitConns.Count;

            Connector runNear = p0.DistanceTo(fitCenter) <= p1.DistanceTo(fitCenter)
                ? FindClosestConnector(run.ConnectorManager, p0)
                : FindClosestConnector(run.ConnectorManager, p1);
            if (runNear == null) return;

            Connector socket = fitConns
                .OrderBy(fc => fc.Origin.DistanceTo(runNear.Origin))
                .FirstOrDefault();
            if (socket == null) return;

            try
            {
                if (runNear.IsConnectedTo(socket)) return;
            }
            catch { }

            if (runNear.IsConnected)
            {
                var stale = new List<Connector>();
                try
                {
                    foreach (Connector r in runNear.AllRefs)
                    {
                        if (r.Owner != null && r.Owner.Id != run.Id && r.ConnectorType != ConnectorType.Logical)
                            stale.Add(r);
                    }
                }
                catch { }
                foreach (var s in stale)
                {
                    try { runNear.DisconnectFrom(s); } catch { }
                }
            }

            LogicalConnect(runNear, socket);
        }

        private static void RigidShiftCurveZ(LocationCurve lc, double dz)
        {
            if (lc == null || !(lc.Curve is Line line) || Math.Abs(dz) < 1e-9) return;
            XYZ a = line.GetEndPoint(0);
            XYZ b = line.GetEndPoint(1);
            lc.Curve = Line.CreateBound(new XYZ(a.X, a.Y, a.Z + dz), new XYZ(b.X, b.Y, b.Z + dz));
        }

        /// <summary>
        /// Near end may move; far end never does.
        /// Anchored (network): near slides on the existing axis only.
        /// Free: far point stays, near may go to the target (slope may change).
        /// </summary>
        private void PrepareRunToJunction(MEPCurve curve, XYZ target, out XYZ farPt, out bool p0IsFar, out bool anchored)
        {
            farPt = target;
            p0IsFar = true;
            anchored = false;
            if (curve == null || target == null) return;
            if (!TryGetLine(curve, out LocationCurve lc, out _, out XYZ p0, out XYZ p1)) return;

            farPt = PickFarEnd(p0, p1, target, null);
            p0IsFar = P0IsFar(p0, farPt);
            anchored = IsFarEndAnchored(curve, target);
            XYZ axisFromFar = SafeNormalize(p0IsFar ? (p1 - p0) : (p0 - p1));

            XYZ near = anchored
                ? ProjectNearOnSacredAxis(farPt, axisFromFar, target, MinRunLenFt)
                : target;
            if (farPt.DistanceTo(near) >= MinRunLenFt)
                SetCurvePreserveFar(lc, farPt, near, p0IsFar);

            _doc.Regenerate();
            if (TryGetLine(curve, out _, out _, out p0, out p1))
            {
                farPt = PickFarEnd(p0, p1, target, null);
                p0IsFar = P0IsFar(p0, farPt);
            }
        }

        private static Connector FindOpenOnRunNear(MEPCurve run, XYZ pt, double tol)
        {
            if (run?.ConnectorManager == null) return null;
            return FindOpenConnectorNear(run.ConnectorManager, pt, tol)
                ?? FindClosestConnector(run.ConnectorManager, pt);
        }

        private struct FarEndSnap
        {
            public MEPCurve Curve;
            public XYZ Far;
            public XYZ AxisFromFar;
            public bool P0IsFar;
            public bool Anchored;
        }

        private FarEndSnap SnapshotFar(MEPCurve curve, XYZ apex)
        {
            var snap = new FarEndSnap { Curve = curve, Far = apex, AxisFromFar = XYZ.BasisX, P0IsFar = true, Anchored = false };
            if (!TryGetLine(curve, out _, out _, out XYZ p0, out XYZ p1)) return snap;
            snap.Far = PickFarEnd(p0, p1, apex, null);
            snap.P0IsFar = P0IsFar(p0, snap.Far);
            snap.AxisFromFar = SafeNormalize(snap.P0IsFar ? (p1 - p0) : (p0 - p1));
            snap.Anchored = IsFarEndAnchored(curve, apex);
            return snap;
        }

        /// <summary>
        /// Put the run back on its snapshot far point. Anchored (or forceAxis): near
        /// slides on the sacred axis only. Free: far stays, near may go to the target.
        /// After a fitting is placed, callers pass forceAxis so pipes never swing
        /// through the body.
        /// </summary>
        private void SacredRestoreRun(FarEndSnap snap, XYZ socket, bool forceAxis = false)
        {
            if (snap.Curve == null || socket == null || !IsLive(snap.Curve)) return;
            if (!TryGetLine(snap.Curve, out LocationCurve lc, out _, out _, out _)) return;

            XYZ near = (forceAxis || snap.Anchored)
                ? ProjectNearOnSacredAxis(snap.Far, snap.AxisFromFar, socket, MinRunLenFt)
                : socket;
            if (snap.Far.DistanceTo(near) < MinRunLenFt) return;
            SetCurvePreserveFar(lc, snap.Far, near, snap.P0IsFar);
        }

        private void SacredRestoreAfterFitting(FamilyInstance fitting, List<FarEndSnap> snaps, XYZ fallback)
        {
            if (snaps == null) return;
            if (!IsLive(fitting)) return;
            _doc.Regenerate();
            XYZ center = fallback;
            try { center = (fitting.Location as LocationPoint)?.Point ?? fallback; } catch { center = fallback; }

            foreach (var s in snaps)
            {
                if (s.Curve == null) continue;
                XYZ socket = SocketOnAxis(fitting, s, center, fallback);

                // Do not DisconnectFrom the new tee — that raises "Disconnect from the network?".
                // Always on-axis here: swinging to the socket 3D point pulls pipe through the body.
                SacredRestoreRun(s, socket, forceAxis: true);
                _doc.Regenerate();

                Connector runNear = FindOpenOnRunNear(s.Curve, socket, 2.5);
                Connector fitConn = fitting != null ? NearestFittingConnector(fitting, s.Curve, socket) : null;
                if (fitConn == null && fitting?.MEPModel?.ConnectorManager != null)
                {
                    double best = double.MaxValue;
                    foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors)
                    {
                        double d = c.Origin.DistanceTo(socket);
                        if (d < best) { best = d; fitConn = c; }
                    }
                }
                LogicalConnect(runNear, fitConn);
            }
            _doc.Regenerate();
        }

        private static Connector NearestFittingConnector(FamilyInstance fitting, MEPCurve run, XYZ hint)
        {
            if (fitting?.MEPModel?.ConnectorManager == null) return null;
            Connector best = null;
            double bestD = double.MaxValue;
            foreach (Connector fc in fitting.MEPModel.ConnectorManager.Connectors)
            {
                try
                {
                    if (fc.IsConnected)
                    {
                        foreach (Connector rc in fc.AllRefs)
                        {
                            if (rc.Owner != null && run != null && rc.Owner.Id == run.Id)
                                return fc;
                        }
                    }
                }
                catch { }
                double d = fc.Origin.DistanceTo(hint);
                if (d < bestD) { bestD = d; best = fc; }
            }
            return best;
        }

        private void DisconnectRunFromFitting(MEPCurve run, FamilyInstance fitting)
        {
            if (run?.ConnectorManager == null || fitting?.MEPModel?.ConnectorManager == null) return;
            foreach (Connector fc in fitting.MEPModel.ConnectorManager.Connectors)
            {
                var refs = new List<Connector>();
                try
                {
                    foreach (Connector rc in fc.AllRefs)
                    {
                        if (rc.Owner != null && rc.Owner.Id == run.Id)
                            refs.Add(rc);
                    }
                }
                catch { }
                foreach (var rc in refs)
                {
                    try { fc.DisconnectFrom(rc); } catch { }
                }
            }
        }

        /// <summary>
        /// Native fitting with far-end shield; if Revit would snap, create then
        /// slide near ends back onto the snapshot sacred axes.
        /// </summary>
        private bool TryFittingKeepFar(Func<FamilyInstance> create, List<FarEndSnap> snaps, MEPCurve[] involved, XYZ junction, out FamilyInstance fitting)
        {
            var pts = new List<XYZ>();
            if (snaps != null)
            {
                foreach (var s in snaps)
                    if (s.Far != null) pts.Add(s.Far);
            }

            if (TryNativeFitting(create, pts, involved, out fitting))
                return true;

            fitting = null;
            using (SubTransaction sub = new SubTransaction(_doc))
            {
                sub.Start();
                try
                {
                    fitting = create();
                    _doc.Regenerate();
                    XYZ center = (fitting?.Location as LocationPoint)?.Point ?? junction;
                    if (snaps != null)
                    {
                        foreach (var s in snaps)
                            SacredRestoreRun(s, center, forceAxis: true);
                    }
                    _doc.Regenerate();
                    if (fitting == null || !AnchoredPointsPreserved(pts, involved))
                    {
                        fitting = null;
                        sub.RollBack();
                        return false;
                    }
                    sub.Commit();
                    return true;
                }
                catch
                {
                    fitting = null;
                    try { sub.RollBack(); } catch { }
                    return false;
                }
            }
        }

        private void AttachRunToLeftoverPort(FarEndSnap snap, Connector leftover)
        {
            if (leftover == null || snap.Curve == null) return;
            SacredRestoreRun(snap, leftover.Origin);
            _doc.Regenerate();
            Connector cRun = FindOpenOnRunNear(snap.Curve, leftover.Origin, 2.5);
            LogicalConnect(cRun, leftover);
        }

        private static FarEndSnap SnapById(List<FarEndSnap> snaps, ElementId id)
        {
            if (snaps == null || id == null) return default;
            foreach (var s in snaps)
            {
                if (s.Curve != null && s.Curve.Id == id)
                    return s;
            }
            return default;
        }

        /// <summary>
        /// From the junction along the run toward its far end.
        /// </summary>
        private static XYZ OutwardAlongRun(FarEndSnap snap, XYZ origin)
        {
            if (snap.Far != null && origin != null)
            {
                XYZ v = snap.Far - origin;
                if (v.GetLength() > 1e-9)
                    return v.Normalize();
            }
            if (snap.AxisFromFar != null && snap.AxisFromFar.GetLength() > 1e-9)
                return snap.AxisFromFar.Negate();
            return XYZ.BasisX;
        }

        private static Connector OpenConnectorFacing(FamilyInstance fitting, XYZ toward)
        {
            if (fitting?.MEPModel?.ConnectorManager == null || toward == null)
                return FirstUnconnectedFittingConnector(fitting);
            Connector best = null;
            double bestDot = -2;
            XYZ want = SafeNormalize(toward);
            foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors)
            {
                if (c.IsConnected) continue;
                if (c.ConnectorType == ConnectorType.Logical) continue;
                double d = SafeNormalize(c.CoordinateSystem.BasisZ).DotProduct(want);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = c;
                }
            }
            return best ?? FirstUnconnectedFittingConnector(fitting);
        }

        /// <summary>
        /// Rotate the tee so its through-run matches the most-opposite pair of pipes
        /// and its branch socket faces the remaining pipe. Pipes are disconnected first
        /// so the fitting turns alone.
        /// </summary>
        private void AlignTeeToSnaps(FamilyInstance tee, List<FarEndSnap> snaps, XYZ origin)
        {
            if (!IsLive(tee) || tee.MEPModel?.ConnectorManager == null || snaps == null || origin == null)
                return;

            var live = new List<FarEndSnap>();
            foreach (var s in snaps)
            {
                if (s.Curve != null && IsLive(s.Curve) && s.Far != null)
                    live.Add(s);
            }
            if (live.Count < 3) return;

            XYZ d0 = OutwardAlongRun(live[0], origin);
            XYZ d1 = OutwardAlongRun(live[1], origin);
            XYZ d2 = OutwardAlongRun(live[2], origin);
            double s01 = d0.DotProduct(d1);
            double s02 = d0.DotProduct(d2);
            double s12 = d1.DotProduct(d2);

            XYZ throughA, branchOut;
            if (s01 <= s02 && s01 <= s12)
            {
                throughA = d0;
                branchOut = d2;
            }
            else if (s02 <= s01 && s02 <= s12)
            {
                throughA = d0;
                branchOut = d1;
            }
            else
            {
                throughA = d1;
                branchOut = d0;
            }

            XYZ pivot = (tee.Location as LocationPoint)?.Point ?? origin;
            DisconnectFittingFromRuns(tee);
            try { _doc.Regenerate(); } catch { }
            if (!IsLive(tee) || tee.MEPModel?.ConnectorManager == null) return;

            var conns = tee.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                .Where(c => c.ConnectorType != ConnectorType.Logical)
                .ToList();
            if (conns.Count < 3) return;
            Connector t0 = conns[0], t1 = conns[1];
            double bestOpp = 2;
            for (int i = 0; i < conns.Count; i++)
            {
                for (int j = i + 1; j < conns.Count; j++)
                {
                    double d = SafeNormalize(conns[i].CoordinateSystem.BasisZ)
                        .DotProduct(SafeNormalize(conns[j].CoordinateSystem.BasisZ));
                    if (d < bestOpp)
                    {
                        bestOpp = d;
                        t0 = conns[i];
                        t1 = conns[j];
                    }
                }
            }

            XYZ curThrough = SafeNormalize(t0.Origin - t1.Origin);
            if (curThrough.DotProduct(throughA) < 0)
                curThrough = curThrough.Negate();
            RotateFitting(tee, pivot, curThrough, throughA);

            try { _doc.Regenerate(); } catch { }
            if (!IsLive(tee) || tee.MEPModel?.ConnectorManager == null) return;

            conns = tee.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
                .Where(c => c.ConnectorType != ConnectorType.Logical)
                .ToList();
            if (conns.Count < 3) return;
            t0 = conns[0]; t1 = conns[1];
            bestOpp = 2;
            for (int i = 0; i < conns.Count; i++)
            {
                for (int j = i + 1; j < conns.Count; j++)
                {
                    double d = SafeNormalize(conns[i].CoordinateSystem.BasisZ)
                        .DotProduct(SafeNormalize(conns[j].CoordinateSystem.BasisZ));
                    if (d < bestOpp)
                    {
                        bestOpp = d;
                        t0 = conns[i];
                        t1 = conns[j];
                    }
                }
            }
            Connector br = conns.FirstOrDefault(c =>
                c.Origin.DistanceTo(t0.Origin) > 1e-9 && c.Origin.DistanceTo(t1.Origin) > 1e-9);
            if (br == null) return;

            XYZ throughAxis = SafeNormalize(t0.Origin - t1.Origin);
            XYZ curBr = SafeNormalize(br.CoordinateSystem.BasisZ);
            XYZ curP = curBr - throughAxis.Multiply(curBr.DotProduct(throughAxis));
            XYZ wantP = branchOut - throughAxis.Multiply(branchOut.DotProduct(throughAxis));
            if (curP.GetLength() < 1e-8 || wantP.GetLength() < 1e-8) return;
            RotateFitting(tee, pivot, curP.Normalize(), wantP.Normalize());
            try { _doc.Regenerate(); } catch { }
        }

        private void RotateFitting(FamilyInstance fitting, XYZ pivot, XYZ fromDir, XYZ toDir)
        {
            if (!IsLive(fitting) || pivot == null || fromDir == null || toDir == null) return;
            XYZ a = SafeNormalize(fromDir);
            XYZ b = SafeNormalize(toDir);
            double ang = a.AngleTo(b);
            if (a.DotProduct(b) > 0.999)
                return;
            XYZ axis = a.CrossProduct(b);
            if (axis.GetLength() < 1e-8)
            {
                axis = a.CrossProduct(XYZ.BasisZ);
                if (axis.GetLength() < 1e-8)
                    axis = a.CrossProduct(XYZ.BasisX);
                if (a.DotProduct(b) > 0)
                    return;
                ang = Math.PI;
            }
            try
            {
                ElementTransformUtils.RotateElement(_doc, fitting.Id, Line.CreateUnbound(pivot, axis.Normalize()), ang);
            }
            catch { }
        }

        private static XYZ OrthoInPlane(XYZ hostDir, XYZ otherDir)
        {
            XYZ n = hostDir.CrossProduct(otherDir);
            if (n.GetLength() < 1e-8)
            {
                n = hostDir.CrossProduct(XYZ.BasisZ);
                if (n.GetLength() < 1e-8) n = hostDir.CrossProduct(XYZ.BasisX);
            }
            n = n.Normalize();
            XYZ dummyDir = n.CrossProduct(hostDir);
            if (dummyDir.GetLength() < 1e-8) return XYZ.BasisX;
            dummyDir = dummyDir.Normalize();
            if (dummyDir.DotProduct(otherDir) < 0) dummyDir = dummyDir.Negate();
            return dummyDir;
        }

        private void TriggerAutoTrimSafe(FamilyInstance fitting, bool anyAnchored)
        {
            if (fitting == null || anyAnchored) return;
            TriggerAutoTrim(fitting, _doc);
        }

        private static XYZ SafeNormalize(XYZ v)
        {
            if (v == null || v.GetLength() < 1e-9) return XYZ.BasisX;
            return v.Normalize();
        }

        /// <summary>
        /// Place an elbow between an existing open connector and a newly created stub.
        /// Must be called inside an already-open Transaction.
        /// Protects the existing run's far end with the geometry shield and dummy-pipe fallback.
        /// </summary>
        public FamilyInstance FinishOpenEndElbow(MEPCurve existing, Connector existingOpen, MEPCurve stub)
        {
            if (existing == null || stub == null) return null;

            ElementId existId = existing.Id;
            ElementId stubId = stub.Id;

            XYZ origin = null;
            try { if (existingOpen != null) origin = existingOpen.Origin; } catch { }

            if (!TryGetLine(existing, out _, out _, out XYZ p0, out XYZ p1))
                return null;

            if (origin == null)
            {
                if (TryGetLine(stub, out _, out _, out XYZ ts0, out XYZ ts1))
                {
                    double d0 = Math.Min(p0.DistanceTo(ts0), p0.DistanceTo(ts1));
                    double d1 = Math.Min(p1.DistanceTo(ts0), p1.DistanceTo(ts1));
                    origin = d0 <= d1 ? p0 : p1;
                }
                else
                    origin = p0;
            }

            XYZ farExisting = PickFarEnd(p0, p1, origin, null);
            bool existingAnc = false;
            try
            {
                existingAnc = IsPhysicallyConnected(FindClosestConnector(existing.ConnectorManager, farExisting));
            }
            catch { }
            List<XYZ> anchoredPts = existingAnc ? new List<XYZ> { farExisting } : new List<XYZ>();

            FamilyInstance elbow;
            bool ok = TryNativeFitting(
                () =>
                {
                    Connector liveExist = LiveOpenNear(existId, origin, 1.5);
                    Connector liveStub = LiveOpenNear(stubId, origin, 1.5);
                    if (liveExist == null || liveStub == null)
                        throw new InvalidOperationException("live connectors missing");
                    return _doc.Create.NewElbowFitting(liveExist, liveStub);
                },
                anchoredPts,
                new MEPCurve[] { LiveCurve(existId) ?? existing },
                out elbow);

            if (ok && elbow != null)
            {
                TrimRunsToFittingSockets(elbow, LiveCurve(existId), LiveCurve(stubId));
                TriggerAutoTrimSafe(elbow, existingAnc);
                return elbow;
            }

            existing = LiveCurve(existId);
            stub = LiveCurve(stubId);
            if (existing == null || stub == null) return null;
            if (!TryGetLine(existing, out _, out _, out p0, out p1))
                return null;
            farExisting = PickFarEnd(p0, p1, origin, null);
            if (!TryGetLine(stub, out _, out _, out XYZ s0, out XYZ s1))
                return null;
            XYZ stubFar = PickFarEnd(s0, s1, origin, null);
            XYZ runDir = SafeNormalize(origin - farExisting);
            XYZ stubDir = SafeNormalize(stubFar - origin);
            XYZ dummyDir = OrthoInPlane(runDir, stubDir);

            MEPCurve dummy = CreateDummyCurve(stub, origin, origin + dummyDir.Multiply(Math.Max(2.0, stubFar.DistanceTo(origin))));
            if (dummy == null)
            {
                LogicalConnect(LiveOpenNear(existId, origin, 1.5), LiveOpenNear(stubId, origin, 1.5));
                return null;
            }

            ElementId dummyId = dummy.Id;
            ok = TryNativeFitting(
                () =>
                {
                    Connector liveExist = LiveOpenNear(existId, origin, 1.5);
                    Connector cDummy = LiveOpenNear(dummyId, origin, 1.5);
                    if (liveExist == null || cDummy == null)
                        throw new InvalidOperationException("live connectors missing");
                    return _doc.Create.NewElbowFitting(liveExist, cDummy);
                },
                anchoredPts,
                new MEPCurve[] { LiveCurve(existId) ?? existing },
                out elbow);

            DeleteDummy(_doc, LiveCurve(dummyId));

            if (ok && elbow != null)
            {
                Connector leftover = FirstUnconnectedFittingConnector(elbow);
                XYZ leftoverPt = origin;
                try { if (leftover != null) leftoverPt = leftover.Origin; } catch { leftover = null; }
                LogicalConnect(LiveOpenNear(stubId, leftoverPt, 1.5), leftover);
                TrimRunsToFittingSockets(elbow, LiveCurve(existId), LiveCurve(stubId));
                TriggerAutoTrimSafe(elbow, existingAnc);
                return elbow;
            }

            LogicalConnect(LiveOpenNear(existId, origin, 1.5), LiveOpenNear(stubId, origin, 1.5));
            return null;
        }

        private FamilyInstance PlaceElbowWithDummy(
            MEPCurve curve1, Connector c1, XYZ meet1, XYZ far1, bool p0IsFar1, bool anc1,
            MEPCurve curve2, Connector c2, XYZ meet2, XYZ far2, bool p0IsFar2, bool anc2,
            List<XYZ> anchoredPoints)
        {
            bool hostIs1 = anc1 || !anc2;
            MEPCurve host = hostIs1 ? curve1 : curve2;
            MEPCurve other = hostIs1 ? curve2 : curve1;
            Connector cHost = hostIs1 ? c1 : c2;
            Connector cOther = hostIs1 ? c2 : c1;
            XYZ apex = hostIs1 ? meet1 : meet2;
            XYZ farOther = hostIs1 ? far2 : far1;
            bool p0IsFarOther = hostIs1 ? p0IsFar2 : p0IsFar1;
            bool otherAnc = hostIs1 ? anc2 : anc1;

            if (cHost == null || cOther == null) return null;

            XYZ hostDir = SafeNormalize(-cHost.CoordinateSystem.BasisZ);
            XYZ otherDir = SafeNormalize(-cOther.CoordinateSystem.BasisZ);
            XYZ dummyDir = OrthoInPlane(hostDir, otherDir);

            MEPCurve dummy = CreateDummyCurve(other, apex, apex + dummyDir.Multiply(2.0));
            if (dummy == null) return null;

            Connector cDummy = FindOpenConnectorNear(dummy.ConnectorManager, apex, 1.0)
                ?? FindClosestConnector(dummy.ConnectorManager, apex);
            if (cDummy == null)
            {
                DeleteDummy(_doc, dummy);
                return null;
            }

            FamilyInstance elbow;
            bool ok = TryNativeFitting(
                () => _doc.Create.NewElbowFitting(cHost, cDummy),
                anchoredPoints,
                new MEPCurve[] { curve1, curve2 },
                out elbow);

            DeleteDummy(_doc, dummy);
            if (!ok || elbow == null) return null;

            Connector leftover = FirstUnconnectedFittingConnector(elbow);
            if (leftover != null)
            {
                if (otherAnc)
                {
                    SacredTrimToSocket(other, farOther, p0IsFarOther, leftover.Origin);
                    _doc.Regenerate();
                    cOther = FindOpenConnectorNear(other.ConnectorManager, leftover.Origin, 1.0)
                        ?? FindClosestConnector(other.ConnectorManager, leftover.Origin);
                }
                LogicalConnect(cOther, leftover);
            }

            return elbow;
        }

        private FamilyInstance PlaceTeeWithDummy(
            Connector cMain1, Connector cMain2, MEPCurve branch, Connector cBranch,
            XYZ onMain, XYZ farBranch, bool p0IsFarBranch, bool branchAnchored,
            List<XYZ> anchoredPoints, MEPCurve mainCurve, MEPCurve mainPart2)
        {
            if (cMain1 == null || cMain2 == null || cBranch == null || branch == null) return null;

            XYZ mainDir = SafeNormalize(-cMain1.CoordinateSystem.BasisZ);
            XYZ branchDir = SafeNormalize(-cBranch.CoordinateSystem.BasisZ);
            XYZ dummyDir = OrthoInPlane(mainDir, branchDir);

            MEPCurve dummy = CreateDummyCurve(branch, onMain, onMain + dummyDir.Multiply(2.0));
            if (dummy == null) return null;

            Connector cDummy = FindOpenConnectorNear(dummy.ConnectorManager, onMain, 1.0)
                ?? FindClosestConnector(dummy.ConnectorManager, onMain);
            if (cDummy == null)
            {
                DeleteDummy(_doc, dummy);
                return null;
            }

            FamilyInstance tee;
            bool ok = TryNativeFitting(
                () => _doc.Create.NewTeeFitting(cMain1, cMain2, cDummy),
                anchoredPoints,
                new MEPCurve[] { mainCurve, mainPart2, branch },
                out tee);

            DeleteDummy(_doc, dummy);
            if (!ok || tee == null) return null;

            Connector leftover = FirstUnconnectedFittingConnector(tee);
            if (leftover != null)
            {
                if (branchAnchored)
                {
                    SacredTrimToSocket(branch, farBranch, p0IsFarBranch, leftover.Origin);
                    _doc.Regenerate();
                    cBranch = FindOpenConnectorNear(branch.ConnectorManager, leftover.Origin, 1.0)
                        ?? FindClosestConnector(branch.ConnectorManager, leftover.Origin);
                }
                LogicalConnect(cBranch, leftover);
            }

            return tee;
        }

        private FamilyInstance PlaceCrossWithDummy(
            Connector main1, Connector main2, Connector branch1,
            MEPCurve newCurve, Connector cNew, XYZ origin, XYZ farPt, bool p0IsFar, bool isAnchored,
            XYZ dummyDir, List<XYZ> anchoredPoints, params MEPCurve[] involved)
        {
            MEPCurve dummy = CreateDummyCurve(newCurve, origin, origin + dummyDir.Multiply(2.0));
            if (dummy == null) return null;

            Connector cDummy = FindOpenConnectorNear(dummy.ConnectorManager, origin, 1.0)
                ?? FindClosestConnector(dummy.ConnectorManager, origin);
            if (cDummy == null)
            {
                DeleteDummy(_doc, dummy);
                return null;
            }

            FamilyInstance cross;
            bool ok = TryNativeFitting(
                () => _doc.Create.NewCrossFitting(main1, main2, branch1, cDummy),
                anchoredPoints,
                involved,
                out cross);

            DeleteDummy(_doc, dummy);
            if (!ok || cross == null) return null;

            Connector leftover = FirstUnconnectedFittingConnector(cross);
            if (leftover != null)
            {
                if (isAnchored)
                {
                    SacredTrimToSocket(newCurve, farPt, p0IsFar, leftover.Origin);
                    _doc.Regenerate();
                    cNew = FindOpenConnectorNear(newCurve.ConnectorManager, leftover.Origin, 1.0)
                        ?? FindClosestConnector(newCurve.ConnectorManager, leftover.Origin);
                }
                LogicalConnect(cNew, leftover);
            }

            return cross;
        }
    }
}
