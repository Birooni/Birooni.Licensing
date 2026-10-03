using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Windows.Interop;
using Biruscan.Services;
using Biruscan.UI.Views;

namespace Biruscan.Commands
{
    /// <summary>
    /// Command to open the Clipping Manager dialog for managing point cloud cuts.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ClippingManagerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                PointCloudService pcService = new PointCloudService(doc);
                if (pcService.GetFirstPointCloudInstance() == null)
                {
                    TaskDialog.Show("Clipping Manager", "No point cloud found.\nPlease load a point cloud first.");
                    return Result.Cancelled;
                }

                if (ClippingManagerWindow.Instance != null)
                {
                    ClippingManagerWindow.Instance.Activate();
                    return Result.Succeeded;
                }

                Biruscan.UI.RevitEventHandler handler = new Biruscan.UI.RevitEventHandler();
                ExternalEvent exEvent = ExternalEvent.Create(handler);

                ClippingService clipService = new ClippingService(doc);
                ClippingManagerWindow window = new ClippingManagerWindow(doc, clipService, pcService, exEvent, handler, commandData.Application);

                // Make it modeless — Revit stays fully interactive while it is open
                WindowInteropHelper helper = new WindowInteropHelper(window);
                helper.Owner = commandData.Application.MainWindowHandle;
                window.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Clipping Manager Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
