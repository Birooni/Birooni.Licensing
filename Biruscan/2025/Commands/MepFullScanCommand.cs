using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using Biruscan.Services.Ai;
using Biruscan.UI.Ai;

namespace Biruscan.Commands
{
    /// <summary>
    /// EdgeWise-style full-cloud MEP extract with no manual clipping.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MepFullScanCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                var pcService = new Biruscan.Services.PointCloudService(doc);
                if (pcService.GetFirstPointCloudInstance() == null)
                {
                    TaskDialog.Show("Full Scan AI", "No point cloud found. Load a point cloud first.");
                    return Result.Cancelled;
                }

                var dlg = new AiGenerateMepWindow(doc, MepDialogMode.AiGenerate);
                try { new System.Windows.Interop.WindowInteropHelper(dlg).Owner = commandData.Application.MainWindowHandle; }
                catch { }

                if (dlg.ShowDialog() != true)
                    return Result.Cancelled;

                if (dlg.SelectedPrimaryTypeId == ElementId.InvalidElementId)
                {
                    TaskDialog.Show("Full Scan AI", $"No '{dlg.SelectedService}' types exist. Load a family first.");
                    return Result.Cancelled;
                }

                string pipeline = dlg.SelectedPipelineMode == AiPipelineMode.TrainedPureDl ? "trained_pure"
                    : dlg.SelectedPipelineMode == AiPipelineMode.TrainedHybrid ? "trained"
                    : dlg.SelectedPipelineMode == AiPipelineMode.Combined ? "combined"
                    : dlg.SelectedPipelineMode == AiPipelineMode.Fit ? "fit"
                    : "standard";

                var edge = new EdgewiseMepExtractor().Extract(doc, dlg.SelectedService, dlg.DiameterOverrideMm, pipeline);
                if (edge.Elements.Count == 0)
                {
                    TaskDialog.Show("Full Scan AI",
                        $"No {dlg.SelectedService} runs found in the entire point cloud.\n\n{edge.Note}");
                    return Result.Cancelled;
                }

                List<XYZ> roiWorld = new List<XYZ>();
                int placed = AiGenerateMepCommand.PlaceElements(doc, dlg.SelectedService, edge.Elements, XYZ.Zero,
                    dlg.SelectedPrimaryTypeId, dlg.SelectedSystemTypeId, dlg.SelectedLevelId,
                    dlg.DiameterOverrideMm, dlg.GenerateWithoutFittings, roiWorld, out string placeDiag);

                int segs = edge.Elements.Count(e => e.Start != null && e.End != null && e.Start.Length == 3 && e.End.Length == 3);
                TaskDialog.Show("Full Scan AI",
                    $"{edge.Note}\nService: {dlg.SelectedService}\n" +
                    $"Tiles {edge.TilesWithHits}/{edge.TilesProcessed} · points {edge.RawPoints}\n" +
                    $"Detected {segs} run(s); placed {placed}.\n\n{placeDiag}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Full Scan AI — Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
