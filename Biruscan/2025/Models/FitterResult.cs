using Autodesk.Revit.DB;
using System.Collections.Generic;

namespace Biruscan.Models
{
    /// <summary>
    /// Result of a fitter operation — contains the created Revit element
    /// and metadata about the fitting process.
    /// </summary>
    public class FitterResult
    {
        public bool Success { get; set; }
        public string ElementType { get; set; }
        public ElementId CreatedElementId { get; set; }
        public string Message { get; set; }
        public List<XYZ> UsedPoints { get; set; }
        public XYZ FittedDirection { get; set; }
        public double FittedDimension { get; set; }

        public FitterResult()
        {
            Success = false;
            UsedPoints = new List<XYZ>();
            Message = "";
        }

        public static FitterResult OK(ElementId id, string type, string msg = "")
        {
            return new FitterResult
            {
                Success = true,
                CreatedElementId = id,
                ElementType = type,
                Message = msg
            };
        }

        public static FitterResult Fail(string msg)
        {
            return new FitterResult
            {
                Success = false,
                Message = msg
            };
        }
    }
}
