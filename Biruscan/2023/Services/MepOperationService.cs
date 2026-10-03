using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Biruscan.Services
{
    public class MepOperationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public FamilyInstance CreatedFitting { get; set; }

        public static MepOperationResult OK(string msg, FamilyInstance fitting = null) =>
            new MepOperationResult { Success = true, Message = msg, CreatedFitting = fitting };

        public static MepOperationResult Fail(string msg) =>
            new MepOperationResult { Success = false, Message = msg };
    }

    /// <summary>
    /// Service for connecting MEP curves (Pipe, Duct, Conduit, CableTray)
    /// with intelligent Auto-Elbow and Auto-Tee/Tap fittings, including
    /// automatic common-plane adjustment and trimming.
    /// </summary>
    public partial class MepOperationService
    {
        private readonly Document _doc;

        public MepOperationService(Document doc)
        {
            _doc = doc;
        }

        #region Auto Elbow (Trim & Connect 2 MEP Curves)

        /// <summary>
        /// Connects two MEP curves with an Elbow fitting.
        /// Intelligently aligns them to a common plane/elevation and trims them to their corner apex.
        /// </summary>
        public MepOperationResult ConnectElbow(MEPCurve curve1, MEPCurve curve2, XYZ click1 = null, XYZ click2 = null)
        {
            if (curve1 == null || curve2 == null)
                return MepOperationResult.Fail("Please select two valid MEP elements (Pipes, Ducts, Conduits, or Cable Trays).");

            if (curve1.Id == curve2.Id)
                return MepOperationResult.Fail("Please select two distinct MEP elements.");

            if (!TryGetLine(curve1, out LocationCurve lc1, out _, out XYZ p10, out XYZ p11) ||
                !TryGetLine(curve2, out LocationCurve lc2, out _, out XYZ p20, out XYZ p21))
            {
                return MepOperationResult.Fail("Selected elements must have linear geometries.");
            }

            XYZ dir1 = SafeNormalize(p11 - p10);
            XYZ dir2 = SafeNormalize(p21 - p20);

            if (Math.Abs(dir1.DotProduct(dir2)) > 0.995)
                return MepOperationResult.Fail("Selected elements are parallel. An elbow requires intersecting or angled runs.");

            using (Transaction t = new Transaction(_doc, "Connect MEP Elbow"))
            {
                AllowNetworkDisconnects(t);
                t.Start();
                try
                {
                    EnsureElbowRoutingPreference(curve1);
                    EnsureElbowRoutingPreference(curve2);

                    if (!LineClosestApproach(p10, dir1, p20, dir2, out _, out _, out _, out XYZ on1, out XYZ on2))
                    {
                        t.RollBack();
                        return MepOperationResult.Fail("Could not compute intersection geometry between selected runs.");
                    }

                    bool anc1 = IsFarEndAnchored(curve1, on1);
                    bool anc2 = IsFarEndAnchored(curve2, on2);

                    XYZ meet1, meet2;
                    if (anc1 && anc2) { meet1 = on1; meet2 = on2; }
                    else if (anc1) { meet1 = on1; meet2 = on1; }
                    else if (anc2) { meet1 = on2; meet2 = on2; }
                    else { meet1 = meet2 = (on1 + on2) * 0.5; }

                    XYZ far1 = PickFarEnd(p10, p11, meet1, click1);
                    XYZ far2 = PickFarEnd(p20, p21, meet2, click2);
                    bool p0Far1 = P0IsFar(p10, far1);
                    bool p0Far2 = P0IsFar(p20, far2);

                    if (anc1)
                    {
                        XYZ axis1 = SafeNormalize(p0Far1 ? (p11 - p10) : (p10 - p11));
                        meet1 = ProjectNearOnSacredAxis(far1, axis1, meet1, MinRunLenFt);
                    }
                    if (anc2)
                    {
                        XYZ axis2 = SafeNormalize(p0Far2 ? (p21 - p20) : (p20 - p21));
                        meet2 = ProjectNearOnSacredAxis(far2, axis2, meet2, MinRunLenFt);
                    }

                    if (far1.DistanceTo(meet1) < MinRunLenFt || far2.DistanceTo(meet2) < MinRunLenFt)
                    {
                        t.RollBack();
                        return MepOperationResult.Fail("A selected run is too short to place an elbow.");
                    }

                    SetCurvePreserveFar(lc1, far1, meet1, p0Far1);
                    SetCurvePreserveFar(lc2, far2, meet2, p0Far2);
                    _doc.Regenerate();

                    List<XYZ> anchoredPts = CollectAnchoredEndPoints(curve1, curve2);

                    Connector c1 = FindOpenConnectorNear(curve1.ConnectorManager, meet1, 0.5)
                        ?? FindOpenOnRunNear(curve1, meet1, 1.0);
                    Connector c2 = FindOpenConnectorNear(curve2.ConnectorManager, meet2, 0.5)
                        ?? FindOpenOnRunNear(curve2, meet2, 1.0);
                    if (c1 == null || c2 == null)
                    {
                        t.RollBack();
                        return MepOperationResult.Fail("Could not locate open connectors at the corner intersection.");
                    }

                    XYZ v1 = SafeNormalize(-c1.CoordinateSystem.BasisZ);
                    XYZ v2 = SafeNormalize(-c2.CoordinateSystem.BasisZ);
                    double deg = v1.AngleTo(v2) * 180.0 / Math.PI;
                    bool acute = deg < 80.0;

                    FamilyInstance elbow = null;
                    bool nativeOk = false;
                    if (!acute)
                    {
                        nativeOk = TryNativeFitting(
                            () => _doc.Create.NewElbowFitting(c1, c2),
                            anchoredPts,
                            new MEPCurve[] { curve1, curve2 },
                            out elbow);
                    }

                    if (!nativeOk)
                    {
                        c1 = FindOpenConnectorNear(curve1.ConnectorManager, meet1, 0.5) ?? c1;
                        c2 = FindOpenConnectorNear(curve2.ConnectorManager, meet2, 0.5) ?? c2;
                        elbow = PlaceElbowWithDummy(
                            curve1, c1, meet1, far1, p0Far1, anc1,
                            curve2, c2, meet2, far2, p0Far2, anc2,
                            anchoredPts);
                        if (elbow == null)
                            LogicalConnect(c1, c2);
                    }

                    if (elbow != null)
                    {
                        TrimRunsToFittingSockets(elbow, curve1, curve2);
                        TriggerAutoTrimSafe(elbow, anc1 || anc2);
                        EnsureSystemInterconnection(curve1, curve2, elbow);
                    }

                    t.Commit();

                    if (elbow != null)
                    {
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, elbow.Id, "Piping", "Auto Elbow"); } catch { }
                    }

                    XYZ apex = meet1;
                    return MepOperationResult.OK(
                        $"Connected {curve1.GetType().Name} and {curve2.GetType().Name} with Elbow at ({apex.X:F1}, {apex.Y:F1}, {apex.Z:F1})",
                        elbow);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    return MepOperationResult.Fail("Elbow operation error: " + ex.Message);
                }
            }
        }

        #endregion

        #region Auto Tee / Tap (Connect Branch to Main Run)

        /// <summary>
        /// Connects a branch MEP curve to a main MEP curve using a Tee or Tap fitting.
        /// Birooni two-pipe method: closest-approach junction, split/takeoff the main,
        /// native NewTeeFitting then dummy. Does not use the Elbow/Tee-pipe restore path.
        /// </summary>
        public MepOperationResult ConnectTee(MEPCurve mainCurve, MEPCurve branchCurve)
        {
            if (mainCurve == null || branchCurve == null)
                return MepOperationResult.Fail("Please select both a Main run and a Branch run.");

            if (mainCurve.Id == branchCurve.Id)
                return MepOperationResult.Fail("Main and Branch must be distinct elements.");

            if (!TryGetLine(mainCurve, out LocationCurve lcm, out _, out XYZ pm0, out XYZ pm1) ||
                !TryGetLine(branchCurve, out LocationCurve lcb, out _, out XYZ pb0, out XYZ pb1))
            {
                return MepOperationResult.Fail("Selected elements must have linear geometries.");
            }

            XYZ dirMain = SafeNormalize(pm1 - pm0);
            XYZ dirBranch = SafeNormalize(pb1 - pb0);

            if (Math.Abs(dirMain.DotProduct(dirBranch)) > 0.995)
                return MepOperationResult.Fail("Main and Branch runs are parallel. A Tee requires an intersecting or branching angle.");

            using (Transaction t = new Transaction(_doc, "Connect MEP Tee / Tap"))
            {
                t.Start();
                try
                {
                    if (!LineClosestApproach(pm0, dirMain, pb0, dirBranch, out _, out _, out _, out XYZ onMain, out XYZ onBranch))
                    {
                        t.RollBack();
                        return MepOperationResult.Fail("Could not compute branch intersection with the main run.");
                    }

                    XYZ farPt = PickFarEnd(pb0, pb1, onBranch, null);
                    bool p0FarBranch = P0IsFar(pb0, farPt);
                    bool branchAnc = IsFarEndAnchored(branchCurve, onBranch);
                    bool mainP0Anc = IsPhysicallyConnected(FindClosestConnector(mainCurve.ConnectorManager, pm0));
                    bool mainP1Anc = IsPhysicallyConnected(FindClosestConnector(mainCurve.ConnectorManager, pm1));

                    XYZ branchMeet = branchAnc
                        ? ProjectNearOnSacredAxis(farPt, SafeNormalize(p0FarBranch ? (pb1 - pb0) : (pb0 - pb1)), onBranch, MinRunLenFt)
                        : onMain;

                    if (farPt.DistanceTo(branchMeet) < MinRunLenFt)
                    {
                        t.RollBack();
                        return MepOperationResult.Fail("Branch element is too short.");
                    }

                    SetCurvePreserveFar(lcb, farPt, branchMeet, p0FarBranch);

                    double mainLen = pm0.DistanceTo(pm1);
                    double projT = (onMain - pm0).DotProduct(dirMain);

                    MEPCurve mainPart2 = null;
                    bool usedTakeoff = false;

                    if (projT > 0.15 && projT < mainLen - 0.15)
                    {
                        if (mainCurve is Pipe)
                        {
                            try
                            {
                                ElementId splitId = PlumbingUtils.BreakCurve(_doc, mainCurve.Id, onMain);
                                if (splitId != ElementId.InvalidElementId)
                                    mainPart2 = _doc.GetElement(splitId) as MEPCurve;
                            }
                            catch { }
                        }

                        if (mainPart2 == null)
                        {
                            lcm.Curve = Line.CreateBound(pm0, onMain);
                            mainPart2 = CreateDuplicateCurveSegment(mainCurve, onMain, pm1);
                        }
                    }
                    else if (projT <= 0.15)
                    {
                        if (mainP0Anc)
                            usedTakeoff = true;
                        else
                            lcm.Curve = Line.CreateBound(onMain, pm1);
                    }
                    else
                    {
                        if (mainP1Anc)
                            usedTakeoff = true;
                        else
                            lcm.Curve = Line.CreateBound(pm0, onMain);
                    }

                    _doc.Regenerate();

                    List<XYZ> anchoredPts = CollectAnchoredEndPoints(mainCurve, mainPart2, branchCurve);
                    XYZ projPt = onMain;

                    Connector cBranch = FindOpenConnectorNear(branchCurve.ConnectorManager, branchMeet, 0.5)
                        ?? FindOpenConnectorNear(branchCurve.ConnectorManager, projPt, 0.5);
                    Connector cMain1 = FindOpenConnectorNear(mainCurve.ConnectorManager, projPt, 0.5);
                    Connector cMain2 = mainPart2 != null
                        ? FindOpenConnectorNear(mainPart2.ConnectorManager, projPt, 0.5)
                        : null;

                    FamilyInstance teeFitting = null;
                    var involved = new MEPCurve[] { mainCurve, mainPart2, branchCurve };

                    if (usedTakeoff && cBranch != null)
                    {
                        TryNativeFitting(
                            () => _doc.Create.NewTakeoffFitting(cBranch, mainCurve),
                            anchoredPts, involved, out teeFitting);
                    }
                    else if (cBranch != null && cMain1 != null && cMain2 != null)
                    {
                        bool nativeOk = TryNativeFitting(
                            () => _doc.Create.NewTeeFitting(cMain1, cMain2, cBranch),
                            anchoredPts, involved, out teeFitting);

                        if (!nativeOk)
                        {
                            cBranch = FindOpenConnectorNear(branchCurve.ConnectorManager, branchMeet, 0.5) ?? cBranch;
                            cMain1 = FindOpenConnectorNear(mainCurve.ConnectorManager, projPt, 0.5) ?? cMain1;
                            cMain2 = mainPart2 != null
                                ? (FindOpenConnectorNear(mainPart2.ConnectorManager, projPt, 0.5) ?? cMain2)
                                : cMain2;
                            teeFitting = PlaceTeeWithDummy(
                                cMain1, cMain2, branchCurve, cBranch,
                                projPt, farPt, p0FarBranch, branchAnc,
                                anchoredPts, mainCurve, mainPart2);
                        }
                    }
                    else if (cBranch != null && cMain1 != null)
                    {
                        bool nativeOk = TryNativeFitting(
                            () => _doc.Create.NewElbowFitting(cMain1, cBranch),
                            anchoredPts, involved, out teeFitting);
                        if (!nativeOk)
                            LogicalConnect(cMain1, cBranch);
                    }

                    if (teeFitting != null)
                        TriggerAutoTrimSafe(teeFitting, branchAnc || mainP0Anc || mainP1Anc);

                    t.Commit();

                    if (teeFitting != null)
                    {
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, teeFitting.Id, "Piping", "Auto Tee/Tap"); } catch { }
                    }

                    return MepOperationResult.OK(
                        $"Connected {branchCurve.GetType().Name} to {mainCurve.GetType().Name} with Tee/Tap at ({projPt.X:F1}, {projPt.Y:F1}, {projPt.Z:F1})",
                        teeFitting);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    return MepOperationResult.Fail("Tee operation error: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Places a Tee on three existing MEP runs whose near ends meet.
        /// Through-run is the most-opposite pair; the remaining run is the branch.
        /// Does not split a through-pipe (that is the two-pipe Tee method).
        /// </summary>
        public MepOperationResult ConnectTee(MEPCurve curve1, MEPCurve curve2, MEPCurve curve3)
        {
            if (curve1 == null || curve2 == null || curve3 == null)
                return MepOperationResult.Fail("Please select three pipes (or MEP runs) to place a Tee.");
            if (curve1.Id == curve2.Id || curve1.Id == curve3.Id || curve2.Id == curve3.Id)
                return MepOperationResult.Fail("Please select three distinct MEP elements.");

            if (!TryGetLine(curve1, out _, out _, out XYZ a0, out XYZ a1) ||
                !TryGetLine(curve2, out _, out _, out XYZ b0, out XYZ b1) ||
                !TryGetLine(curve3, out _, out _, out XYZ c0, out XYZ c1))
            {
                return MepOperationResult.Fail("Selected elements must have linear geometries.");
            }

            if (!TryThreePipeJunction(a0, a1, b0, b1, c0, c1, out XYZ origin))
                return MepOperationResult.Fail("Could not find a common Tee junction for the three runs.");

            // Junction must be at an end of each run, not the interior of a long main.
            if (IsInteriorPoint(a0, a1, origin) || IsInteriorPoint(b0, b1, origin) || IsInteriorPoint(c0, c1, origin))
            {
                return MepOperationResult.Fail(
                    "One run passes through the junction. Use two-pipe Tee (main + branch) so the main can be split.");
            }

            XYZ d1 = OutwardFromOrigin(a0, a1, origin);
            XYZ d2 = OutwardFromOrigin(b0, b1, origin);
            XYZ d3 = OutwardFromOrigin(c0, c1, origin);
            double s12 = d1.DotProduct(d2);
            double s13 = d1.DotProduct(d3);
            double s23 = d2.DotProduct(d3);

            MEPCurve throughA, throughB, branch;
            if (s12 <= s13 && s12 <= s23)
            {
                throughA = curve1; throughB = curve2; branch = curve3;
            }
            else if (s13 <= s12 && s13 <= s23)
            {
                throughA = curve1; throughB = curve3; branch = curve2;
            }
            else
            {
                throughA = curve2; throughB = curve3; branch = curve1;
            }

            using (Transaction t = new Transaction(_doc, "Connect MEP Tee (3 pipes)"))
            {
                t.Start();
                try
                {
                    if (!TrimRunToPoint(curve1, origin, out _, out _, out bool anc1) ||
                        !TrimRunToPoint(curve2, origin, out _, out _, out bool anc2) ||
                        !TrimRunToPoint(curve3, origin, out XYZ farBranch, out bool p0FarBranch, out bool anc3))
                    {
                        t.RollBack();
                        return MepOperationResult.Fail("A selected run is too short to connect with a Tee.");
                    }

                    _doc.Regenerate();

                    List<XYZ> anchoredPts = CollectAnchoredEndPoints(curve1, curve2, curve3);
                    var involved = new MEPCurve[] { curve1, curve2, curve3 };

                    Connector cA = FindOpenConnectorNear(throughA.ConnectorManager, origin, 0.5);
                    Connector cB = FindOpenConnectorNear(throughB.ConnectorManager, origin, 0.5);
                    Connector cBr = FindOpenConnectorNear(branch.ConnectorManager, origin, 0.5);

                    FamilyInstance teeFitting = null;

                    if (cA != null && cB != null && cBr != null)
                    {
                        bool nativeOk = TryNativeFitting(
                            () => _doc.Create.NewTeeFitting(cA, cB, cBr),
                            anchoredPts, involved, out teeFitting);

                        if (!nativeOk)
                        {
                            cA = FindOpenConnectorNear(throughA.ConnectorManager, origin, 0.5) ?? cA;
                            cB = FindOpenConnectorNear(throughB.ConnectorManager, origin, 0.5) ?? cB;
                            cBr = FindOpenConnectorNear(branch.ConnectorManager, origin, 0.5) ?? cBr;
                            teeFitting = PlaceThreePipeTeeWithDummy(
                                throughA, cA, throughB, cB, branch, cBr,
                                origin, farBranch, p0FarBranch, anc3,
                                anchoredPts);
                        }
                    }

                    if (!IsLive(teeFitting))
                    {
                        t.RollBack();
                        return MepOperationResult.Fail("Could not place a Tee on the three selected runs.");
                    }

                    TriggerAutoTrimSafe(teeFitting, anc1 || anc2 || anc3);

                    t.Commit();
                    try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, teeFitting.Id, "Piping", "Auto Tee (3 pipes)"); } catch { }
                    return MepOperationResult.OK(
                        $"Connected three runs with Tee at ({origin.X:F1}, {origin.Y:F1}, {origin.Z:F1})",
                        teeFitting);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    return MepOperationResult.Fail("Tee operation error: " + ex.Message);
                }
            }
        }

        private static bool TryThreePipeJunction(
            XYZ a0, XYZ a1, XYZ b0, XYZ b1, XYZ c0, XYZ c1, out XYZ origin)
        {
            origin = XYZ.Zero;
            XYZ da = SafeNormalize(a1 - a0);
            XYZ db = SafeNormalize(b1 - b0);
            XYZ dc = SafeNormalize(c1 - c0);
            XYZ sum = XYZ.Zero;
            int n = 0;
            if (LineClosestApproach(a0, da, b0, db, out _, out _, out _, out XYZ pab, out XYZ pba))
            { sum += (pab + pba) * 0.5; n++; }
            if (LineClosestApproach(a0, da, c0, dc, out _, out _, out _, out XYZ pac, out XYZ pca))
            { sum += (pac + pca) * 0.5; n++; }
            if (LineClosestApproach(b0, db, c0, dc, out _, out _, out _, out XYZ pbc, out XYZ pcb))
            { sum += (pbc + pcb) * 0.5; n++; }
            if (n == 0) return false;
            origin = sum / n;
            return true;
        }

        private static bool IsInteriorPoint(XYZ p0, XYZ p1, XYZ pt)
        {
            XYZ dir = p1 - p0;
            double len = dir.GetLength();
            if (len < 1e-9) return false;
            dir = dir.Normalize();
            double t = (pt - p0).DotProduct(dir);
            return t > 0.15 && t < len - 0.15;
        }

        private static XYZ OutwardFromOrigin(XYZ p0, XYZ p1, XYZ origin)
        {
            XYZ far = p0.DistanceTo(origin) >= p1.DistanceTo(origin) ? p0 : p1;
            XYZ v = far - origin;
            if (v.GetLength() < 1e-9) return SafeNormalize(p1 - p0);
            return v.Normalize();
        }

        private bool TrimRunToPoint(MEPCurve curve, XYZ target, out XYZ farPt, out bool p0Far, out bool anchored)
        {
            farPt = target;
            p0Far = true;
            anchored = false;
            if (curve == null || target == null) return false;
            if (!TryGetLine(curve, out LocationCurve lc, out _, out XYZ p0, out XYZ p1)) return false;

            farPt = PickFarEnd(p0, p1, target, null);
            p0Far = P0IsFar(p0, farPt);
            anchored = IsFarEndAnchored(curve, target);
            XYZ axis = SafeNormalize(p0Far ? (p1 - p0) : (p0 - p1));
            XYZ near = anchored
                ? ProjectNearOnSacredAxis(farPt, axis, target, MinRunLenFt)
                : target;
            if (farPt.DistanceTo(near) < MinRunLenFt) return false;
            lc.Curve = p0Far ? Line.CreateBound(farPt, near) : Line.CreateBound(near, farPt);
            return true;
        }

        private FamilyInstance PlaceThreePipeTeeWithDummy(
            MEPCurve throughA, Connector cA, MEPCurve throughB, Connector cB,
            MEPCurve branch, Connector cBr,
            XYZ origin, XYZ farBranch, bool p0FarBranch, bool branchAnchored,
            List<XYZ> anchoredPts)
        {
            if (cA == null || cBr == null || throughA == null) return null;

            XYZ dummyDir;
            XYZ dummyStart;
            try
            {
                dummyDir = SafeNormalize(cA.CoordinateSystem.BasisZ);
                dummyStart = cA.Origin;
            }
            catch { return null; }

            MEPCurve dummy = CreateDummyCurve(throughA, dummyStart, dummyStart + dummyDir.Multiply(2.0));
            if (dummy == null) return null;
            OrientDummyAsHostContinuation(throughA, dummyStart, dummy);
            ElementId dummyId = dummy.Id;
            var protect = new HashSet<ElementId> { throughA.Id, throughB != null ? throughB.Id : ElementId.InvalidElementId, branch.Id };

            FamilyInstance tee = null;
            bool ok = TryNativeFitting(
                () =>
                {
                    Connector h = FindOpenConnectorNear(throughA.ConnectorManager, origin, 0.5) ?? cA;
                    Connector d = FindOpenConnectorNear(dummy.ConnectorManager, dummyStart, 2.5)
                        ?? FindOpenConnectorNear(dummy.ConnectorManager, origin, 2.5);
                    Connector b = FindOpenConnectorNear(branch.ConnectorManager, origin, 0.5) ?? cBr;
                    if (h == null || d == null || b == null)
                        throw new InvalidOperationException("live connectors missing");
                    return _doc.Create.NewTeeFitting(h, d, b);
                },
                anchoredPts,
                new MEPCurve[] { throughA, throughB, branch },
                out tee);

            DeleteDummy(_doc, LiveCurve(dummyId), protect);
            tee = IsLive(tee) ? tee : null;
            if (!ok || tee == null) return null;

            Connector leftover = FirstUnconnectedFittingConnector(tee);
            if (leftover != null && throughB != null)
            {
                Connector cKeep = FindOpenConnectorNear(throughB.ConnectorManager, origin, 0.5) ?? cB
                    ?? FindClosestConnector(throughB.ConnectorManager, origin);
                LogicalConnect(cKeep, leftover);
            }
            else if (leftover != null && cBr != null)
            {
                if (branchAnchored)
                {
                    SacredTrimToSocket(branch, farBranch, p0FarBranch, leftover.Origin);
                    _doc.Regenerate();
                    cBr = FindOpenConnectorNear(branch.ConnectorManager, leftover.Origin, 1.0)
                        ?? FindClosestConnector(branch.ConnectorManager, leftover.Origin);
                }
                LogicalConnect(cBr, leftover);
            }

            return tee;
        }

        #endregion

        #region Auto Union (Unify 2 MEP Curves / Pipes with Union or Coupling)

        /// <summary>
        /// Connects two MEP curves (Pipes, Ducts, Conduits, Cable Trays) with a Union or Coupling fitting.
        /// Maintains maximum possible 3D angle and slope, aligns the meeting endpoints, and establishes full MEP system connection (Tab).
        /// </summary>
        public MepOperationResult ConnectUnion(MEPCurve curve1, MEPCurve curve2)
        {
            if (curve1 == null || curve2 == null)
                return MepOperationResult.Fail("Please select two valid MEP elements (Pipes, Ducts, Conduits, or Cable Trays).");
            if (curve1.Id == curve2.Id)
                return MepOperationResult.Fail("Please select two distinct MEP elements.");
            if (!TryGetLine(curve1, out LocationCurve lc1, out _, out XYZ p10, out XYZ p11) ||
                !TryGetLine(curve2, out LocationCurve lc2, out _, out XYZ p20, out XYZ p21))
                return MepOperationResult.Fail("Selected elements must have linear geometries.");

            using (Transaction t = new Transaction(_doc, "Connect MEP Union"))
            {
                t.Start();
                try
                {
                    IdentifyNearFar(p10, p11, p20, p21, out XYZ near1, out XYZ far1, out XYZ near2, out XYZ far2);
                    bool p0Far1 = P0IsFar(p10, far1);
                    bool p0Far2 = P0IsFar(p20, far2);
                    bool n1IsP10 = near1.DistanceTo(p10) < 0.001;
                    bool n2IsP20 = near2.DistanceTo(p20) < 0.001;

                    bool anc1 = IsFarEndAnchored(curve1, near1);
                    bool anc2 = IsFarEndAnchored(curve2, near2);

                    // Hack 3: Z-shift only the free run. Both anchored → no shift.
                    if (Math.Abs(near1.Z - near2.Z) > 0.005)
                    {
                        if (anc1 && !anc2)
                            RigidShiftCurveZ(lc2, near1.Z - near2.Z);
                        else if (anc2 && !anc1)
                            RigidShiftCurveZ(lc1, near2.Z - near1.Z);
                        else if (!anc1 && !anc2)
                            RigidShiftCurveZ(lc2, near1.Z - near2.Z);
                        _doc.Regenerate();

                        if (!TryGetLine(curve1, out lc1, out _, out p10, out p11) ||
                            !TryGetLine(curve2, out lc2, out _, out p20, out p21))
                        {
                            t.RollBack();
                            return MepOperationResult.Fail("Lost run geometry after elevation shift.");
                        }
                        near1 = n1IsP10 ? p10 : p11;
                        far1 = n1IsP10 ? p11 : p10;
                        near2 = n2IsP20 ? p20 : p21;
                        far2 = n2IsP20 ? p21 : p20;
                        p0Far1 = P0IsFar(p10, far1);
                        p0Far2 = P0IsFar(p20, far2);
                    }

                    XYZ dir1 = SafeNormalize(near1 - far1);
                    XYZ dir2 = SafeNormalize(near2 - far2);

                    SyncMepSystem(curve1, curve2);
                    EnsureUnionRoutingPreference(curve1);
                    EnsureUnionRoutingPreference(curve2);

                    // Host keeps its axis. Free pipe may slerp (near only) if the union
                    // cannot connect at the original angle. Both anchored → neither swings.
                    bool hostIs1 = anc1 || !anc2;
                    MEPCurve host = hostIs1 ? curve1 : curve2;
                    MEPCurve free = hostIs1 ? curve2 : curve1;
                    LocationCurve hostLc = hostIs1 ? lc1 : lc2;
                    LocationCurve freeLc = hostIs1 ? lc2 : lc1;
                    XYZ hostFar = hostIs1 ? far1 : far2;
                    XYZ freeFar = hostIs1 ? far2 : far1;
                    XYZ hostDir = hostIs1 ? dir1 : dir2;
                    XYZ freeDir0 = hostIs1 ? dir2 : dir1;
                    bool hostP0Far = hostIs1 ? p0Far1 : p0Far2;
                    bool freeP0Far = hostIs1 ? p0Far2 : p0Far1;

                    XYZ junction;
                    if (LineClosestApproach(hostFar, hostDir, freeFar, freeDir0, out _, out _, out _, out XYZ onHost, out _))
                        junction = onHost;
                    else
                        junction = hostIs1 ? near1 : near2;

                    double hostLen = hostFar.DistanceTo(junction);
                    if (hostLen < MinRunLenFt)
                    {
                        t.RollBack();
                        return MepOperationResult.Fail("Elements are too short after alignment.");
                    }

                    double rHost = 0.25, rFree = 0.25;
                    try { double d = GetCurveDiameter(host); if (d > 0.1) rHost = d / 2.0; } catch { }
                    try { double d = GetCurveDiameter(free); if (d > 0.1) rFree = d / 2.0; } catch { }
                    double pullHost = Math.Max(rHost * 0.5, 0.04);
                    double pullFree = Math.Max(rFree * 0.5, 0.04);

                    XYZ targetDir = SafeNormalize(junction - freeFar);
                    double origKink = freeDir0.AngleTo(targetDir);

                    ApplyUnionAngleGeometry(
                        hostLc, hostFar, hostDir, hostP0Far, hostLen, pullHost,
                        freeLc, freeFar, freeDir0, freeP0Far, pullFree,
                        junction, 0.0);

                    List<XYZ> anchoredPts = CollectAnchoredEndPoints(curve1, curve2);
                    FamilyInstance fitting = null;
                    bool joined = TryUnionNative(host, free, junction, anchoredPts, out fitting);

                    // Original angle first. If the union disconnects, reduce only the free
                    // pipe's near-end direction (far end stays) until it reconnects — keep
                    // the largest remaining kink, never collapse to coaxial.
                    if (!joined && origKink > 1.0 * Math.PI / 180.0 && !(anc1 && anc2))
                    {
                        double minKink = Math.Min(origKink, 2.0 * Math.PI / 180.0);
                        double tMax = Math.Min(0.95, 1.0 - minKink / origKink);
                        double tBest = SearchMaxUnionAngle(
                            host, hostLc, hostFar, hostDir, hostP0Far, hostLen, pullHost,
                            free, freeLc, freeFar, freeDir0, freeP0Far, pullFree,
                            junction, anchoredPts, tMax);

                        ApplyUnionAngleGeometry(
                            hostLc, hostFar, hostDir, hostP0Far, hostLen, pullHost,
                            freeLc, freeFar, freeDir0, freeP0Far, pullFree,
                            junction, tBest);
                        joined = TryUnionNative(host, free, junction, anchoredPts, out fitting);
                    }

                    if (!joined)
                    {
                        Connector cHost = FindOpenConnectorNear(host.ConnectorManager, junction, 1.0)
                            ?? FindClosestConnector(host.ConnectorManager, junction);
                        Connector cFree = FindOpenConnectorNear(free.ConnectorManager, junction, 1.0)
                            ?? FindClosestConnector(free.ConnectorManager, junction);
                        fitting = PlaceLinearFittingManually(host, cHost, free, cFree, junction, unionMode: true);
                    }

                    if (fitting != null)
                    {
                        TrimRunsToFittingSockets(fitting, host, free);
                        EnsurePhysicalUnionConnections(fitting, host, free);
                        TriggerAutoTrimSafe(fitting, anc1 || anc2);
                        EnsureSystemInterconnection(curve1, curve2, fitting);
                    }
                    else
                    {
                        Connector cHost = FindOpenConnectorNear(host.ConnectorManager, junction, 1.0)
                            ?? FindClosestConnector(host.ConnectorManager, junction);
                        Connector cFree = FindOpenConnectorNear(free.ConnectorManager, junction, 1.0)
                            ?? FindClosestConnector(free.ConnectorManager, junction);
                        LogicalConnect(cHost, cFree);
                        EnsureSystemInterconnection(curve1, curve2, null);
                    }

                    t.Commit();

                    if (fitting != null)
                    {
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, fitting.Id, "Piping", "Auto Union"); } catch { }
                    }

                    string fitName = fitting != null ? "Union/Coupling fitting" : "Direct System Connection";
                    return MepOperationResult.OK(
                        $"Unified {curve1.GetType().Name} and {curve2.GetType().Name} with {fitName} and full System Interconnection",
                        fitting);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    return MepOperationResult.Fail(ex.Message);
                }
            }
        }

        /// <summary>
        /// t=0 keeps the free pipe on its original axis (maximum angle).
        /// t→1 slings only its near end toward the host junction. Far end is fixed.
        /// </summary>
        private void ApplyUnionAngleGeometry(
            LocationCurve hostLc, XYZ hostFar, XYZ hostDir, bool hostP0Far, double hostLen, double pullHost,
            LocationCurve freeLc, XYZ freeFar, XYZ freeDir0, bool freeP0Far, double pullFree,
            XYZ junction, double t)
        {
            XYZ hostNear = hostFar + hostDir.Multiply(Math.Max(MinRunLenFt, hostLen - pullHost));
            SetCurvePreserveFar(hostLc, hostFar, hostNear, hostP0Far);

            XYZ targetDir = SafeNormalize(junction - freeFar);
            XYZ freeDir = t <= 1e-6 ? freeDir0 : Slerp(freeDir0, targetDir, t);
            double freeLen = Math.Max(MinRunLenFt + pullFree, freeFar.DistanceTo(junction));
            XYZ freeNear = freeFar + freeDir.Multiply(Math.Max(MinRunLenFt, freeLen - pullFree));
            SetCurvePreserveFar(freeLc, freeFar, freeNear, freeP0Far);
            _doc.Regenerate();
        }

        private bool TryUnionNative(MEPCurve host, MEPCurve free, XYZ junction, List<XYZ> anchoredPts, out FamilyInstance fitting)
        {
            fitting = null;
            Connector cHost = FindOpenConnectorNear(host.ConnectorManager, junction, 1.0)
                ?? FindClosestConnector(host.ConnectorManager, junction);
            Connector cFree = FindOpenConnectorNear(free.ConnectorManager, junction, 1.0)
                ?? FindClosestConnector(free.ConnectorManager, junction);
            if (cHost == null || cFree == null) return false;

            if (!TryNativeFitting(
                () => _doc.Create.NewUnionFitting(cHost, cFree),
                anchoredPts, new MEPCurve[] { host, free }, out fitting))
                return false;

            return UnionFittingJoins(fitting, host, free);
        }

        private bool ProbeUnionAtAngle(
            LocationCurve hostLc, XYZ hostFar, XYZ hostDir, bool hostP0Far, double hostLen, double pullHost,
            LocationCurve freeLc, XYZ freeFar, XYZ freeDir0, bool freeP0Far, double pullFree,
            XYZ junction, MEPCurve host, MEPCurve free, List<XYZ> anchoredPts, double t)
        {
            using (SubTransaction sub = new SubTransaction(_doc))
            {
                sub.Start();
                try
                {
                    ApplyUnionAngleGeometry(
                        hostLc, hostFar, hostDir, hostP0Far, hostLen, pullHost,
                        freeLc, freeFar, freeDir0, freeP0Far, pullFree,
                        junction, t);

                    Connector cHost = FindOpenConnectorNear(host.ConnectorManager, junction, 1.0)
                        ?? FindClosestConnector(host.ConnectorManager, junction);
                    Connector cFree = FindOpenConnectorNear(free.ConnectorManager, junction, 1.0)
                        ?? FindClosestConnector(free.ConnectorManager, junction);
                    if (cHost == null || cFree == null)
                    {
                        sub.RollBack();
                        return false;
                    }

                    FamilyInstance f = null;
                    try
                    {
                        f = _doc.Create.NewUnionFitting(cHost, cFree);
                        _doc.Regenerate();
                    }
                    catch { }

                    bool ok = f != null
                        && UnionFittingJoins(f, host, free)
                        && AnchoredPointsPreserved(anchoredPts, host, free);
                    sub.RollBack();
                    return ok;
                }
                catch
                {
                    try { sub.RollBack(); } catch { }
                    return false;
                }
            }
        }

        private double SearchMaxUnionAngle(
            MEPCurve host, LocationCurve hostLc, XYZ hostFar, XYZ hostDir, bool hostP0Far, double hostLen, double pullHost,
            MEPCurve free, LocationCurve freeLc, XYZ freeFar, XYZ freeDir0, bool freeP0Far, double pullFree,
            XYZ junction, List<XYZ> anchoredPts, double tMax)
        {
            // Smallest t that still physically connects = largest remaining angle.
            double lo = 0.0;
            double hi = tMax;
            double best = tMax;
            for (int i = 0; i < 8; i++)
            {
                double mid = 0.5 * (lo + hi);
                bool ok = ProbeUnionAtAngle(
                    hostLc, hostFar, hostDir, hostP0Far, hostLen, pullHost,
                    freeLc, freeFar, freeDir0, freeP0Far, pullFree,
                    junction, host, free, anchoredPts, mid);
                if (ok)
                {
                    best = mid;
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }
            return best;
        }

        private static bool UnionFittingJoins(FamilyInstance fitting, MEPCurve a, MEPCurve b)
        {
            if (fitting?.MEPModel?.ConnectorManager == null || a == null || b == null) return false;
            bool ja = false, jb = false;
            try
            {
                foreach (Connector fc in fitting.MEPModel.ConnectorManager.Connectors)
                {
                    if (!fc.IsConnected) continue;
                    foreach (Connector r in fc.AllRefs)
                    {
                        if (r.Owner == null) continue;
                        if (r.Owner.Id == a.Id) ja = true;
                        if (r.Owner.Id == b.Id) jb = true;
                    }
                }
            }
            catch { return false; }
            return ja && jb;
        }

        
        public MepOperationResult ConnectUnify(MEPCurve curve1, MEPCurve curve2)
        {
            if (curve1 == null || curve2 == null)
                return MepOperationResult.Fail("Please select two valid MEP elements.");
            if (curve1.Id == curve2.Id)
                return MepOperationResult.Fail("Please select two distinct MEP elements.");
            if (curve1.GetType() != curve2.GetType())
                return MepOperationResult.Fail("Elements must be of the same type.");

            if (!TryGetLine(curve1, out LocationCurve lc1, out _, out XYZ p10, out XYZ p11) ||
                !TryGetLine(curve2, out _, out _, out XYZ p20, out XYZ p21))
                return MepOperationResult.Fail("Selected elements must have linear geometries.");

            IdentifyNearFar(p10, p11, p20, p21, out XYZ near1, out XYZ far1, out XYZ near2, out XYZ far2);
            bool p0Far1 = P0IsFar(p10, far1);

            XYZ dir1 = SafeNormalize(near1 - far1);
            XYZ dir2 = SafeNormalize(near2 - far2);
            bool collinear = Math.Abs(dir1.DotProduct(dir2)) > 0.995;

            bool anc1 = IsFarEndAnchored(curve1, near1);
            bool anc2 = IsFarEndAnchored(curve2, near2);

            if (anc1 && anc2 && !collinear)
                return MepOperationResult.Fail("Both runs are anchored and not collinear. Unify would break a network.");

            using (Transaction t = new Transaction(_doc, "Unify MEP Elements"))
            {
                AllowNetworkDisconnects(t);
                t.Start();
                try
                {
                    MEPCurve keep = curve1;
                    MEPCurve drop = curve2;
                    XYZ keepFar = far1;
                    XYZ dropFar = far2;
                    bool keepP0Far = p0Far1;
                    XYZ keepDir = dir1;
                    LocationCurve keepLc = lc1;

                    if (anc2 && !anc1)
                    {
                        keep = curve2;
                        drop = curve1;
                        keepFar = far2;
                        dropFar = far1;
                        keepP0Far = P0IsFar(p20, far2);
                        keepDir = dir2;
                        if (!TryGetLine(curve2, out keepLc, out _, out _, out _))
                        {
                            t.RollBack();
                            return MepOperationResult.Fail("Could not read keep-run geometry.");
                        }
                    }

                    var dropFarLinks = CapturePhysicalLinks(drop, keep.Id);

                    XYZ newNear;
                    if (anc1 || anc2)
                    {
                        newNear = ProjectNearOnSacredAxis(keepFar, keepDir, dropFar, MinRunLenFt);
                    }
                    else
                    {
                        newNear = dropFar;
                    }

                    DisconnectAllPhysical(drop);
                    _doc.Delete(drop.Id);
                    SetCurvePreserveFar(keepLc, keepFar, newNear, keepP0Far);
                    _doc.Regenerate();

                    Connector newEnd = FindClosestConnector(keep.ConnectorManager, newNear);
                    if (newEnd != null)
                    {
                        foreach (var link in dropFarLinks)
                        {
                            try
                            {
                                Element partner = _doc.GetElement(link.PartnerId);
                                if (!IsLive(partner)) continue;
                                Connector pc = FindConnectorOnElement(partner, link.PartnerPt);
                                if (pc == null) continue;
                                if (!newEnd.IsConnectedTo(pc))
                                    newEnd.ConnectTo(pc);
                            }
                            catch { }
                        }
                    }

                    t.Commit();
                    return MepOperationResult.OK($"Unified two {keep.GetType().Name}s into a single continuous element.", null);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    return MepOperationResult.Fail(ex.Message);
                }
            }
        }

        public MepOperationResult ConnectReducer(MEPCurve curve1, MEPCurve curve2)
        {
            return ConnectLinearFitting(curve1, curve2, unionMode: false);
        }

        /// <summary>
        /// Shared Union/Reducer path. Z-shifts only the free run; trims only near ends
        /// along each run's own axis; native fitting is shielded; auto-trim is suppressed
        /// on anchored runs.
        /// </summary>
        private MepOperationResult ConnectLinearFitting(MEPCurve curve1, MEPCurve curve2, bool unionMode)
        {
            if (curve1 == null || curve2 == null)
                return MepOperationResult.Fail("Please select two valid MEP elements (Pipes, Ducts, Conduits, or Cable Trays).");
            if (curve1.Id == curve2.Id)
                return MepOperationResult.Fail("Please select two distinct MEP elements.");
            if (!TryGetLine(curve1, out LocationCurve lc1, out _, out XYZ p10, out XYZ p11) ||
                !TryGetLine(curve2, out LocationCurve lc2, out _, out XYZ p20, out XYZ p21))
                return MepOperationResult.Fail("Selected elements must have linear geometries.");

            string txnName = unionMode ? "Connect MEP Union" : "Connect MEP Reducer";
            using (Transaction t = new Transaction(_doc, txnName))
            {
                AllowNetworkDisconnects(t);
                t.Start();
                try
                {
                    IdentifyNearFar(p10, p11, p20, p21, out XYZ near1, out XYZ far1, out XYZ near2, out XYZ far2);
                    bool p0Far1 = P0IsFar(p10, far1);
                    bool p0Far2 = P0IsFar(p20, far2);
                    bool n1IsP10 = near1.DistanceTo(p10) < 0.001;
                    bool n2IsP20 = near2.DistanceTo(p20) < 0.001;

                    bool anc1 = IsFarEndAnchored(curve1, near1);
                    bool anc2 = IsFarEndAnchored(curve2, near2);

                    if (Math.Abs(near1.Z - near2.Z) > 0.005)
                    {
                        if (anc1 && !anc2)
                            RigidShiftCurveZ(lc2, near1.Z - near2.Z);
                        else if (anc2 && !anc1)
                            RigidShiftCurveZ(lc1, near2.Z - near1.Z);
                        else if (!anc1 && !anc2)
                            RigidShiftCurveZ(lc2, near1.Z - near2.Z);
                        _doc.Regenerate();

                        if (!TryGetLine(curve1, out lc1, out _, out p10, out p11) ||
                            !TryGetLine(curve2, out lc2, out _, out p20, out p21))
                        {
                            t.RollBack();
                            return MepOperationResult.Fail("Lost run geometry after elevation shift.");
                        }
                        near1 = n1IsP10 ? p10 : p11;
                        far1 = n1IsP10 ? p11 : p10;
                        near2 = n2IsP20 ? p20 : p21;
                        far2 = n2IsP20 ? p21 : p20;
                        p0Far1 = P0IsFar(p10, far1);
                        p0Far2 = P0IsFar(p20, far2);
                    }

                    XYZ dir1 = SafeNormalize(near1 - far1);
                    XYZ dir2 = SafeNormalize(near2 - far2);

                    SyncMepSystem(curve1, curve2);
                    if (unionMode)
                    {
                        EnsureUnionRoutingPreference(curve1);
                        EnsureUnionRoutingPreference(curve2);
                    }
                    else
                    {
                        EnsureTransitionRoutingPreference(curve1);
                        EnsureTransitionRoutingPreference(curve2);
                    }

                    XYZ target1, target2;
                    if (anc1 && anc2)
                    {
                        if (LineClosestApproach(far1, dir1, far2, dir2, out _, out _, out _, out XYZ on1, out XYZ on2))
                        {
                            target1 = on1;
                            target2 = on2;
                        }
                        else
                        {
                            target1 = near1;
                            target2 = near2;
                        }
                    }
                    else if (anc1)
                    {
                        target1 = ProjectNearOnSacredAxis(far1, dir1, near2, MinRunLenFt);
                        target2 = target1;
                    }
                    else if (anc2)
                    {
                        target2 = ProjectNearOnSacredAxis(far2, dir2, near1, MinRunLenFt);
                        target1 = target2;
                    }
                    else if (Math.Abs(dir1.DotProduct(dir2)) > 0.9999)
                    {
                        target1 = target2 = (near1 + near2) * 0.5;
                    }
                    else if (LineClosestApproach(far1, dir1, far2, dir2, out _, out _, out double gap, out XYZ o1, out XYZ o2))
                    {
                        target1 = target2 = (gap < 0.01) ? o1 : (o1 + o2) * 0.5;
                    }
                    else
                    {
                        target1 = target2 = (near1 + near2) * 0.5;
                    }

                    if (far1.DistanceTo(target1) < MinRunLenFt || far2.DistanceTo(target2) < MinRunLenFt)
                    {
                        t.RollBack();
                        return MepOperationResult.Fail("Elements are too short after alignment.");
                    }

                    double r1 = 0.25, r2 = 0.25;
                    try { double d = GetCurveDiameter(curve1); if (d > 0.1) r1 = d / 2.0; } catch { }
                    try { double d = GetCurveDiameter(curve2); if (d > 0.1) r2 = d / 2.0; } catch { }
                    // Union bodies are short; a 1.5R pullback leaves a visible physical gap.
                    // Reducer still needs room for the transition. Exact seating is done
                    // after placement by TrimRunsToFittingSockets.
                    double pullFactor = unionMode ? 0.5 : 1.5;
                    double pull1 = Math.Max(r1 * pullFactor, 0.04);
                    double pull2 = Math.Max(r2 * pullFactor, 0.04);

                    double tEnd1 = Math.Max(MinRunLenFt, (target1 - far1).DotProduct(dir1) - pull1);
                    double tEnd2 = Math.Max(MinRunLenFt, (target2 - far2).DotProduct(dir2) - pull2);
                    XYZ end1 = far1 + dir1.Multiply(tEnd1);
                    XYZ end2 = far2 + dir2.Multiply(tEnd2);

                    SetCurvePreserveFar(lc1, far1, end1, p0Far1);
                    SetCurvePreserveFar(lc2, far2, end2, p0Far2);
                    _doc.Regenerate();

                    XYZ junction = (end1 + end2) * 0.5;
                    Connector c1 = FindOpenConnectorNear(curve1.ConnectorManager, junction, 0.75)
                        ?? FindClosestConnector(curve1.ConnectorManager, junction);
                    Connector c2 = FindOpenConnectorNear(curve2.ConnectorManager, junction, 0.75)
                        ?? FindClosestConnector(curve2.ConnectorManager, junction);
                    if (c1 == null || c2 == null)
                    {
                        t.RollBack();
                        return MepOperationResult.Fail("Could not locate open connectors at the junction point.");
                    }

                    List<XYZ> anchoredPts = CollectAnchoredEndPoints(curve1, curve2);
                    var involved = new MEPCurve[] { curve1, curve2 };

                    FamilyInstance fitting = null;
                    bool nativeOk = TryNativeFitting(
                        () => unionMode ? _doc.Create.NewUnionFitting(c1, c2) : _doc.Create.NewTransitionFitting(c1, c2),
                        anchoredPts, involved, out fitting);

                    if (!nativeOk)
                    {
                        fitting = PlaceLinearFittingManually(curve1, c1, curve2, c2, junction, unionMode);
                        if (fitting == null)
                            LogicalConnect(c1, c2);
                    }

                    if (fitting != null)
                    {
                        if (unionMode)
                        {
                            AlignUnionToPipeEnds(fitting, curve1, curve2);
                            TrimRunsToFittingSockets(fitting, curve1, curve2);
                            EnsurePhysicalUnionConnections(fitting, curve1, curve2);
                        }
                        else
                        {
                            TriggerAutoTrimSafe(fitting, anc1 || anc2);
                        }
                        EnsureSystemInterconnection(curve1, curve2, fitting);
                    }

                    t.Commit();

                    if (fitting != null)
                    {
                        try
                        {
                            Ai.AutoTrainingManager.Instance.RecordFittedElement(
                                _doc, fitting.Id, "Piping", unionMode ? "Auto Union" : "Auto Reducer");
                        }
                        catch { }
                    }

                    string fitName = fitting != null
                        ? (unionMode ? "Union/Coupling fitting" : "Reducer/Transition fitting")
                        : "Direct System Connection";
                    return MepOperationResult.OK(
                        $"Unified {curve1.GetType().Name} and {curve2.GetType().Name} with {fitName} and full System Interconnection",
                        fitting);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    return MepOperationResult.Fail(ex.Message);
                }
            }
        }

        private static void IdentifyNearFar(
            XYZ p10, XYZ p11, XYZ p20, XYZ p21,
            out XYZ near1, out XYZ far1, out XYZ near2, out XYZ far2)
        {
            double d00 = p10.DistanceTo(p20);
            double d01 = p10.DistanceTo(p21);
            double d10 = p11.DistanceTo(p20);
            double d11 = p11.DistanceTo(p21);

            if (d00 <= d01 && d00 <= d10 && d00 <= d11)
            { near1 = p10; far1 = p11; near2 = p20; far2 = p21; }
            else if (d01 <= d00 && d01 <= d10 && d01 <= d11)
            { near1 = p10; far1 = p11; near2 = p21; far2 = p20; }
            else if (d10 <= d00 && d10 <= d01 && d10 <= d11)
            { near1 = p11; far1 = p10; near2 = p20; far2 = p21; }
            else
            { near1 = p11; far1 = p10; near2 = p21; far2 = p20; }
        }

        private FamilyInstance PlaceLinearFittingManually(
            MEPCurve curve1, Connector c1, MEPCurve curve2, Connector c2, XYZ junction, bool unionMode)
        {
            FamilySymbol sym = unionMode ? FindUnionFamilySymbol(_doc) : FindReducerFamilySymbol(_doc);
            if (sym == null) return null;
            if (!sym.IsActive) sym.Activate();

            FamilyInstance fitting = _doc.Create.NewFamilyInstance(
                junction, sym, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            _doc.Regenerate();
            if (fitting?.MEPModel?.ConnectorManager == null) return fitting;

            if (unionMode) CopyPipeParametersToFitting(curve1, fitting);
            else CopyReducerParametersToFitting(curve1, curve2, fitting);
            _doc.Regenerate();

            var fitConns = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>().ToList();
            if (fitConns.Count < 2) return fitting;

            Connector fc1;
            Connector fc2;
            if (unionMode)
            {
                fc1 = fitConns.OrderBy(c => c.Origin.DistanceTo(c1.Origin)).FirstOrDefault();
                fitConns.Remove(fc1);
                fc2 = fitConns.OrderBy(c => c.Origin.DistanceTo(c2.Origin)).FirstOrDefault();
            }
            else
            {
                double c1Rad = c1.Radius;
                double c2Rad = c2.Radius;
                if (Math.Abs(fitConns[0].Radius - c1Rad) + Math.Abs(fitConns[1].Radius - c2Rad)
                    < Math.Abs(fitConns[1].Radius - c1Rad) + Math.Abs(fitConns[0].Radius - c2Rad))
                { fc1 = fitConns[0]; fc2 = fitConns[1]; }
                else
                { fc1 = fitConns[1]; fc2 = fitConns[0]; }
            }

            XYZ vCurrent = fc1.CoordinateSystem.BasisZ;
            XYZ vTarget = -c1.CoordinateSystem.BasisZ;
            if (vCurrent.DistanceTo(vTarget) > 1e-5)
            {
                XYZ axis = vCurrent.CrossProduct(vTarget);
                double angle = vCurrent.AngleTo(vTarget);
                if (axis.GetLength() < 1e-5)
                {
                    axis = vCurrent.CrossProduct(XYZ.BasisZ);
                    if (axis.GetLength() < 1e-5) axis = vCurrent.CrossProduct(XYZ.BasisX);
                }
                if (axis.GetLength() > 1e-5 && angle > 1e-5)
                {
                    Line axisLine = Line.CreateUnbound(junction, axis.Normalize());
                    try { ElementTransformUtils.RotateElement(_doc, fitting.Id, axisLine, angle); } catch { }
                    _doc.Regenerate();
                }
            }

            LogicalConnect(fc1, c1);
            LogicalConnect(fc2, c2);
            _doc.Regenerate();
            return fitting;
        }


        #endregion

        #region Helpers

        private MEPCurve CreateDuplicateCurveSegment(MEPCurve original, XYZ start, XYZ end)
        {
            ElementId levelId = original.ReferenceLevel?.Id ?? original.LevelId;
            if (original is Duct duct)
            {
                var newDuct = Duct.Create(_doc, duct.MEPSystem?.GetTypeId() ?? ElementId.InvalidElementId, duct.GetTypeId(), levelId, start, end);
                CopyParameters(duct, newDuct);
                return newDuct;
            }
            if (original is Conduit conduit)
            {
                var newConduit = Conduit.Create(_doc, conduit.GetTypeId(), start, end, levelId);
                CopyParameters(conduit, newConduit);
                return newConduit;
            }
            if (original is CableTray tray)
            {
                var newTray = CableTray.Create(_doc, tray.GetTypeId(), start, end, levelId);
                CopyParameters(tray, newTray);
                return newTray;
            }
            if (original is Pipe pipe)
            {
                var newPipe = Pipe.Create(_doc, pipe.MEPSystem?.GetTypeId() ?? ElementId.InvalidElementId, pipe.GetTypeId(), levelId, start, end);
                CopyParameters(pipe, newPipe);
                return newPipe;
            }
            return null;
        }

        private static void CopyParameters(MEPCurve src, MEPCurve target)
        {
            if (src == null || target == null) return;
            try
            {
                var pDia = src.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                        ?? src.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)
                        ?? src.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                if (pDia != null && pDia.HasValue)
                {
                    var tDia = target.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                            ?? target.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)
                            ?? target.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                    if (tDia != null && !tDia.IsReadOnly) tDia.Set(pDia.AsDouble());
                }

                var pW = src.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM) ?? src.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                var pH = src.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM) ?? src.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                if (pW != null && pW.HasValue)
                {
                    var tW = target.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM) ?? target.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                    if (tW != null && !tW.IsReadOnly) tW.Set(pW.AsDouble());
                }
                if (pH != null && pH.HasValue)
                {
                    var tH = target.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM) ?? target.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                    if (tH != null && !tH.IsReadOnly) tH.Set(pH.AsDouble());
                }
            }
            catch { }
        }

        
        private static double GetCurveDiameter(MEPCurve curve)
        {
            var pDia = curve.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                    ?? curve.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                    ?? curve.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
            if (pDia != null && pDia.HasValue) return pDia.AsDouble();
            
            if (curve.ConnectorManager != null)
            {
                foreach (Connector c in curve.ConnectorManager.Connectors)
                {
                    if (c.Shape == ConnectorProfileType.Round) return c.Radius * 2.0;
                }
            }
            return -1.0;
        }

        private static void CopyReducerParametersToFitting(MEPCurve pipe1, MEPCurve pipe2, FamilyInstance fitting)
        {
            if (pipe1 == null || pipe2 == null || fitting == null) return;
            try
            {
                double d1 = GetCurveDiameter(pipe1);
                double d2 = GetCurveDiameter(pipe2);
                
                if (d1 > 0 && d2 > 0)
                {
                    Parameter p1Dia = null, p2Dia = null;
                    Parameter p1Rad = null, p2Rad = null;
                    
                    foreach (Parameter p in fitting.Parameters)
                    {
                        if (!p.IsReadOnly && p.StorageType == StorageType.Double)
                        {
                            string pName = p.Definition.Name.ToLowerInvariant();
                            if (pName.Contains("radius"))
                            {
                                if (pName.Contains("1")) p1Rad = p;
                                else if (pName.Contains("2")) p2Rad = p;
                                else if (p1Rad == null) p1Rad = p;
                            }
                            else if (pName.Contains("diameter") || pName.Contains("size") || pName.Contains("nominal"))
                            {
                                if (pName.Contains("1")) p1Dia = p;
                                else if (pName.Contains("2")) p2Dia = p;
                                else if (p1Dia == null) p1Dia = p;
                            }
                        }
                    }

                    if (p1Dia != null) try { p1Dia.Set(d1); } catch { }
                    if (p1Rad != null) try { p1Rad.Set(d1 / 2.0); } catch { }
                    if (p2Dia != null) try { p2Dia.Set(d2); } catch { }
                    if (p2Rad != null) try { p2Rad.Set(d2 / 2.0); } catch { }
                }
            }
            catch { }
        }

        private static void CopyPipeParametersToFitting(MEPCurve pipe, FamilyInstance fitting)
        {
            if (pipe == null || fitting == null) return;
            try
            {
                double diaVal = -1.0;
                double radVal = -1.0;

                var pDia = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                        ?? pipe.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                        ?? pipe.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                        
                if (pDia != null && pDia.HasValue)
                {
                    diaVal = pDia.AsDouble();
                    radVal = diaVal / 2.0;
                }
                else if (pipe.ConnectorManager != null)
                {
                    // Fallback to connector size if parameter is missing
                    foreach (Connector c in pipe.ConnectorManager.Connectors)
                    {
                        if (c.Shape == ConnectorProfileType.Round)
                        {
                            radVal = c.Radius;
                            diaVal = radVal * 2.0;
                            break;
                        }
                    }
                }

                if (diaVal > 0)
                {
                    foreach (Parameter p in fitting.Parameters)
                    {
                        if (!p.IsReadOnly && p.StorageType == StorageType.Double)
                        {
                            string pName = p.Definition.Name.ToLowerInvariant();
                            
                            // Prevent setting diameter to a radius parameter
                            if (pName.Contains("radius"))
                            {
                                try { p.Set(radVal); } catch { }
                            }
                            else if (pName.Contains("diameter") || pName.Contains("nominal") || pName.Contains("size"))
                            {
                                try { p.Set(diaVal); } catch { }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static Connector FindOpenConnectorNear(ConnectorManager cm, XYZ pt, double tol)
        {
            if (cm == null) return null;
            Connector best = null;
            double minDist = double.MaxValue;

            foreach (Connector c in cm.Connectors)
            {
                if (c.IsConnected) continue;
                double d = c.Origin.DistanceTo(pt);
                if (d < tol && d < minDist)
                {
                    minDist = d;
                    best = c;
                }
            }

            return best;
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

        private static Connector FindClosestConnector(ConnectorManager cm, XYZ pt)
        {
            if (cm == null) return null;
            Connector best = null;
            double minDist = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                double d = c.Origin.DistanceTo(pt);
                if (d < minDist)
                {
                    minDist = d;
                    best = c;
                }
            }
            return best;
        }

        
        
        private void EnsureCrossRoutingPreference(MEPCurve curve)
        {
            if (curve is Pipe pipe)
            {
                try
                {
                    PipeType pipeType = _doc.GetElement(pipe.GetTypeId()) as PipeType;
                    if (pipeType != null && pipeType.RoutingPreferenceManager != null)
                    {
                        var rpm = pipeType.RoutingPreferenceManager;
                        if (rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Crosses) == 0)
                        {
                            FamilySymbol sym = FindCrossFamilySymbol(_doc);
                            if (sym != null)
                            {
                                RoutingPreferenceRule rule = new RoutingPreferenceRule(sym.Id, "Auto Cross Rule");
                                rpm.AddRule(RoutingPreferenceRuleGroupType.Crosses, rule);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static FamilySymbol FindCrossFamilySymbol(Document doc)
        {
            try
            {
                var symbols = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .Cast<FamilySymbol>()
                    .ToList();

                foreach (var sym in symbols)
                {
                    var pType = sym.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)
                             ?? sym.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                    if (pType != null && pType.HasValue && pType.AsInteger() == (int)PartType.Cross)
                    {
                        if (!sym.IsActive) sym.Activate();
                        return sym;
                    }
                }
            }
            catch { }
            return null;
        }

        private void EnsureElbowRoutingPreference(MEPCurve curve)
        {
            if (curve is Pipe pipe)
            {
                try
                {
                    PipeType pipeType = _doc.GetElement(pipe.GetTypeId()) as PipeType;
                    if (pipeType != null && pipeType.RoutingPreferenceManager != null)
                    {
                        var rpm = pipeType.RoutingPreferenceManager;
                        if (rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows) == 0)
                        {
                            FamilySymbol sym = FindElbowFamilySymbol(_doc);
                            if (sym != null)
                            {
                                RoutingPreferenceRule rule = new RoutingPreferenceRule(sym.Id, "Auto Elbow Rule");
                                rpm.AddRule(RoutingPreferenceRuleGroupType.Elbows, rule);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static FamilySymbol FindElbowFamilySymbol(Document doc)
        {
            try
            {
                var symbols = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .Cast<FamilySymbol>()
                    .ToList();

                foreach (var sym in symbols)
                {
                    var pType = sym.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)
                             ?? sym.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                    if (pType != null && pType.HasValue)
                    {
                        if (pType.AsInteger() == (int)PartType.Elbow)
                        {
                            if (!sym.IsActive) sym.Activate();
                            return sym;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void EnsureTransitionRoutingPreference(MEPCurve curve)
        {
            if (curve is Pipe pipe)
            {
                try
                {
                    PipeType pipeType = _doc.GetElement(pipe.GetTypeId()) as PipeType;
                    if (pipeType != null && pipeType.RoutingPreferenceManager != null)
                    {
                        var rpm = pipeType.RoutingPreferenceManager;
                        if (rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Transitions) == 0)
                        {
                            FamilySymbol sym = FindReducerFamilySymbol(_doc);
                            if (sym != null)
                            {
                                RoutingPreferenceRule rule = new RoutingPreferenceRule(sym.Id, "Reducer Rule");
                                rpm.AddRule(RoutingPreferenceRuleGroupType.Transitions, rule);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private void EnsureUnionRoutingPreference(MEPCurve curve)
        {
            if (curve == null) return;
            try
            {
                if (curve is Pipe pipe && pipe.PipeType != null)
                {
                    RoutingPreferenceManager rpm = pipe.PipeType.RoutingPreferenceManager;
                    if (rpm != null && rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Unions) == 0)
                    {
                        FamilySymbol unionSym = FindUnionFamilySymbol(_doc);
                        if (unionSym != null)
                        {
                            RoutingPreferenceRule rule = new RoutingPreferenceRule(unionSym.Id, "Auto Union");
                            rpm.AddRule(RoutingPreferenceRuleGroupType.Unions, rule);
                        }
                    }
                }
            }
            catch { }
        }

        private static FamilySymbol FindUnionFamilySymbol(Document doc)
        {
            try
            {
                var symbols = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .Cast<FamilySymbol>()
                    .ToList();

                // 1. Check PartType.Union or PartType.Coupling
                foreach (var sym in symbols)
                {
                    var pType = sym.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)
                             ?? sym.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                    if (pType != null && pType.HasValue)
                    {
                        int pt = pType.AsInteger();
                        if (pt == (int)PartType.Union)
                        {
                            if (!sym.IsActive) sym.Activate();
                            return sym;
                        }
                    }
                }

                // 2. Check name contains "union" or "coupling"
                foreach (var sym in symbols)
                {
                    string name = ((sym.FamilyName ?? "") + " " + (sym.Name ?? "")).ToLowerInvariant();
                    if (name.Contains("union") || name.Contains("coupling"))
                    {
                        if (!sym.IsActive) sym.Activate();
                        return sym;
                    }
                }

                // 3. Any fitting
                foreach (var sym in symbols)
                {
                    if (!sym.IsActive) sym.Activate();
                    return sym;
                }
            }
            catch { }

            return null;
        }

        
        private static FamilySymbol FindReducerFamilySymbol(Document doc)
        {
            try
            {
                var symbols = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .Cast<FamilySymbol>()
                    .ToList();

                foreach (var sym in symbols)
                {
                    var pType = sym.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)
                             ?? sym.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                    if (pType != null && pType.HasValue)
                    {
                        if (pType.AsInteger() == (int)PartType.Transition)
                        {
                            if (!sym.IsActive) sym.Activate();
                            return sym;
                        }
                    }
                }

                foreach (var sym in symbols)
                {
                    string name = ((sym.FamilyName ?? "") + " " + (sym.Name ?? "")).ToLowerInvariant();
                    if (name.Contains("transition") || name.Contains("reducer"))
                    {
                        if (!sym.IsActive) sym.Activate();
                        return sym;
                    }
                }
                
                foreach (var sym in symbols)
                {
                    if (!sym.IsActive) sym.Activate();
                    return sym;
                }
            }
            catch { }
            return null;
        }

        private void SyncMepSystem(MEPCurve src, MEPCurve target)
        {
            if (src == null || target == null) return;
            try
            {
                // Pipe System Type synchronization
                var srcPipeSys = src.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                var tgtPipeSys = target.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);

                if (srcPipeSys != null && srcPipeSys.HasValue && srcPipeSys.AsElementId() != ElementId.InvalidElementId)
                {
                    if (tgtPipeSys != null && !tgtPipeSys.IsReadOnly && tgtPipeSys.AsElementId() != srcPipeSys.AsElementId())
                    {
                        tgtPipeSys.Set(srcPipeSys.AsElementId());
                    }
                }
                else if (tgtPipeSys != null && tgtPipeSys.HasValue && tgtPipeSys.AsElementId() != ElementId.InvalidElementId)
                {
                    if (srcPipeSys != null && !srcPipeSys.IsReadOnly && srcPipeSys.AsElementId() != tgtPipeSys.AsElementId())
                    {
                        srcPipeSys.Set(tgtPipeSys.AsElementId());
                    }
                }
                else if (src is Pipe || target is Pipe)
                {
                    var pst = new FilteredElementCollector(_doc)
                        .OfClass(typeof(PipingSystemType))
                        .Cast<PipingSystemType>()
                        .FirstOrDefault(st => st.SystemClassification == MEPSystemClassification.SupplyHydronic)
                        ?? new FilteredElementCollector(_doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().FirstOrDefault();

                    if (pst != null)
                    {
                        if (srcPipeSys != null && !srcPipeSys.IsReadOnly) srcPipeSys.Set(pst.Id);
                        if (tgtPipeSys != null && !tgtPipeSys.IsReadOnly) tgtPipeSys.Set(pst.Id);
                    }
                }

                // Duct System Type synchronization
                var srcDuctSys = src.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                var tgtDuctSys = target.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);

                if (srcDuctSys != null && srcDuctSys.HasValue && srcDuctSys.AsElementId() != ElementId.InvalidElementId)
                {
                    if (tgtDuctSys != null && !tgtDuctSys.IsReadOnly && tgtDuctSys.AsElementId() != srcDuctSys.AsElementId())
                    {
                        tgtDuctSys.Set(srcDuctSys.AsElementId());
                    }
                }
                else if (tgtDuctSys != null && tgtDuctSys.HasValue && tgtDuctSys.AsElementId() != ElementId.InvalidElementId)
                {
                    if (srcDuctSys != null && !srcDuctSys.IsReadOnly && srcDuctSys.AsElementId() != tgtDuctSys.AsElementId())
                    {
                        srcDuctSys.Set(tgtDuctSys.AsElementId());
                    }
                }
            }
            catch { }
        }

        private static void EnsureSystemInterconnection(MEPCurve curve1, MEPCurve curve2, FamilyInstance fitting)
        {
            if (curve1 == null || curve2 == null) return;
            try
            {
                MEPSystem sys = curve1.MEPSystem ?? curve2.MEPSystem;
                if (sys == null && fitting != null && fitting.MEPModel?.ConnectorManager != null)
                {
                    sys = fitting.MEPModel.ConnectorManager.Connectors.Cast<Connector>().Select(c => c.MEPSystem).FirstOrDefault(s => s != null);
                }

                if (sys != null)
                {
                    var set = new ConnectorSet();
                    if (curve1.ConnectorManager != null)
                    {
                        foreach (Connector c in curve1.ConnectorManager.Connectors)
                        {
                            if (c != null) set.Insert(c);
                        }
                    }
                    if (curve2.ConnectorManager != null)
                    {
                        foreach (Connector c in curve2.ConnectorManager.Connectors)
                        {
                            if (c != null) set.Insert(c);
                        }
                    }
                    if (fitting != null && fitting.MEPModel?.ConnectorManager != null)
                    {
                        foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors)
                        {
                            if (c != null) set.Insert(c);
                        }
                    }

                    try { sys.Add(set); } catch { }
                }
            }
            catch { }
        }

        private static XYZ Slerp(XYZ a, XYZ b, double t)
        {
            a = SafeNormalize(a);
            b = SafeNormalize(b);
            t = Math.Max(0.0, Math.Min(1.0, t));
            double dot = Math.Max(-1.0, Math.Min(1.0, a.DotProduct(b)));
            if (dot > 0.9995)
                return SafeNormalize(a + (b - a) * t);
            if (dot < -0.9995)
            {
                XYZ n = a.CrossProduct(XYZ.BasisZ);
                if (n.GetLength() < 1e-8) n = a.CrossProduct(XYZ.BasisX);
                n = n.Normalize();
                double ang = Math.PI * t;
                return SafeNormalize(a * Math.Cos(ang) + n * Math.Sin(ang));
            }
            double theta = Math.Acos(dot);
            double sinTheta = Math.Sin(theta);
            double w1 = Math.Sin((1.0 - t) * theta) / sinTheta;
            double w2 = Math.Sin(t * theta) / sinTheta;
            return (a.Multiply(w1) + b.Multiply(w2)).Normalize();
        }
        
        private bool IsFarEndAnchored(MEPCurve curve, XYZ apex)
        {
            if (curve == null || curve.ConnectorManager == null) return false;
            Connector farConn = null;
            double maxDist = -1;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                if (c.ConnectorType == ConnectorType.End || c.ConnectorType == ConnectorType.Curve)
                {
                    double d = c.Origin.DistanceTo(apex);
                    if (d > maxDist)
                    {
                        maxDist = d;
                        farConn = c;
                    }
                }
            }
            if (farConn != null && farConn.IsConnected)
            {
                foreach (Connector refConn in farConn.AllRefs)
                {
                    if (refConn.Owner.Id != curve.Id && refConn.ConnectorType != ConnectorType.Logical)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        
        private class FarEndState
        {
            public MEPCurve Curve { get; set; }
            public XYZ OriginalPosition { get; set; }
            public System.Collections.Generic.List<Connector> ConnectedTo { get; set; } = new System.Collections.Generic.List<Connector>();

            public bool IsDisplaced(MEPCurve curve)
        {
            if (curve.Location is LocationCurve lc && lc.Curve is Line line)
            {
                XYZ p0 = line.GetEndPoint(0);
                XYZ p1 = line.GetEndPoint(1);
                double d0 = p0.DistanceTo(OriginalPosition);
                double d1 = p1.DistanceTo(OriginalPosition);
                return (d0 > DisplacementTolFt && d1 > DisplacementTolFt);
            }
            return false;
        }

        public void Restore(Document doc)
            {
                if (Curve == null || !(Curve.Location is LocationCurve lc) || !(lc.Curve is Line line)) return;

                XYZ p0 = line.GetEndPoint(0);
                XYZ p1 = line.GetEndPoint(1);

                double d0 = p0.DistanceTo(OriginalPosition);
                double d1 = p1.DistanceTo(OriginalPosition);

                // If displaced, fix the far end back to OriginalPosition
                if (d0 > DisplacementTolFt && d1 > DisplacementTolFt)
                {
                    XYZ nearPt = (d0 > d1) ? p0 : p1; // The other end is the near end
                    lc.Curve = Line.CreateBound(OriginalPosition, nearPt);
                    doc.Regenerate();
                }

                // Restore connections if disjunction happened
                if (ConnectedTo.Count > 0)
                {
                    Connector currentFarConn = FindClosestConnector(Curve.ConnectorManager, OriginalPosition);
                    if (currentFarConn != null)
                    {
                        foreach (var target in ConnectedTo)
                        {
                            if (!currentFarConn.IsConnectedTo(target))
                            {
                                try { currentFarConn.ConnectTo(target); } catch { }
                            }
                        }
                    }
                }
            }
        }

        private FarEndState CaptureFarEnd(MEPCurve curve, XYZ apex)
        {
            if (curve == null || !(curve.Location is LocationCurve lc) || !(lc.Curve is Line line)) return null;
            
            XYZ p0 = line.GetEndPoint(0);
            XYZ p1 = line.GetEndPoint(1);
            XYZ farPt = (p0.DistanceTo(apex) > p1.DistanceTo(apex)) ? p0 : p1;

            var state = new FarEndState { Curve = curve, OriginalPosition = farPt };

            Connector farConn = FindClosestConnector(curve.ConnectorManager, farPt);
            if (farConn != null && farConn.IsConnected)
            {
                foreach (Connector refConn in farConn.AllRefs)
                {
                    if (refConn.Owner.Id != curve.Id && refConn.ConnectorType != ConnectorType.Logical)
                    {
                        state.ConnectedTo.Add(refConn);
                    }
                }
            }
            return state;
        }



        private static void TriggerAutoTrim(FamilyInstance fitting, Document doc)
        {
            if (fitting == null) return;
            try
            {
                // Jiggle size parameter to force Revit's auto-trim on connected pipes
                Parameter sizeParam = null;
                foreach (Parameter p in fitting.Parameters)
                {
                    if (!p.IsReadOnly && p.StorageType == StorageType.Double)
                    {
                        string name = p.Definition.Name.ToLowerInvariant();
                        if (name.Contains("diameter") || name.Contains("radius") || name.Contains("nominal") || name.Contains("size"))
                        {
                            sizeParam = p;
                            break;
                        }
                    }
                }

                if (sizeParam != null)
                {
                    double originalValue = sizeParam.AsDouble();
                    if (originalValue > 0)
                    {
                        sizeParam.Set(originalValue * 1.05); // +5%
                        doc.Regenerate();
                        sizeParam.Set(originalValue); // Restore
                        doc.Regenerate();
                    }
                }
                
                // Also do a microscopic move just in case (forces connector re-evaluation)
                XYZ tinyMove = new XYZ(0, 0, 0.001);
                try { ElementTransformUtils.MoveElement(doc, fitting.Id, tinyMove); } catch { }
                doc.Regenerate();
                try { ElementTransformUtils.MoveElement(doc, fitting.Id, -tinyMove); } catch { }
                doc.Regenerate();
            }
            catch { }
        }

        public MepOperationResult ConnectTee(FamilyInstance fitting, MEPCurve newCurve)
        {
            if (fitting == null || newCurve == null) return MepOperationResult.Fail("Fitting and Pipe must be provided.");
            if (fitting.MEPModel?.ConnectorManager == null) return MepOperationResult.Fail("Selected fitting is not MEP.");

            int connCount = fitting.MEPModel.ConnectorManager.Connectors.Size;
            if (connCount != 2)
                return MepOperationResult.Fail("Selected fitting must be an Elbow (2 connectors) to upgrade to a Tee.");

            LocationPoint locPt = fitting.Location as LocationPoint;
            if (locPt == null) return MepOperationResult.Fail("Fitting has no location point.");
            XYZ origin = locPt.Point;

            if (!TryGetLine(newCurve, out _, out _, out _, out _))
                return MepOperationResult.Fail("Selected run must have linear geometry.");

            var existingRuns = new List<MEPCurve>();
            foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors)
            {
                if (!c.IsConnected) continue;
                foreach (Connector refC in c.AllRefs)
                {
                    if (refC.Owner.Id != fitting.Id && refC.Owner is MEPCurve mep && !existingRuns.Contains(mep))
                        existingRuns.Add(mep);
                }
            }

            if (existingRuns.Count != 2)
                return MepOperationResult.Fail($"Elbow must have exactly 2 connected pipes to convert to a tee. Found {existingRuns.Count}.");

            using (Transaction t = new Transaction(_doc, "Convert Elbow to Tee"))
            {
                AllowNetworkDisconnects(t);
                t.Start();
                try
                {
                    ElementId elbowId = fitting.Id;

                    // Release far-end network ties BEFORE any curve edit / NewTeeFitting.
                    // Revit reverse-flips a through-run while the far end is still joined
                    // and posts "pipe has been modified to be in the opposite direction"
                    // (Error cannot be ignored — only resolution is Delete Element(s)).
                    var farLinks = new List<PhysicalLink>();
                    farLinks.AddRange(CapturePhysicalLinks(existingRuns[0], elbowId));
                    farLinks.AddRange(CapturePhysicalLinks(existingRuns[1], elbowId));
                    farLinks.AddRange(CapturePhysicalLinks(newCurve, elbowId));
                    DisconnectPhysicalLinks(farLinks);
                    DisconnectAllPhysical(existingRuns[0]);
                    DisconnectAllPhysical(existingRuns[1]);
                    DisconnectAllPhysical(newCurve);

                    DisconnectFittingFromRuns(fitting);
                    if (IsLive(fitting))
                    {
                        _doc.Delete(fitting.Id);
                        _doc.Regenerate();
                    }

                    if (!IsLive(existingRuns[0]) || !IsLive(existingRuns[1]) || !IsLive(newCurve))
                        throw new Exception("A connected pipe was lost after removing the elbow.");

                    PrepareRunToJunction(newCurve, origin, out _, out _, out _);

                    var snaps = new List<FarEndSnap>
                    {
                        SnapshotFar(existingRuns[0], origin),
                        SnapshotFar(existingRuns[1], origin),
                        SnapshotFar(newCurve, origin)
                    };

                    FamilyInstance tee = TryElbowUpgradeTee(
                        existingRuns[0].Id, existingRuns[1].Id, newCurve.Id,
                        snaps, origin);

                    if (!IsLive(tee))
                        throw new Exception("Could not convert the elbow to a tee. Check that the new run reaches the elbow.");

                    foreach (var s in snaps)
                    {
                        if (s.Curve != null)
                            DisconnectRunFromFitting(s.Curve, tee);
                    }
                    SacredRestoreAfterFitting(tee, snaps, origin);
                    ReconnectPhysicalLinks(farLinks);
                    tee = LiveFitting(tee.Id);
                    // Do not restore to the tee CENTER — that pulls pipes inside the body.
                    // Do not auto-trim: it snaps anchored networks. Socket trim is enough.

                    t.Commit();
                    if (IsLive(tee))
                    {
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, tee.Id, "Piping", "Auto Tee (Elbow upgrade)"); } catch { }
                    }
                    return MepOperationResult.OK("Converted Elbow to Tee and connected pipe.", tee);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    return MepOperationResult.Fail(ex.Message);
                }
            }
        }

        /// <summary>
        /// Elbow → Tee: assign through-run vs branch from the three pipe directions
        /// (most-opposite pair is the through-run; leftover is the 90° branch).
        /// Dummy continues one through-leg so NewTeeFitting orients the body correctly.
        /// </summary>
        private FamilyInstance TryElbowUpgradeTee(
            ElementId id1, ElementId id2, ElementId idNew,
            List<FarEndSnap> snaps, XYZ origin)
        {
            var allFars = new List<XYZ>();
            foreach (var s in snaps)
                if (s.Far != null) allFars.Add(s.Far);

            MEPCurve[] involved = { LiveCurve(id1), LiveCurve(id2), LiveCurve(idNew) };
            if (LiveOpenNear(id1, origin, 2.5) == null
                || LiveOpenNear(id2, origin, 2.5) == null
                || LiveOpenNear(idNew, origin, 2.5) == null)
                return null;

            XYZ d1 = OutwardAlongRun(SnapById(snaps, id1), origin);
            XYZ d2 = OutwardAlongRun(SnapById(snaps, id2), origin);
            XYZ dN = OutwardAlongRun(SnapById(snaps, idNew), origin);

            // Through-run = most opposite pair (dot → -1). Remaining pipe is the branch.
            double s12 = d1.DotProduct(d2);
            double s1n = d1.DotProduct(dN);
            double s2n = d2.DotProduct(dN);

            ElementId throughA, throughB, branchId;
            if (s1n <= s12 && s1n <= s2n)
            {
                throughA = id1; throughB = idNew; branchId = id2;
            }
            else if (s2n <= s12 && s2n <= s1n)
            {
                throughA = id2; throughB = idNew; branchId = id1;
            }
            else
            {
                throughA = id1; throughB = id2; branchId = idNew;
            }

            FamilyInstance tee;

            // Native: NewTeeFitting(main, main, branch) — first two are the through-run.
            if (TryNativeFitting(() =>
                {
                    Connector mainA = LiveOpenNear(throughA, origin, 2.5);
                    Connector mainB = LiveOpenNear(throughB, origin, 2.5);
                    Connector branch = LiveOpenNear(branchId, origin, 2.5);
                    if (mainA == null || mainB == null || branch == null)
                        throw new InvalidOperationException("live connectors missing");
                    return _doc.Create.NewTeeFitting(mainA, mainB, branch);
                }, allFars, involved, out tee) && IsLive(tee))
            {
                AlignTeeToSnaps(tee, snaps, origin);
                return LiveFitting(tee.Id);
            }

            // Dummy continues throughA; leftover port (opposite host) gets throughB.
            tee = TryDummyElbowTee(throughA, throughB, branchId, snaps, origin);
            if (IsLive(tee)) return tee;

            tee = TryDummyElbowTee(throughB, throughA, branchId, snaps, origin);
            return IsLive(tee) ? tee : null;
        }

        /// <summary>
        /// Dummy continues <paramref name="hostId"/> past the junction. NewTeeFitting
        /// uses host+dummy as through-run and <paramref name="branchId"/> as the 90°
        /// port. After dummy delete, leftover through-port gets <paramref name="leftoverId"/>.
        /// </summary>
        private FamilyInstance TryDummyElbowTee(
            ElementId hostId, ElementId leftoverId, ElementId branchId,
            List<FarEndSnap> snaps, XYZ origin)
        {
            if (hostId == leftoverId || hostId == branchId || leftoverId == branchId)
                return null;

            Connector cHost = LiveOpenNear(hostId, origin, 2.5);
            MEPCurve hostRun = LiveCurve(hostId);
            if (cHost == null || hostRun == null) return null;

            XYZ dummyDir, dummyStart;
            try
            {
                dummyDir = SafeNormalize(cHost.CoordinateSystem.BasisZ);
                dummyStart = cHost.Origin;
            }
            catch { return null; }

            FarEndSnap leftoverSnap = SnapById(snaps, leftoverId);
            XYZ leftoverOut = OutwardAlongRun(leftoverSnap, origin);

            MEPCurve dummy = CreateDummyCurve(hostRun, dummyStart, dummyStart + dummyDir.Multiply(2.0));
            if (dummy == null) return null;
            OrientDummyAsHostContinuation(hostRun, dummyStart, dummy);
            ElementId dummyId = dummy.Id;
            var protect = new HashSet<ElementId> { hostId, leftoverId, branchId };

            FamilyInstance tee = null;
            bool ok = false;
            try
            {
                ok = TryFittingKeepFar(() =>
                {
                    Connector h = LiveOpenNear(hostId, origin, 2.5);
                    Connector d = LiveOpenNear(dummyId, dummyStart, 2.5) ?? LiveOpenNear(dummyId, origin, 2.5);
                    Connector b = LiveOpenNear(branchId, origin, 2.5);
                    if (h == null || d == null || b == null)
                        throw new InvalidOperationException("live connectors missing");
                    return _doc.Create.NewTeeFitting(h, d, b);
                }, snaps, new[] { LiveCurve(hostId), LiveCurve(leftoverId), LiveCurve(branchId) }, origin, out tee);
            }
            catch { ok = false; tee = null; }

            ElementId teeId = IsLive(tee) ? tee.Id : ElementId.InvalidElementId;
            DeleteDummy(_doc, LiveCurve(dummyId), protect);
            tee = LiveFitting(teeId);
            if (!ok || !IsLive(tee))
                return null;

            AlignTeeToSnaps(tee, snaps, origin);
            tee = LiveFitting(teeId);
            if (!IsLive(tee)) return null;

            Connector leftover = OpenConnectorFacing(tee, leftoverOut);
            try { AttachRunToLeftoverPort(leftoverSnap, leftover); } catch { }
            return LiveFitting(teeId);
        }

        /// <summary>
        /// Places a Cross on four existing MEP runs whose near ends meet.
        /// Sacred: far ends of anchored runs never move. Native first, then dummy/logical.
        /// Does not change Tee-to-Cross / open-port Cross (fitting + pipe).
        /// </summary>
        public MepOperationResult ConnectCross(MEPCurve curve1, MEPCurve curve2, MEPCurve curve3, MEPCurve curve4)
        {
            if (curve1 == null || curve2 == null || curve3 == null || curve4 == null)
                return MepOperationResult.Fail("Please select four pipes (or MEP runs) to place a Cross.");

            var runs = new[] { curve1, curve2, curve3, curve4 };
            if (runs.Select(r => r.Id).Distinct().Count() != 4)
                return MepOperationResult.Fail("Please select four distinct MEP elements.");

            XYZ[] p0 = new XYZ[4];
            XYZ[] p1 = new XYZ[4];
            for (int i = 0; i < 4; i++)
            {
                if (!TryGetLine(runs[i], out _, out _, out p0[i], out p1[i]))
                    return MepOperationResult.Fail("Selected elements must have linear geometries.");
            }

            if (!TryFourPipeJunction(p0[0], p1[0], p0[1], p1[1], p0[2], p1[2], p0[3], p1[3], out XYZ origin))
                return MepOperationResult.Fail("Could not find a common Cross junction for the four runs.");

            for (int i = 0; i < 4; i++)
            {
                if (IsInteriorPoint(p0[i], p1[i], origin))
                    return MepOperationResult.Fail("One run passes through the junction. Split it first, or use Cross on a Tee plus a pipe.");
            }

            XYZ[] outward = new XYZ[4];
            for (int i = 0; i < 4; i++)
                outward[i] = OutwardFromOrigin(p0[i], p1[i], origin);

            // Three ways to split 4 runs into two through-pairs. Best opposite pair first.
            var pairings = new[]
            {
                (a: 0, b: 1, c: 2, d: 3),
                (a: 0, b: 2, c: 1, d: 3),
                (a: 0, b: 3, c: 1, d: 2)
            };
            Array.Sort(pairings, (x, y) =>
            {
                double sx = outward[x.a].DotProduct(outward[x.b]) + outward[x.c].DotProduct(outward[x.d]);
                double sy = outward[y.a].DotProduct(outward[y.b]) + outward[y.c].DotProduct(outward[y.d]);
                return sx.CompareTo(sy);
            });

            using (Transaction t = new Transaction(_doc, "Connect MEP Cross (4 pipes)"))
            {
                AllowNetworkDisconnects(t);
                t.Start();
                try
                {
                    var runIds = new HashSet<ElementId>(runs.Select(r => r.Id));
                    var farLinks = new List<PhysicalLink>();
                    foreach (MEPCurve r in runs)
                    {
                        foreach (PhysicalLink link in CapturePhysicalLinks(r, ElementId.InvalidElementId))
                        {
                            if (!runIds.Contains(link.PartnerId))
                                farLinks.Add(link);
                        }
                    }
                    DisconnectPhysicalLinks(farLinks);
                    foreach (MEPCurve r in runs)
                        DisconnectAllPhysical(r);

                    bool[] anc = new bool[4];
                    XYZ[] fars = new XYZ[4];
                    bool[] p0Far = new bool[4];
                    for (int i = 0; i < 4; i++)
                    {
                        if (!TrimRunToPoint(runs[i], origin, out fars[i], out p0Far[i], out anc[i]))
                        {
                            t.RollBack();
                            return MepOperationResult.Fail("A selected run is too short to connect with a Cross.");
                        }
                    }

                    _doc.Regenerate();

                    foreach (MEPCurve r in runs)
                        EnsureCrossRoutingPreference(r);

                    // Snap junction to the four near ends (stubs), not the infinite-line average.
                    XYZ nearSum = XYZ.Zero;
                    XYZ[] outwardNow = new XYZ[4];
                    for (int i = 0; i < 4; i++)
                    {
                        if (!TryGetLine(runs[i], out _, out _, out XYZ a, out XYZ b)) continue;
                        XYZ near = a.DistanceTo(origin) <= b.DistanceTo(origin) ? a : b;
                        nearSum += near;
                        outwardNow[i] = OutwardFromOrigin(a, b, origin);
                    }
                    origin = nearSum / 4.0;

                    List<XYZ> anchoredPts = CollectAnchoredEndPoints(runs);
                    ElementId[] ids = runs.Select(r => r.Id).ToArray();
                    FamilyInstance cross = null;

                    foreach (var p in pairings)
                    {
                        int[] order = { p.a, p.b, p.c, p.d };
                        cross = PlaceCrossFromPlusDummies(runs, outwardNow, order, origin);
                        if (IsLive(cross)) break;

                        cross = PlaceCrossFromFourDummies(runs, outwardNow, order, origin);
                        if (IsLive(cross)) break;

                        cross = TryNativeFourPipeCross(ids[p.a], ids[p.b], ids[p.c], ids[p.d], origin, new List<XYZ>(), runs);
                        if (IsLive(cross)) break;

                        cross = PlaceFourPipeCrossWithDummy(
                            LiveCurve(ids[p.a]), LiveCurve(ids[p.b]),
                            LiveCurve(ids[p.c]), LiveCurve(ids[p.d]),
                            origin, fars[p.d], p0Far[p.d], anc[p.d], new List<XYZ>());
                        if (IsLive(cross)) break;

                        cross = PlaceFourPipeCrossWithDummy(
                            LiveCurve(ids[p.c]), LiveCurve(ids[p.d]),
                            LiveCurve(ids[p.a]), LiveCurve(ids[p.b]),
                            origin, fars[p.b], p0Far[p.b], anc[p.b], new List<XYZ>());
                        if (IsLive(cross)) break;
                    }

                    if (!IsLive(cross))
                        cross = PlaceCrossFamilyFallback(runs, outwardNow, pairings[0], origin);

                    if (!IsLive(cross))
                    {
                        t.RollBack();
                        return MepOperationResult.Fail("Could not place a Cross on the four selected runs.");
                    }

                    TrimRunsToFittingSockets(cross, runs);
                    ReconnectPhysicalLinks(farLinks);
                    TriggerAutoTrimSafe(cross, anc[0] || anc[1] || anc[2] || anc[3]);

                    t.Commit();
                    try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, cross.Id, "Piping", "Auto Cross (4 pipes)"); } catch { }
                    return MepOperationResult.OK(
                        $"Connected four runs with Cross at ({origin.X:F1}, {origin.Y:F1}, {origin.Z:F1})",
                        cross);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    return MepOperationResult.Fail("Cross operation error: " + ex.Message);
                }
            }
        }

        private static bool TryFourPipeJunction(
            XYZ a0, XYZ a1, XYZ b0, XYZ b1, XYZ c0, XYZ c1, XYZ d0, XYZ d1, out XYZ origin)
        {
            origin = XYZ.Zero;
            XYZ da = SafeNormalize(a1 - a0);
            XYZ db = SafeNormalize(b1 - b0);
            XYZ dc = SafeNormalize(c1 - c0);
            XYZ dd = SafeNormalize(d1 - d0);
            XYZ sum = XYZ.Zero;
            int n = 0;
            void Acc(XYZ p, XYZ q) { sum += (p + q) * 0.5; n++; }
            if (LineClosestApproach(a0, da, b0, db, out _, out _, out _, out XYZ pab, out XYZ pba)) Acc(pab, pba);
            if (LineClosestApproach(a0, da, c0, dc, out _, out _, out _, out XYZ pac, out XYZ pca)) Acc(pac, pca);
            if (LineClosestApproach(a0, da, d0, dd, out _, out _, out _, out XYZ pad, out XYZ pda)) Acc(pad, pda);
            if (LineClosestApproach(b0, db, c0, dc, out _, out _, out _, out XYZ pbc, out XYZ pcb)) Acc(pbc, pcb);
            if (LineClosestApproach(b0, db, d0, dd, out _, out _, out _, out XYZ pbd, out XYZ pdb)) Acc(pbd, pdb);
            if (LineClosestApproach(c0, dc, d0, dd, out _, out _, out _, out XYZ pcd, out XYZ pdc)) Acc(pcd, pdc);
            if (n == 0) return false;
            origin = sum / n;
            return true;
        }

        private MEPCurve CreateDummyCurveForCross(MEPCurve template, XYZ start, XYZ end)
        {
            MEPCurve dummy = CreateDummyCurve(template, start, end);
            if (dummy != null) return dummy;
            if (!(template is Pipe pipe) || start == null || end == null) return null;
            if (start.DistanceTo(end) < MinRunLenFt) return null;
            try
            {
                ElementId sysId = pipe.MEPSystem?.GetTypeId() ?? ElementId.InvalidElementId;
                if (sysId == ElementId.InvalidElementId)
                {
                    sysId = new FilteredElementCollector(_doc)
                        .OfClass(typeof(PipingSystemType))
                        .FirstElementId();
                }
                ElementId levelId = pipe.ReferenceLevel?.Id ?? pipe.LevelId;
                Pipe np = Pipe.Create(_doc, sysId, pipe.GetTypeId(), levelId, start, end);
                CopyParameters(pipe, np);
                DisconnectAllPhysical(np);
                return np;
            }
            catch { return null; }
        }

        private MEPCurve SplitDummyAt(MEPCurve dummy, XYZ pt)
        {
            if (dummy == null || pt == null) return null;
            try
            {
                if (dummy is Pipe)
                {
                    ElementId id2 = PlumbingUtils.BreakCurve(_doc, dummy.Id, pt);
                    return _doc.GetElement(id2) as MEPCurve;
                }
            }
            catch { }
            try
            {
                if (dummy is Duct)
                {
                    ElementId id2 = MechanicalUtils.BreakCurve(_doc, dummy.Id, pt);
                    return _doc.GetElement(id2) as MEPCurve;
                }
            }
            catch { }
            try
            {
                if (!TryGetLine(dummy, out LocationCurve lc, out _, out XYZ a, out XYZ b)) return null;
                if (a.DistanceTo(pt) < MinRunLenFt || b.DistanceTo(pt) < MinRunLenFt) return null;
                lc.Curve = Line.CreateBound(a, pt);
                MEPCurve other = CreateDummyCurveForCross(dummy, pt, b);
                DisconnectAllPhysical(dummy);
                if (other != null) DisconnectAllPhysical(other);
                return other;
            }
            catch { return null; }
        }

        /// <summary>
        /// Plus-sign dummy: one through-run along each axis, split at the junction,
        /// then NewCrossFitting on the four dummy ends. Live pipes are not in the create.
        /// </summary>
        private FamilyInstance PlaceCrossFromPlusDummies(
            MEPCurve[] runs, XYZ[] outward, int[] order, XYZ origin)
        {
            if (runs == null || runs.Length != 4 || order == null || order.Length != 4 || origin == null)
                return null;

            XYZ mainAxis = outward[order[0]];
            XYZ brAxis = outward[order[2]];
            if (mainAxis == null || brAxis == null) return null;
            mainAxis = SafeNormalize(mainAxis);
            brAxis = SafeNormalize(brAxis);
            if (Math.Abs(mainAxis.DotProduct(brAxis)) > 0.92)
                return null;

            var protect = new HashSet<ElementId>();
            foreach (MEPCurve r in runs)
                if (r != null) protect.Add(r.Id);

            var dummyIds = new List<ElementId>();
            try
            {
                const double arm = 2.0;
                MEPCurve dummyMain = CreateDummyCurveForCross(runs[order[0]], origin - mainAxis.Multiply(arm), origin + mainAxis.Multiply(arm));
                MEPCurve dummyBr = CreateDummyCurveForCross(runs[order[2]], origin - brAxis.Multiply(arm), origin + brAxis.Multiply(arm));
                if (dummyMain == null || dummyBr == null)
                {
                    DeleteDummy(_doc, dummyMain, protect);
                    DeleteDummy(_doc, dummyBr, protect);
                    return null;
                }
                dummyIds.Add(dummyMain.Id);
                dummyIds.Add(dummyBr.Id);
                DisconnectAllPhysical(dummyMain);
                DisconnectAllPhysical(dummyBr);
                _doc.Regenerate();

                MEPCurve main2 = SplitDummyAt(LiveCurve(dummyMain.Id), origin);
                MEPCurve br2 = SplitDummyAt(LiveCurve(dummyBr.Id), origin);
                dummyMain = LiveCurve(dummyMain.Id);
                dummyBr = LiveCurve(dummyBr.Id);
                if (main2 != null) dummyIds.Add(main2.Id);
                if (br2 != null) dummyIds.Add(br2.Id);
                if (dummyMain != null) DisconnectAllPhysical(dummyMain);
                if (main2 != null) DisconnectAllPhysical(main2);
                if (dummyBr != null) DisconnectAllPhysical(dummyBr);
                if (br2 != null) DisconnectAllPhysical(br2);
                _doc.Regenerate();

                if (dummyMain == null || main2 == null || dummyBr == null || br2 == null)
                {
                    foreach (ElementId id in dummyIds)
                        DeleteDummy(_doc, LiveCurve(id), protect);
                    return null;
                }

                ElementId idM1 = dummyMain.Id, idM2 = main2.Id, idB1 = dummyBr.Id, idB2 = br2.Id;
                var involved = new MEPCurve[] { dummyMain, main2, dummyBr, br2 };

                FamilyInstance cross;
                bool ok = TryNativeFitting(
                    () =>
                    {
                        Connector cM1 = LiveOpenNear(idM1, origin, 2.5);
                        Connector cM2 = LiveOpenNear(idM2, origin, 2.5);
                        Connector cB1 = LiveOpenNear(idB1, origin, 2.5);
                        Connector cB2 = LiveOpenNear(idB2, origin, 2.5);
                        if (cM1 == null || cM2 == null || cB1 == null || cB2 == null)
                            throw new InvalidOperationException("dummy connectors missing");
                        return _doc.Create.NewCrossFitting(cM1, cM2, cB1, cB2);
                    },
                    new List<XYZ>(),
                    involved,
                    out cross);

                foreach (ElementId id in dummyIds)
                    DeleteDummy(_doc, LiveCurve(id), protect);

                ElementId crossId = IsLive(cross) ? cross.Id : ElementId.InvalidElementId;
                cross = LiveFitting(crossId);
                if (!ok || !IsLive(cross)) return null;

                _doc.Regenerate();
                AttachRunsToCrossSockets(cross, runs, outward, origin);
                return LiveFitting(crossId);
            }
            catch
            {
                foreach (ElementId id in dummyIds)
                    DeleteDummy(_doc, LiveCurve(id), protect);
                return null;
            }
        }

        private FamilyInstance PlaceCrossFamilyFallback(
            MEPCurve[] runs, XYZ[] outward, (int a, int b, int c, int d) pair, XYZ origin)
        {
            FamilySymbol sym = FindCrossFamilySymbol(_doc);
            if (sym == null || runs == null || runs[0] == null || origin == null) return null;
            try
            {
                if (!sym.IsActive) sym.Activate();
                ElementId levelId = runs[0].ReferenceLevel?.Id ?? runs[0].LevelId;
                Level level = _doc.GetElement(levelId) as Level;
                FamilyInstance cross = level != null
                    ? _doc.Create.NewFamilyInstance(origin, sym, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural)
                    : _doc.Create.NewFamilyInstance(origin, sym, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                if (!IsLive(cross)) return null;
                _doc.Regenerate();

                XYZ mainAxis = outward != null && outward.Length > pair.a ? outward[pair.a] : XYZ.BasisX;
                XYZ brAxis = outward != null && outward.Length > pair.c ? outward[pair.c] : XYZ.BasisY;
                AlignTeeToSnaps(cross, SnapshotFarsForCross(runs, origin), origin);

                AttachRunsToCrossSockets(cross, runs, outward, origin);
                return LiveFitting(cross.Id);
            }
            catch { return null; }
        }

        private List<FarEndSnap> SnapshotFarsForCross(MEPCurve[] runs, XYZ origin)
        {
            var snaps = new List<FarEndSnap>();
            if (runs == null) return snaps;
            foreach (MEPCurve r in runs)
                snaps.Add(SnapshotFar(r, origin));
            return snaps;
        }

        /// <summary>
        /// Dummy-stub hack: NewCrossFitting runs only on four short free segments so
        /// live far ends never move. Stubs are deleted; real runs attach to leftover ports.
        /// </summary>
        private FamilyInstance PlaceCrossFromFourDummies(
            MEPCurve[] runs, XYZ[] outward, int[] order, XYZ origin)
        {
            if (runs == null || runs.Length != 4 || order == null || order.Length != 4)
                return null;

            var dummyIds = new List<ElementId>();
            var protect = new HashSet<ElementId>();
            foreach (MEPCurve r in runs)
            {
                if (r != null) protect.Add(r.Id);
            }

            XYZ[] starts = new XYZ[4];
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    XYZ dir = outward[i] != null && outward[i].GetLength() > 1e-9
                        ? outward[i].Normalize()
                        : XYZ.BasisX;
                    starts[i] = origin + dir.Multiply(0.10);
                    XYZ end = origin + dir.Multiply(2.0);
                    MEPCurve dummy = CreateDummyCurve(runs[i], starts[i], end);
                    if (dummy == null)
                    {
                        foreach (ElementId id in dummyIds)
                            DeleteDummy(_doc, LiveCurve(id), protect);
                        return null;
                    }
                    dummyIds.Add(dummy.Id);
                }

                _doc.Regenerate();

                var dummyCurves = dummyIds.Select(id => LiveCurve(id)).ToArray();
                FamilyInstance cross;
                bool ok = TryNativeFitting(
                    () =>
                    {
                        Connector[] cs = new Connector[4];
                        for (int k = 0; k < 4; k++)
                        {
                            int i = order[k];
                            MEPCurve d = LiveCurve(dummyIds[i]);
                            if (d == null) throw new InvalidOperationException("dummy missing");
                            cs[k] = FindOpenConnectorNear(d.ConnectorManager, starts[i], 1.5)
                                ?? FindClosestConnector(d.ConnectorManager, origin);
                            if (cs[k] == null) throw new InvalidOperationException("dummy connector missing");
                        }
                        return _doc.Create.NewCrossFitting(cs[0], cs[1], cs[2], cs[3]);
                    },
                    new List<XYZ>(),
                    dummyCurves,
                    out cross);

                foreach (ElementId id in dummyIds)
                    DeleteDummy(_doc, LiveCurve(id), protect);

                ElementId crossId = IsLive(cross) ? cross.Id : ElementId.InvalidElementId;
                cross = LiveFitting(crossId);
                if (!ok || !IsLive(cross)) return null;

                _doc.Regenerate();
                AttachRunsToCrossSockets(cross, runs, outward, origin);
                return LiveFitting(crossId);
            }
            catch
            {
                foreach (ElementId id in dummyIds)
                    DeleteDummy(_doc, LiveCurve(id), protect);
                return null;
            }
        }

        private void AttachRunsToCrossSockets(FamilyInstance cross, MEPCurve[] runs, XYZ[] outward, XYZ origin)
        {
            if (!IsLive(cross) || runs == null) return;
            var sockets = new List<Connector>();
            try
            {
                foreach (Connector c in cross.MEPModel.ConnectorManager.Connectors)
                {
                    if (c.ConnectorType != ConnectorType.Logical)
                        sockets.Add(c);
                }
            }
            catch { return; }

            var taken = new HashSet<int>();
            for (int i = 0; i < runs.Length && i < outward.Length; i++)
            {
                MEPCurve run = LiveCurve(runs[i].Id);
                if (run == null) continue;
                XYZ want = outward[i] ?? XYZ.BasisX;
                Connector best = null;
                int bestIdx = -1;
                double bestDot = -2;
                for (int s = 0; s < sockets.Count; s++)
                {
                    if (taken.Contains(s)) continue;
                    double d = SafeNormalize(sockets[s].CoordinateSystem.BasisZ).DotProduct(want);
                    if (d > bestDot)
                    {
                        bestDot = d;
                        best = sockets[s];
                        bestIdx = s;
                    }
                }
                if (bestIdx >= 0) taken.Add(bestIdx);
                Connector runNear = FindOpenOnRunNear(run, origin, 2.5)
                    ?? FindClosestConnector(run.ConnectorManager, origin);
                LogicalConnect(runNear, best);
            }
        }

        private FamilyInstance TryNativeFourPipeCross(
            ElementId mainA, ElementId mainB, ElementId brA, ElementId brB,
            XYZ origin, List<XYZ> anchoredPts, MEPCurve[] involved)
        {
            FamilyInstance cross;
            bool ok = TryNativeFitting(
                () =>
                {
                    Connector cA = LiveOpenNear(mainA, origin, 2.5);
                    Connector cB = LiveOpenNear(mainB, origin, 2.5);
                    Connector cC = LiveOpenNear(brA, origin, 2.5);
                    Connector cD = LiveOpenNear(brB, origin, 2.5);
                    if (cA == null || cB == null || cC == null || cD == null)
                        throw new InvalidOperationException("live connectors missing");
                    return _doc.Create.NewCrossFitting(cA, cB, cC, cD);
                },
                anchoredPts, involved, out cross);
            return ok && IsLive(cross) ? cross : null;
        }

        private FamilyInstance PlaceFourPipeCrossWithDummy(
            MEPCurve mainA, MEPCurve mainB, MEPCurve brA, MEPCurve brB,
            XYZ origin, XYZ farLeftover, bool p0FarLeftover, bool leftoverAnchored,
            List<XYZ> anchoredPts)
        {
            if (mainA == null || brA == null || brB == null) return null;
            Connector cHost = LiveOpenNear(mainA.Id, origin, 2.5);
            if (cHost == null) return null;

            XYZ dummyDir, dummyStart;
            try
            {
                dummyDir = SafeNormalize(cHost.CoordinateSystem.BasisZ);
                dummyStart = cHost.Origin;
            }
            catch { return null; }

            MEPCurve dummy = CreateDummyCurve(mainA, dummyStart, dummyStart + dummyDir.Multiply(2.0));
            if (dummy == null) return null;
            OrientDummyAsHostContinuation(mainA, dummyStart, dummy);
            ElementId dummyId = dummy.Id;
            ElementId hostId = mainA.Id;
            ElementId leftoverId = mainB != null ? mainB.Id : ElementId.InvalidElementId;
            ElementId brAId = brA.Id;
            ElementId brBId = brB.Id;
            var protect = new HashSet<ElementId> { hostId, leftoverId, brAId, brBId };

            FamilyInstance cross = null;
            bool ok = TryNativeFitting(
                () =>
                {
                    Connector h = LiveOpenNear(hostId, origin, 2.5);
                    Connector d = LiveOpenNear(dummyId, dummyStart, 2.5) ?? LiveOpenNear(dummyId, origin, 2.5);
                    Connector b1 = LiveOpenNear(brAId, origin, 2.5);
                    Connector b2 = LiveOpenNear(brBId, origin, 2.5);
                    if (h == null || d == null || b1 == null || b2 == null)
                        throw new InvalidOperationException("live connectors missing");
                    return _doc.Create.NewCrossFitting(h, d, b1, b2);
                },
                anchoredPts,
                new MEPCurve[] { mainA, mainB, brA, brB },
                out cross);

            DeleteDummy(_doc, LiveCurve(dummyId), protect);
            ElementId crossId = IsLive(cross) ? cross.Id : ElementId.InvalidElementId;
            cross = LiveFitting(crossId);
            if (!ok || !IsLive(cross)) return null;

            XYZ leftoverOut = origin;
            if (mainB != null && TryGetLine(mainB, out _, out _, out XYZ lp0, out XYZ lp1))
                leftoverOut = OutwardFromOrigin(lp0, lp1, origin);
            Connector leftover = OpenConnectorFacing(cross, leftoverOut) ?? FirstUnconnectedFittingConnector(cross);
            if (leftover != null && mainB != null)
            {
                if (leftoverAnchored)
                {
                    SacredTrimToSocket(mainB, farLeftover, p0FarLeftover, leftover.Origin);
                    _doc.Regenerate();
                }
                Connector cKeep = LiveOpenNear(leftoverId, leftover.Origin, 2.5)
                    ?? FindClosestConnector(mainB.ConnectorManager, leftover.Origin);
                LogicalConnect(cKeep, leftover);
            }

            return LiveFitting(crossId);
        }

        public MepOperationResult ConnectCross(FamilyInstance fitting, MEPCurve newCurve)
        {
            if (fitting == null || newCurve == null) return MepOperationResult.Fail("Fitting and Pipe must be provided.");
            if (fitting.MEPModel?.ConnectorManager == null) return MepOperationResult.Fail("Selected fitting is not MEP.");

            int connCount = fitting.MEPModel.ConnectorManager.Connectors.Size;
            if (connCount != 3 && connCount != 4)
                return MepOperationResult.Fail("Selected fitting must be a Tee (3 connectors) or Cross (4 connectors).");

            LocationPoint locPt = fitting.Location as LocationPoint;
            if (locPt == null) return MepOperationResult.Fail("Fitting has no location point.");
            XYZ origin = locPt.Point;

            if (!TryGetLine(newCurve, out LocationCurve lc, out Line line, out XYZ p0, out XYZ p1))
                return MepOperationResult.Fail("Selected run must have linear geometry.");

            XYZ farPt = PickFarEnd(p0, p1, origin, null);
            bool isP0Far = P0IsFar(p0, farPt);
            bool isAnchored = IsFarEndAnchored(newCurve, origin);
            XYZ axisFromFar = SafeNormalize(isP0Far ? (p1 - p0) : (p0 - p1));

            var connectedCurves = new List<Connector>();
            var existingRuns = new List<MEPCurve>();
            foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors)
            {
                if (!c.IsConnected) continue;
                foreach (Connector refC in c.AllRefs)
                {
                    if (refC.Owner.Id != fitting.Id && refC.Owner is MEPCurve mep)
                    {
                        connectedCurves.Add(refC);
                        existingRuns.Add(mep);
                    }
                }
            }

            if (connCount == 4 || (connCount == 3 && connectedCurves.Count < 3))
            {
                Connector openCrossConn = null;
                foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors)
                {
                    if (!c.IsConnected) { openCrossConn = c; break; }
                }
                if (openCrossConn == null) return MepOperationResult.Fail("Fitting has no open connectors.");

                using (Transaction t = new Transaction(_doc, "Connect to Fitting"))
                {
                    t.Start();
                    try
                    {
                        XYZ connOrigin = openCrossConn.Origin;
                        PrepareRunToJunction(newCurve, connOrigin, out farPt, out isP0Far, out isAnchored);
                        if (isAnchored)
                            SacredTrimToSocket(newCurve, farPt, isP0Far, connOrigin);

                        _doc.Regenerate();
                        Connector cNew = FindOpenOnRunNear(newCurve, connOrigin, 0.6);
                        LogicalConnect(cNew, openCrossConn);
                        t.Commit();
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, fitting.Id, "Piping", "Auto Tee (open port)"); } catch { }
                        return MepOperationResult.OK("Connected pipe to fitting successfully.", fitting);
                    }
                    catch (Exception ex)
                    {
                        t.RollBack();
                        return MepOperationResult.Fail(ex.Message);
                    }
                }
            }

            if (connectedCurves.Count != 3)
                return MepOperationResult.Fail($"Tee must have exactly 3 connected pipes to convert to a cross. Found {connectedCurves.Count}.");

            Connector main1, main2, branch1;
            XYZ d0 = connectedCurves[0].CoordinateSystem.BasisZ;
            XYZ d1v = connectedCurves[1].CoordinateSystem.BasisZ;
            XYZ d2v = connectedCurves[2].CoordinateSystem.BasisZ;
            double dot01 = d0.DotProduct(d1v);
            double dot12 = d1v.DotProduct(d2v);
            double dot02 = d0.DotProduct(d2v);
            if (dot01 < dot12 && dot01 < dot02) { main1 = connectedCurves[0]; main2 = connectedCurves[1]; branch1 = connectedCurves[2]; }
            else if (dot12 < dot01 && dot12 < dot02) { main1 = connectedCurves[1]; main2 = connectedCurves[2]; branch1 = connectedCurves[0]; }
            else { main1 = connectedCurves[0]; main2 = connectedCurves[2]; branch1 = connectedCurves[1]; }

            using (Transaction t = new Transaction(_doc, "Convert Tee to Cross"))
            {
                t.Start();
                try
                {
                    XYZ dBranch1 = branch1.CoordinateSystem.BasisZ;
                    XYZ oppositeDir = SafeNormalize(-dBranch1);

                    PrepareRunToJunction(newCurve, origin, out farPt, out isP0Far, out isAnchored);
                    bool anyExistAnc = existingRuns.Any(r => IsFarEndAnchored(r, origin));

                    _doc.Delete(fitting.Id);
                    _doc.Regenerate();

                    List<XYZ> anchoredPts = CollectAnchoredEndPoints(existingRuns.Concat(new[] { newCurve }).ToArray());
                    var involved = existingRuns.Concat(new[] { newCurve }).ToArray();

                    Connector cNew = FindOpenOnRunNear(newCurve, origin, 0.6);
                    Connector liveMain1 = FindOpenOnRunNear(main1.Owner as MEPCurve, origin, 0.8) ?? main1;
                    Connector liveMain2 = FindOpenOnRunNear(main2.Owner as MEPCurve, origin, 0.8) ?? main2;
                    Connector liveBranch1 = FindOpenOnRunNear(branch1.Owner as MEPCurve, origin, 0.8) ?? branch1;

                    FamilyInstance cross = null;
                    bool nativeOk = cNew != null && liveMain1 != null && liveMain2 != null && liveBranch1 != null
                        && TryNativeFitting(
                            () => _doc.Create.NewCrossFitting(liveMain1, liveMain2, liveBranch1, cNew),
                            anchoredPts, involved, out cross);

                    if (!nativeOk)
                    {
                        cNew = FindOpenOnRunNear(newCurve, origin, 0.6) ?? cNew;
                        cross = PlaceCrossWithDummy(
                            liveMain1, liveMain2, liveBranch1,
                            newCurve, cNew, origin, farPt, isP0Far, isAnchored,
                            oppositeDir, anchoredPts, involved);
                    }

                    if (cross != null)
                    {
                        TrimRunsToFittingSockets(cross, involved);
                        TriggerAutoTrimSafe(cross, isAnchored || anyExistAnc);
                    }

                    t.Commit();
                    if (cross != null)
                    {
                        try { Ai.AutoTrainingManager.Instance.RecordFittedElement(_doc, cross.Id, "Piping", "Auto Cross (Tee upgrade)"); } catch { }
                    }
                    return MepOperationResult.OK("Converted Tee to Cross and connected pipe.", cross);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    return MepOperationResult.Fail(ex.Message);
                }
            }
        }
        #endregion

        #region Auto (Union / Elbow / Tee / Cross)

        /// <summary>
        /// Picks Union, Reducer, Elbow, Tee, or Cross from the selection.
        /// Includes every Tee method (2-pipe, 3-pipe, Elbow+pipe) and every Cross
        /// method (4-pipe, Tee+pipe, open-port Cross+pipe).
        /// Parallel same-size → Union; parallel different size → Reducer.
        /// Does not change the dedicated operation tools.
        /// </summary>
        public MepOperationResult ConnectAuto(IList<MEPCurve> pipes)
        {
            if (pipes == null)
                return MepOperationResult.Fail("Please select pipes (or MEP runs).");
            return ConnectAuto(pipes.Cast<Element>().ToList());
        }

        public MepOperationResult ConnectAuto(IList<Element> picked)
        {
            if (picked == null)
                return MepOperationResult.Fail("Please select pipes and/or fittings.");

            var runs = picked.OfType<MEPCurve>().GroupBy(c => c.Id).Select(g => g.First()).ToList();
            var fittings = picked.OfType<FamilyInstance>()
                .Where(f => f.MEPModel?.ConnectorManager != null)
                .GroupBy(f => f.Id).Select(g => g.First()).ToList();

            FamilyInstance fit = fittings.FirstOrDefault(f =>
            {
                int n = f.MEPModel.ConnectorManager.Connectors.Size;
                return n >= 2 && n <= 4;
            });

            // Fitting + pipe: Elbow→Tee, Tee→Cross / open Tee, open Cross.
            if (fit != null && runs.Count >= 1)
            {
                int n = fit.MEPModel.ConnectorManager.Connectors.Size;
                if (n == 2)
                    return ConnectTee(fit, runs[0]);
                return ConnectCross(fit, runs[0]);
            }

            if (runs.Count >= 4)
                return ConnectCross(runs[0], runs[1], runs[2], runs[3]);
            if (runs.Count == 3)
                return ConnectTee(runs[0], runs[1], runs[2]);
            if (runs.Count == 2)
                return ConnectAutoTwo(runs[0], runs[1]);

            return MepOperationResult.Fail(
                "Auto needs two pipes, three pipes, four pipes, or a fitting (Elbow/Tee/Cross) plus a pipe.");
        }

        private MepOperationResult ConnectAutoTwo(MEPCurve a, MEPCurve b)
        {
            if (!TryGetLine(a, out _, out _, out XYZ a0, out XYZ a1) ||
                !TryGetLine(b, out _, out _, out XYZ b0, out XYZ b1))
            {
                return MepOperationResult.Fail("Selected elements must have linear geometries.");
            }

            XYZ da = SafeNormalize(a1 - a0);
            XYZ db = SafeNormalize(b1 - b0);
            double align = Math.Abs(da.DotProduct(db));

            // No elbow angle (parallel / collinear): same size → Union, different size → Reducer.
            if (align > 0.995)
            {
                if (SizesDifferForReducer(a, b))
                    return ConnectReducer(a, b);
                return ConnectUnion(a, b);
            }

            if (LineClosestApproach(a0, da, b0, db, out double t, out double s, out double gap, out _, out _))
            {
                double lenA = a0.DistanceTo(a1);
                double lenB = b0.DistanceTo(b1);
                bool aInterior = t > 0.15 && t < lenA - 0.15;
                bool bInterior = s > 0.15 && s < lenB - 0.15;
                const double maxTeeGapFt = 3.0;

                // One run hits the interior of the other → Tee (split the through-run).
                if (gap < maxTeeGapFt && aInterior && !bInterior)
                    return ConnectTee(a, b);
                if (gap < maxTeeGapFt && bInterior && !aInterior)
                    return ConnectTee(b, a);
                if (gap < maxTeeGapFt && aInterior && bInterior)
                    return lenA >= lenB ? ConnectTee(a, b) : ConnectTee(b, a);
            }

            return ConnectElbow(a, b);
        }

        private static bool SizesDifferForReducer(MEPCurve a, MEPCurve b)
        {
            double sa = CharacteristicSize(a);
            double sb = CharacteristicSize(b);
            if (sa < 1e-6 || sb < 1e-6) return false;
            double larger = Math.Max(sa, sb);
            return Math.Abs(sa - sb) > larger * 0.12;
        }

        private static double CharacteristicSize(MEPCurve curve)
        {
            double dia = GetCurveDiameter(curve);
            if (dia > 1e-6) return dia;
            try
            {
                var pW = curve.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)
                      ?? curve.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                var pH = curve.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)
                      ?? curve.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                double w = pW != null && pW.HasValue ? pW.AsDouble() : 0;
                double h = pH != null && pH.HasValue ? pH.AsDouble() : 0;
                if (w > 1e-6 && h > 1e-6) return Math.Max(w, h);
                if (w > 1e-6) return w;
                if (h > 1e-6) return h;
            }
            catch { }
            return 0;
        }

        #endregion
    }
}
