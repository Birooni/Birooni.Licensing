using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using Biruscan.Services;

namespace Biruscan.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SliceBackwardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                ClippingService clipService = new ClippingService(uidoc.Document);
                clipService.StepSlice(false);
                try { uidoc.Document.Regenerate(); } catch { }
                try { uidoc.RefreshActiveView(); } catch { }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Slice Backward", ex.Message);
                return Result.Failed;
            }
        }
    }
}
