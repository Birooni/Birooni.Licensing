using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FamilyLoader.WPF;

namespace FamilyLoader
{
    [System.Reflection.Obfuscation(Exclude = true)]
    [Transaction(TransactionMode.Manual)]
    public class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var window = new SettingsWindow();
                
                // Set the owner window to Revit's main window handle
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
                message = $"Failed to open settings window:\n{ex.Message}";
                return Result.Failed;
            }
        }
    }
}
