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
    /// <summary>
    /// Unifies two selected MEP runs (Pipes, Ducts, Conduits, Cable Trays) with a Union or Coupling fitting.
    /// Auto-aligns meeting endpoints, maintains maximum possible 3D angle and slope, and establishes full MEP system connection (Tab).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MepAutoUnionCommand : IExternalCommand
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
                        MEPCurve curve1 = null;
                        MEPCurve curve2 = null;

                        if (firstRun && hasPreSelection)
                        {
                            var mepCurves = selectionIds.Select(id => doc.GetElement(id)).OfType<MEPCurve>().ToList();
                            if (mepCurves.Count >= 2)
                            {
                                curve1 = mepCurves[0];
                                curve2 = mepCurves[1];
                            }
                            uidoc.Selection.SetElementIds(new System.Collections.Generic.List<ElementId>());
                        }
                        else
                        {
                            var filter = new MepCurveSelectionFilter();
                            Reference ref1 = uidoc.Selection.PickObject(ObjectType.Element, filter, "Pick FIRST pipe (or MEP element) (Press ESC to finish)");
                            Reference ref2 = uidoc.Selection.PickObject(ObjectType.Element, filter, "Pick SECOND pipe to connect with Union/Coupling (Press ESC to finish)");

                            curve1 = doc.GetElement(ref1.ElementId) as MEPCurve;
                            curve2 = doc.GetElement(ref2.ElementId) as MEPCurve;
                        }

                        if (curve1 == null || curve2 == null)
                        {
                            if (firstRun && hasPreSelection) { firstRun = false; continue; }
                            break;
                        }

                        var service = new MepOperationService(doc);
                        var result = service.ConnectUnion(curve1, curve2);

                        if (result.Success)
                        {
                            count++;
                        }
                        else
                        {
                            if (firstRun && hasPreSelection) TaskDialog.Show("MEP Auto Union Error", result.Message);
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
                TaskDialog.Show("MEP Auto Union Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
