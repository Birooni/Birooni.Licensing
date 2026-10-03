using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace Biruscan.Commands
{
    public class PointCloudSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem is PointCloudInstance;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return true;
        }
    }
}
