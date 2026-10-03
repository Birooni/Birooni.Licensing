using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Biruscan.Commands
{
    /// <summary>
    /// "Fit Generate MEP" — geometric Edgewise fit. Tiles the whole point cloud
    /// (clip optional), peels cylinders, builds the network, places MEP. No AI weights.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class FitGenerateMepCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            => AiGenerateMepCommand.ExecuteInternal(commandData, ref message, AiPipelineMode.Fit);
    }
}
