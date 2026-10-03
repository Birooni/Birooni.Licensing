using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace Biruscan.AI.Vision
{
    public static class RobotLidarScanner
    {
        public class OrbitFrame
        {
            public XYZ EyePosition;
            public XYZ UpDirection;
            public XYZ ForwardDirection;
        }

        public static List<OrbitFrame> Generate36AxisOrbit(XYZ roiCentroid, double orbitRadiusFt)
        {
            var frames = new List<OrbitFrame>();
            double[] elevationAngles = { Math.PI / 6.0, Math.PI / 12.0, 0.0 };
            
            foreach (double elev in elevationAngles)
            {
                double zOffset = orbitRadiusFt * Math.Sin(elev);
                double xyRadius = orbitRadiusFt * Math.Cos(elev);

                for (int i = 0; i < 12; i++)
                {
                    double azimuth = i * (2.0 * Math.PI / 12.0);
                    
                    double xOffset = xyRadius * Math.Cos(azimuth);
                    double yOffset = xyRadius * Math.Sin(azimuth);

                    XYZ eyePos = new XYZ(roiCentroid.X + xOffset, roiCentroid.Y + yOffset, roiCentroid.Z + zOffset);
                    XYZ forward = (roiCentroid - eyePos).Normalize();
                    
                    XYZ right = forward.CrossProduct(XYZ.BasisZ).Normalize();
                    if (right.GetLength() < 0.1) right = XYZ.BasisX;
                    XYZ up = right.CrossProduct(forward).Normalize();

                    frames.Add(new OrbitFrame
                    {
                        EyePosition = eyePos,
                        ForwardDirection = forward,
                        UpDirection = up
                    });
                }
            }

            return frames;
        }
    }
}
