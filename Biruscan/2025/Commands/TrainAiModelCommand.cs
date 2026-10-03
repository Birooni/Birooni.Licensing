using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using Biruscan.AI.DeepLearning;
using Biruscan.Services.Ai;
using Biruscan.UI.Ai;

namespace Biruscan.Commands
{
    /// <summary>
    /// Train AI: capture the current ROI + visible/selected MEP as ground truth
    /// and fine-tune the in-process C# neural weights.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class TrainAiModelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null)
            {
                TaskDialog.Show("Train AI", "No active document.");
                return Result.Cancelled;
            }

            try
            {
                var window = new TrainAiModelWindow(doc);
                try { new System.Windows.Interop.WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle; }
                catch { }
                if (window.ShowDialog() != true || !window.CaptureRequested)
                    return Result.Cancelled;

                var collector = new TrainingDataCollector();
                BirooniTrainSampleRequest sample = collector.Collect(doc, window.SelectedService);
                if (sample == null)
                {
                    TaskDialog.Show("Train AI",
                        "Could not build a training sample.\n\n" +
                        "Make sure the point cloud is loaded and the MEP elements you want as ground " +
                        "truth are visible (or selected) in the active view.");
                    return Result.Cancelled;
                }

                InProcessTrainResult local = InProcessAiTrainer.TrainFromSample(sample, window.TriggerTrainingAfterCapture, epochs: 6);

                TaskDialog.Show("Train AI",
                    $"{local.Message}\n\n" +
                    $"Sample ID : {local.SampleId}\n" +
                    $"Points    : {local.NPoints}\n" +
                    $"Elements  : {local.NElements}\n" +
                    $"Clusters  : {local.LabeledClusters}\n" +
                    $"{NeuralWeightsDataset.StatusSummary()}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.ToString();
                TaskDialog.Show("Train AI — Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
