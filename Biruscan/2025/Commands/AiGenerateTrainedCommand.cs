using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Biruscan.Commands
{
    /// <summary>
    /// "Generate AI (Trained)" — runs a trained model (Hybrid PointNet++ → RANSAC, by
    /// default) on the current ROI to create MEP elements. Ported from Birooni.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AiGenerateTrainedCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            => AiGenerateMepCommand.ExecuteInternal(commandData, ref message, AiPipelineMode.TrainedHybrid);
    }
}
