using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FamilyLoader.UI;

namespace FamilyLoader
{
    [System.Reflection.Obfuscation(Exclude = true)]
    [Transaction(TransactionMode.Manual)]
    public class LicenseCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var window = new LicenseDialog();
                IntPtr revitWindowHandle = commandData.Application.MainWindowHandle;
                if (revitWindowHandle != IntPtr.Zero)
                {
                    var helper = new WindowInteropHelper(window);
                    helper.Owner = revitWindowHandle;
                }

                window.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Failed to open license dialog:\n{ex.Message}";
                return Result.Failed;
            }
        }
    }
}
