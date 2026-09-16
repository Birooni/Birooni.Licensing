using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BiruBox.Core;

namespace BiruBox.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ToggleSectionBoxCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!LicensingManager.EnsureLicense())
        {
            return Result.Cancelled;
        }

        UIDocument? uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc == null)
        {
            message = "No active document.";
            return Result.Cancelled;
        }

        if (!SectionBoxService.ToggleActive3DSectionBox(uidoc, out string msg))
        {
            TaskDialog.Show("BiruBox", msg);
            message = msg;
            return Result.Cancelled;
        }

        return Result.Succeeded;
    }
}
