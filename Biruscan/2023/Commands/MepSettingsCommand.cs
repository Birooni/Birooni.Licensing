using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Biruscan.UI.Views;
using System;
using System.Windows.Interop;

namespace Biruscan.Commands
{
    /// <summary>
    /// Opens the MEP Routing and Pull (Bloom) Settings Window.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MepSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                var window = new MepSettingsWindow(doc);

                IntPtr revitHandle = commandData.Application.MainWindowHandle;
                if (revitHandle != IntPtr.Zero)
                {
                    new WindowInteropHelper(window) { Owner = revitHandle };
                }

                window.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("MEP Settings Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
