using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BiruBox.Core;
using BiruBox.UI;

namespace BiruBox.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class LicenseCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var dialog = new LicenseDialog();
        dialog.ShowDialog();
        return Result.Succeeded;
    }
}
