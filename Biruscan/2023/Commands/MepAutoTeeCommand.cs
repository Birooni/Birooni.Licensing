using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Biruscan.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Biruscan.Commands
{
    public class MepCurveOrFittingSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem is MEPCurve) return true;
            if (elem is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
            {
                int c = fi.MEPModel.ConnectorManager.Connectors.Size;
                return c >= 2 && c <= 4;
            }
            return false;
        }

        public bool AllowReference(Reference reference, XYZ position) => true;
    }

    /// <summary>
    /// Connects a branch MEP run to a main MEP run with a Tee or Tap fitting.
    /// Also supports selecting an Elbow or Tee to upgrade to a Tee or Cross.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MepAutoTeeCommand : IExternalCommand
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool IsControlPressed()
        {
            const int vkControl = 0x11;
            return (GetAsyncKeyState(vkControl) & 0x8000) != 0;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                var selectionIds = uidoc.Selection.GetElementIds();
                bool hasPreSelection = selectionIds != null && selectionIds.Count >= 2;
                bool firstRun = true;
                int count = 0;

                while (true)
                {
                    try
                    {
                        Element mainElem = null;
                        MEPCurve branchCurve = null;

                        if (firstRun && hasPreSelection)
                        {
                            var curves = selectionIds.Select(id => doc.GetElement(id)).OfType<MEPCurve>().ToList();
                            var fittings = selectionIds.Select(id => doc.GetElement(id)).OfType<FamilyInstance>().Where(f => f.MEPModel?.ConnectorManager != null).ToList();
                            uidoc.Selection.SetElementIds(new System.Collections.Generic.List<ElementId>()); // Clear selection

                            // Three selected pipes → new 3-pipe Tee. Fitting+pipe and two-pipe are unchanged.
                            if (fittings.Count == 0 && curves.Count >= 3)
                            {
                                var service3 = new MepOperationService(doc);
                                MepOperationResult three = service3.ConnectTee(curves[0], curves[1], curves[2]);
                                if (three.Success)
                                    count++;
                                else
                                    TaskDialog.Show("MEP Auto Tee Error", three.Message);
                                break;
                            }

                            if (fittings.Count > 0 && curves.Count > 0)
                            {
                                mainElem = fittings[0];
                                branchCurve = curves[0];
                            }
                            else if (curves.Count >= 2)
                            {
                                mainElem = curves[0];
                                branchCurve = curves[1];
                            }
                        }
                        else
                        {
                            var filter = new MepCurveOrFittingSelectionFilter();
                            var pipeFilter = new MepCurveSelectionFilter();

                            // Draw-select is the first action (no Ctrl). Click two corners of a window.
                            // ESC falls back to click-to-pick (two-pipe / elbow+pipe).
                            try
                            {
                                IList<Element> boxed = uidoc.Selection.PickElementsByRectangle(filter,
                                    "Draw a window: click first corner, then opposite corner, around 2 or 3 pipes (ESC = pick one by one)");
                                if (TryRunTeeFromPicked(doc, boxed, out MepOperationResult boxedResult))
                                {
                                    if (boxedResult.Success)
                                        count++;
                                    else
                                        TaskDialog.Show("MEP Auto Tee Error", boxedResult.Message);
                                    firstRun = false;
                                    continue;
                                }

                                // Window caught a single run/fitting — pick the other one.
                                if (boxed != null)
                                {
                                    var oneFit = boxed.OfType<FamilyInstance>().FirstOrDefault(f => f.MEPModel?.ConnectorManager != null);
                                    var onePipe = boxed.OfType<MEPCurve>().FirstOrDefault();
                                    if (oneFit != null) mainElem = oneFit;
                                    else if (onePipe != null) mainElem = onePipe;
                                }
                            }
                            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                            {
                                // ESC → click pipes one by one
                            }

                            if (mainElem == null)
                            {
                                Reference refMain = uidoc.Selection.PickObject(ObjectType.Element, filter,
                                    "Pick MAIN MEP run OR existing Elbow/Tee (hold Ctrl to pick a third pipe)");
                                mainElem = doc.GetElement(refMain.ElementId);
                            }

                            if (branchCurve == null)
                            {
                                Reference refBranch = uidoc.Selection.PickObject(ObjectType.Element, pipeFilter,
                                    "Pick BRANCH MEP run (hold Ctrl to pick a third pipe, ESC to finish)");
                                branchCurve = doc.GetElement(refBranch.ElementId) as MEPCurve;
                            }

                            // Two pipes + Ctrl still down → pick a third. Fitting+pipe and two-pipe stay as they are.
                            if (mainElem is MEPCurve mainPipe && branchCurve != null
                                && mainPipe.Id != branchCurve.Id && IsControlPressed())
                            {
                                try
                                {
                                    Reference refThird = uidoc.Selection.PickObject(ObjectType.Element, pipeFilter,
                                        "Pick THIRD pipe for 3-pipe Tee (ESC = Tee the two pipes)");
                                    MEPCurve third = doc.GetElement(refThird.ElementId) as MEPCurve;
                                    if (third != null && third.Id != mainPipe.Id && third.Id != branchCurve.Id)
                                    {
                                        var service3 = new MepOperationService(doc);
                                        MepOperationResult three = service3.ConnectTee(mainPipe, branchCurve, third);
                                        if (three.Success)
                                            count++;
                                        else
                                            TaskDialog.Show("MEP Auto Tee Error", three.Message);
                                        firstRun = false;
                                        continue;
                                    }
                                }
                                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                                {
                                    // ESC on the third pick → existing two-pipe Tee
                                }
                            }
                        }

                        if (mainElem == null || branchCurve == null)
                        {
                            if (firstRun && hasPreSelection) { firstRun = false; continue; }
                            break;
                        }

                        var service = new MepOperationService(doc);
                        MepOperationResult result;

                        if (mainElem is FamilyInstance fi)
                        {
                            int c = fi.MEPModel.ConnectorManager.Connectors.Size;
                            if (c == 2)
                            {
                                result = service.ConnectTee(fi, branchCurve);
                            }
                            else if (c == 3)
                            {
                                result = service.ConnectCross(fi, branchCurve); // ConnectCross handles open-port Tees
                            }
                            else
                            {
                                result = MepOperationResult.Fail("Selected fitting must be an Elbow (to upgrade) or a Tee with an open port.");
                            }
                        }
                        else if (mainElem is MEPCurve mainCurve)
                        {
                            result = service.ConnectTee(mainCurve, branchCurve);
                        }
                        else
                        {
                            result = MepOperationResult.Fail("Invalid main element selected.");
                        }

                        if (result.Success)
                        {
                            count++;
                        }
                        else
                        {
                            TaskDialog.Show("MEP Auto Tee Error", result.Message);
                        }

                        if (firstRun && hasPreSelection) break;
                        firstRun = false;
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        break;
                    }
                }

                return count > 0 || hasPreSelection ? Result.Succeeded : Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("MEP Auto Tee Error", ex.Message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// Runs Tee from a window/multi pick: 3 pipes, 2 pipes, or fitting + pipe.
        /// Returns false if the set is not enough to run (caller continues picking).
        /// </summary>
        private static bool TryRunTeeFromPicked(Document doc, IList<Element> picked, out MepOperationResult result)
        {
            result = null;
            if (picked == null || picked.Count == 0)
                return false;

            var curves = picked.OfType<MEPCurve>().GroupBy(c => c.Id).Select(g => g.First()).ToList();
            var fittings = picked.OfType<FamilyInstance>()
                .Where(f => f.MEPModel?.ConnectorManager != null)
                .GroupBy(f => f.Id).Select(g => g.First()).ToList();

            var service = new MepOperationService(doc);

            if (fittings.Count > 0 && curves.Count > 0)
            {
                FamilyInstance fi = fittings[0];
                int n = fi.MEPModel.ConnectorManager.Connectors.Size;
                if (n == 2)
                    result = service.ConnectTee(fi, curves[0]);
                else if (n == 3)
                    result = service.ConnectCross(fi, curves[0]);
                else
                    result = MepOperationResult.Fail("Selected fitting must be an Elbow (to upgrade) or a Tee with an open port.");
                return true;
            }

            if (curves.Count >= 3)
            {
                result = service.ConnectTee(curves[0], curves[1], curves[2]);
                return true;
            }

            if (curves.Count == 2)
            {
                result = service.ConnectTee(curves[0], curves[1]);
                return true;
            }

            return false;
        }
    }
}