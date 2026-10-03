using System;
using System.Collections.Generic;

namespace Biruscan.AI.Contracts
{
    public class BirooniAiRequest
    {
        public string Service { get; set; } = "piping";
        public string Units { get; set; } = "feet";
        public double? DiameterOverrideMm { get; set; }
        public bool InsulationPresent { get; set; }
        /// <summary>standard | trained | trained_pure | combined</summary>
        public string Pipeline { get; set; } = "standard";
        public List<double[]> Points { get; set; } = new List<double[]>();
    }

    public class BirooniAiResponse
    {
        public string Status { get; set; } = "ok";
        public string Service { get; set; } = "piping";
        public string Message { get; set; } = "";
        public List<BirooniAiElement> Elements { get; set; } = new List<BirooniAiElement>();
    }

    public class BirooniAiElement
    {
        public string Type { get; set; } = "pipe"; // pipe, duct, cable_tray, conduit, fitting, elbow, tee
        public double[] Start { get; set; }
        public double[] End { get; set; }
        public double DiameterMm { get; set; } = 50.0;
        public double WidthMm { get; set; } = 300.0;
        public double HeightMm { get; set; } = 200.0;
        public double Confidence { get; set; } = 1.0;
        public string FittingType { get; set; } // elbow, tee, null
        public double[] Center { get; set; }
    }
}
