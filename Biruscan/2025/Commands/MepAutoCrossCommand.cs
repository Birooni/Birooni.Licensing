using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Biruscan.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Biruscan.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class MepAutoCrossCommand : IExternalCommand
    {
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
                        FamilyInstance fitting = null;
                        MEPCurve newCurve = null;

                        if (firstRun && hasPreSelection)
                        {
                            var curves = selectionIds.Select(id => doc.GetElement(id)).OfType<MEPCurve>().ToList();
                            var fittings = selectionIds.Select(id => doc.GetElement(id)).OfType<FamilyInstance>().Where(f =>
                            {
                                if (f.MEPModel == null || f.MEPModel.ConnectorManager == null) return false;
                                int c = f.MEPModel.ConnectorManager.Connectors.Size;
                                return c == 3 || c == 4;
                            }).ToList();
                            uidoc.Selection.SetElementIds(new List<ElementId>());

                            // Four selected pipes → new 4-pipe Cross. Fitting+pipe is unchanged.
                            if (fittings.Count == 0 && curves.Count >= 4)
                            {
                                var service4 = new MepOperationService(doc);
                                MepOperationResult four = service4.ConnectCross(curves[0], curves[1], curves[2], curves[3]);
                                if (four.Success)
                                    count++;
                                else
                                    TaskDialog.Show("MEP Auto Cross Error", four.Message);
                                break;
                            }

                            fitting = fittings.FirstOrDefault();
                            newCurve = curves.FirstOrDefault();
                        }
                        else
                        {
                            var fitOrPipe = new MepCurveOrFittingSelectionFilter();
                            var pipeFilter = new MepCurveSelectionFilter();

                            try
                            {
                                IList<Element> boxed = uidoc.Selection.PickElementsByRectangle(fitOrPipe,
                                    "Draw a window: click first corner, then opposite corner, around 4 pipes (ESC = pick Tee/Cross then pipe)");
                                if (TryRunCrossFromPicked(doc, boxed, out MepOperationResult boxedResult))
                                {
                                    if (boxedResult.Success)
                                        count++;
                                    else
                                        TaskDialog.Show("MEP Auto Cross Error", boxedResult.Message);
                                    firstRun = false;
                                    continue;
                                }
                            }
                            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                            {
                                // ESC → existing pick Tee/Cross then pipe
                            }

                            Reference refFitting = uidoc.Selection.PickObject(ObjectType.Element, fitOrPipe,
                                "Pick a Tee or Cross fitting, or a pipe to start 4-pipe Cross (Press ESC to finish)");
                            Element first = doc.GetElement(refFitting.ElementId);

                            if (first is MEPCurve pipe0)
                            {
                                var pipes = new List<MEPCurve> { pipe0 };
                                string[] prompts =
                                {
                                    "Pick SECOND pipe for 4-pipe Cross",
                                    "Pick THIRD pipe for 4-pipe Cross",
                                    "Pick FOURTH pipe for 4-pipe Cross"
                                };
                                bool badPick = false;
                                for (int i = 0; i < 3; i++)
                                {
                                    Reference r = uidoc.Selection.PickObject(ObjectType.Element, pipeFilter, prompts[i]);
                                    MEPCurve p = doc.GetElement(r.ElementId) as MEPCurve;
                                    if (p == null || pipes.Any(x => x.Id == p.Id))
                                    {
                                        TaskDialog.Show("MEP Auto Cross Error", "Pick four distinct pipes.");
                                        badPick = true;
                                        break;
                                    }
                                    pipes.Add(p);
                                }
                                if (!badPick && pipes.Count == 4)
                                {
                                    var service4 = new MepOperationService(doc);
                                    MepOperationResult four = service4.ConnectCross(pipes[0], pipes[1], pipes[2], pipes[3]);
                                    if (four.Success)
                                        count++;
                                    else
                                        TaskDialog.Show("MEP Auto Cross Error", four.Message);
                                }
                                firstRun = false;
                                continue;
                            }

                            fitting = first as FamilyInstance;
                            Reference refPipe = uidoc.Selection.PickObject(ObjectType.Element,
                                "Pick a Pipe/Duct to connect to it (Press ESC to finish)");
                            newCurve = doc.GetElement(refPipe.ElementId) as MEPCurve;
                        }

                        if (fitting == null || newCurve == null)
                        {
                            if (firstRun && hasPreSelection) { firstRun = false; continue; }
                            break;
                        }

                        var service = new MepOperationService(doc);
                        var result = service.ConnectCross(fitting, newCurve);

                        if (result.Success)
                        {
                            count++;
                        }
                        else
                        {
                            if (firstRun && hasPreSelection) TaskDialog.Show("MEP Auto Cross Error", result.Message);
                        }

                        if (firstRun && hasPreSelection)
                        {
                            break;
                        }
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
                TaskDialog.Show("MEP Auto Cross Error", ex.Message);
                return Result.Failed;
            }
        }

        private static bool TryRunCrossFromPicked(Document doc, IList<Element> picked, out MepOperationResult result)
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
                result = service.ConnectCross(fittings[0], curves[0]);
                return true;
            }

            if (curves.Count >= 4)
            {
                result = service.ConnectCross(curves[0], curves[1], curves[2], curves[3]);
                return true;
            }

            return false;
        }
    }
}
