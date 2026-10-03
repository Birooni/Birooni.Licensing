using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.Attributes;
using Biruscan.Services;

namespace Biruscan.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class MepAutoUnifyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

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
                        var mepCurves = new List<MEPCurve>();

                        if (firstRun && hasPreSelection)
                        {
                            foreach (var id in selectionIds)
                            {
                                if (doc.GetElement(id) is MEPCurve mepCurve)
                                {
                                    mepCurves.Add(mepCurve);
                                }
                            }
                            uidoc.Selection.SetElementIds(new List<ElementId>());
                        }
                        else
                        {
                            Reference ref1 = uidoc.Selection.PickObject(ObjectType.Element, new MepCurveSelectionFilter(), "Select first MEP curve (Pipe/Duct) to Unify (Press ESC to finish)");
                            mepCurves.Add(doc.GetElement(ref1) as MEPCurve);

                            Reference ref2 = uidoc.Selection.PickObject(ObjectType.Element, new MepCurveSelectionFilter(), "Select second MEP curve (Pipe/Duct) to Unify (Press ESC to finish)");
                            mepCurves.Add(doc.GetElement(ref2) as MEPCurve);
                        }

                        if (mepCurves.Count != 2)
                        {
                            if (firstRun && hasPreSelection) { firstRun = false; continue; }
                            break;
                        }

                        var service = new MepOperationService(doc);
                        var result = service.ConnectUnify(mepCurves[0], mepCurves[1]);

                        if (result.Success)
                        {
                            count++;
                        }
                        else
                        {
                            if (firstRun && hasPreSelection) TaskDialog.Show("MEP Auto Unify Error", result.Message);
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
                TaskDialog.Show("MEP Auto Unify Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
