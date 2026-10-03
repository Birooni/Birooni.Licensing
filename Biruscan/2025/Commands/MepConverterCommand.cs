using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Biruscan.Services;
using Biruscan.UI.Views;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Biruscan.Commands
{
    /// <summary>
    /// Selection filter for MEP elements based on conversion mode.
    /// </summary>
    public class MepNetworkSelectionFilter : ISelectionFilter
    {
        private readonly MepConversionMode _mode;
        private readonly MepConverterService _service;

        private static readonly ElementId PipeFittingCatId = new ElementId(BuiltInCategory.OST_PipeFitting);
        private static readonly ElementId DuctFittingCatId = new ElementId(BuiltInCategory.OST_DuctFitting);
        private static readonly ElementId ConduitFittingCatId = new ElementId(BuiltInCategory.OST_ConduitFitting);

        public MepNetworkSelectionFilter(Document doc, MepConversionMode mode)
        {
            _mode = mode;
            _service = new MepConverterService(doc);
        }

        public bool AllowElement(Element elem)
        {
            if (elem is MEPCurve curve)
            {
                return _service.IsCompatibleCurve(curve, _mode);
            }
            if (elem is FamilyInstance fi && fi.Category != null)
            {
                var catId = fi.Category.Id;
                return catId == PipeFittingCatId ||
                       catId == DuctFittingCatId ||
                       catId == ConduitFittingCatId;
            }
            return false;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return true;
        }
    }

    /// <summary>
    /// Command to convert full MEP networks (runs and fittings) between
    /// Round Duct, Pipe, and Conduit.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MepConverterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                var selectionIds = uidoc.Selection.GetElementIds();

                // Open MepConverterWindow
                var dlg = new MepConverterWindow(doc, selectionIds);
                try
                {
                    new System.Windows.Interop.WindowInteropHelper(dlg).Owner = commandData.Application.MainWindowHandle;
                }
                catch { }

                if (dlg.ShowDialog() != true)
                {
                    return Result.Cancelled;
                }

                var options = dlg.Options;
                var scope = dlg.Scope;
                var service = new MepConverterService(doc);
                List<ElementId> targetIds = new List<ElementId>();

                switch (scope)
                {
                    case ConversionScope.Pick:
                        {
                            var filter = new MepNetworkSelectionFilter(doc, options.Mode);
                            IList<Reference> pickedRefs;
                            try
                            {
                                pickedRefs = uidoc.Selection.PickObjects(
                                    ObjectType.Element,
                                    filter,
                                    "Select MEP run(s) or connected network to convert. Press Finish when done.");
                            }
                            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                            {
                                return Result.Cancelled;
                            }

                            if (pickedRefs != null && pickedRefs.Count > 0)
                            {
                                targetIds = pickedRefs.Select(r => r.ElementId).ToList();
                            }
                        }
                        break;

                    case ConversionScope.View:
                        {
                            Type targetClass = typeof(MEPCurve);
                            switch (options.Mode)
                            {
                                case MepConversionMode.RoundDuctToPipe:
                                    targetClass = typeof(Duct);
                                    break;
                                case MepConversionMode.PipeToConduit:
                                case MepConversionMode.PipeToRoundDuct:
                                    targetClass = typeof(Pipe);
                                    break;
                                case MepConversionMode.ConduitToPipe:
                                    targetClass = typeof(Conduit);
                                    break;
                            }

                            var viewElems = new FilteredElementCollector(doc, doc.ActiveView.Id)
                                .OfClass(targetClass)
                                .Cast<MEPCurve>()
                                .Where(c => service.IsCompatibleCurve(c, options.Mode))
                                .Select(c => c.Id)
                                .ToList();

                            targetIds = viewElems;
                        }
                        break;

                    case ConversionScope.Selected:
                    default:
                        {
                            if (selectionIds != null && selectionIds.Count > 0)
                            {
                                targetIds = selectionIds.ToList();
                            }
                            else
                            {
                                // Fallback to interactive pick if nothing was selected
                                var filter = new MepNetworkSelectionFilter(doc, options.Mode);
                                IList<Reference> pickedRefs;
                                try
                                {
                                    pickedRefs = uidoc.Selection.PickObjects(
                                        ObjectType.Element,
                                        filter,
                                        "Select MEP run(s) or connected network to convert. Press Finish when done.");
                                }
                                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                                {
                                    return Result.Cancelled;
                                }

                                if (pickedRefs != null && pickedRefs.Count > 0)
                                {
                                    targetIds = pickedRefs.Select(r => r.ElementId).ToList();
                                }
                            }
                        }
                        break;
                }

                if (targetIds.Count == 0)
                {
                    TaskDialog.Show("MEP Network Converter", "No compatible elements found to convert.");
                    return Result.Cancelled;
                }

                // Execute conversion
                var result = service.ConvertNetwork(targetIds, options);

                if (result.Success)
                {
                    TaskDialog.Show("MEP Network Converter - Success", result.Summary);
                    return Result.Succeeded;
                }
                else
                {
                    TaskDialog.Show("MEP Network Converter - Incomplete", result.Summary);
                    return Result.Failed;
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("MEP Converter Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
