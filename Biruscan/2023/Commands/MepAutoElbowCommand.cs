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
    public class MepCurveSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem is MEPCurve;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return true;
        }
    }

    /// <summary>
    /// Connects two selected MEP runs (Pipes, Ducts, Conduits, Cable Trays) with an Elbow.
    /// Auto-adjusts to a common plane/elevation and trims to the corner apex (like trim).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MepAutoElbowCommand : IExternalCommand
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
                        XYZ click1 = null;
                        XYZ click2 = null;

                        if (firstRun && hasPreSelection)
                        {
                            var mepCurves = selectionIds.Select(id => doc.GetElement(id)).OfType<MEPCurve>().ToList();
                            if (mepCurves.Count >= 2)
                            {
                                curve1 = mepCurves[0];
                                curve2 = mepCurves[1];
                            }
                            uidoc.Selection.SetElementIds(new System.Collections.Generic.List<ElementId>()); // Clear selection
                        }
                        else
                        {
                            var filter = new MepCurveSelectionFilter();
                            Reference ref1 = uidoc.Selection.PickObject(ObjectType.Element, filter, "Pick FIRST MEP element (Press ESC to finish)");
                            Reference ref2 = uidoc.Selection.PickObject(ObjectType.Element, filter, "Pick SECOND MEP element to connect with Elbow (Press ESC to finish)");

                            curve1 = doc.GetElement(ref1.ElementId) as MEPCurve;
                            curve2 = doc.GetElement(ref2.ElementId) as MEPCurve;
                            click1 = ref1.GlobalPoint;
                            click2 = ref2.GlobalPoint;
                        }

                        if (curve1 == null || curve2 == null)
                        {
                            if (firstRun && hasPreSelection) { firstRun = false; continue; }
                            break;
                        }

                        var service = new MepOperationService(doc);
                        var result = service.ConnectElbow(curve1, curve2, click1, click2);

                        if (result.Success)
                        {
                            count++;
                        }
                        else
                        {
                            if (firstRun && hasPreSelection) TaskDialog.Show("MEP Auto Elbow Error", result.Message);
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
                TaskDialog.Show("MEP Auto Elbow Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}