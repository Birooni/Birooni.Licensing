using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;

namespace Biruscan.AI.DeepLearning
{
    /// <summary>
    /// Core skill book taught into the in-process net: as-built pipe shells, walls,
    /// trays, and junction shapes (elbow / tee / cross / reducer / union).
    /// Routing *usage* (when to place each fitting) stays in MEP operation rules;
    /// this curriculum teaches the *look* of each skill in a point cloud.
    /// </summary>
    public static class MepSkillCurriculum
    {
        public static readonly string[] SkillNames =
        {
            "Straight pipe (partial-arc / edgewise shell)",
            "Sloped as-built run (not on grid)",
            "Vertical riser",
            "Elbow (90° and 45°)",
            "Tee (branch off a through-run)",
            "Cross (four-way)",
            "Reducer (in-line size change)",
            "Union (in-line same size)",
            "Rectangular duct / cable tray",
            "Wall / floor / tray-slat clutter (negatives)",
            "Insulation wrap (thicker shell)"
        };

        public static bool IsTaught()
        {
            try { return File.Exists(MarkerPath()); }
            catch { return false; }
        }

        public static string MarkerPath()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            return Path.Combine(dir, "biruscan_core_skills.json");
        }

        public static string TeachAll(int epochs = 3)
        {
            var engine = new MepPointNetNeuralEngine();
            int n = 0;
            int rounds = Math.Max(1, epochs);
            for (int e = 0; e < rounds; e++)
                n += TeachOnePass(engine, new Random(20260905 + e * 17));

            NeuralWeightsDataset.SaveCurrentWeights();
            try
            {
                File.WriteAllText(MarkerPath(),
                    "{\"taught\":true,\"samples\":" + n + ",\"utc\":\"" + DateTime.UtcNow.ToString("o") + "\"}");
            }
            catch { }

            return "Core skills taught (" + n + " synthetic lessons, " + rounds + " pass(es)). Weights saved.";
        }

        private static int TeachOnePass(MepPointNetNeuralEngine engine, Random rng)
        {
            int n = 0;

            // Straight / edgewise / as-built slope
            for (int i = 0; i < 36; i++)
            {
                double dia = 0.04 + rng.NextDouble() * 0.22; // ~1"–6"
                double len = 1.2 + rng.NextDouble() * 4.0;
                double arc = 50 + rng.NextDouble() * 130;
                XYZ a = new XYZ(rng.NextDouble(), rng.NextDouble(), rng.NextDouble() * 0.3);
                XYZ dir = SafeDir(new XYZ(1, rng.NextDouble() * 0.15 - 0.07, rng.NextDouble() * 0.08 - 0.02));
                var pts = CylinderShell(a, a + dir * len, dia, arc, 90, rng);
                engine.TrainPositiveSample(pts, MepSemanticClass.PipingCylinder, (float)(dia * 2 * 304.8), XYZ.Zero);
                n++;
            }

            // Risers
            for (int i = 0; i < 10; i++)
            {
                double dia = 0.05 + rng.NextDouble() * 0.15;
                XYZ a = new XYZ(rng.NextDouble(), rng.NextDouble(), 0);
                var pts = CylinderShell(a, a + XYZ.BasisZ * (2 + rng.NextDouble() * 3), dia, 120, 80, rng);
                engine.TrainPositiveSample(pts, MepSemanticClass.PipingCylinder, (float)(dia * 2 * 304.8), XYZ.Zero);
                n++;
            }

            // Elbows
            for (int i = 0; i < 22; i++)
            {
                bool deg45 = i % 3 == 0;
                var pts = ElbowCloud(deg45 ? 45 : 90, 0.06 + rng.NextDouble() * 0.12, rng);
                engine.TrainPositiveSample(pts, MepSemanticClass.PipingElbow, 80f, XYZ.Zero);
                n++;
            }

            // Tees
            for (int i = 0; i < 16; i++)
            {
                var pts = TeeCloud(0.06 + rng.NextDouble() * 0.12, rng);
                engine.TrainPositiveSample(pts, MepSemanticClass.PipingTee, 80f, XYZ.Zero);
                n++;
            }

            // Crosses → branching class
            for (int i = 0; i < 8; i++)
            {
                var pts = CrossCloud(0.07 + rng.NextDouble() * 0.10, rng);
                engine.TrainPositiveSample(pts, MepSemanticClass.PipingTee, 90f, XYZ.Zero);
                n++;
            }

            // Reducer / union → in-line pipe class (usage is a routing rule)
            for (int i = 0; i < 10; i++)
            {
                var pts = ReducerCloud(0.05, 0.10, rng);
                engine.TrainPositiveSample(pts, MepSemanticClass.PipingCylinder, 90f, XYZ.Zero);
                n++;
            }
            for (int i = 0; i < 8; i++)
            {
                var pts = UnionCloud(0.07, rng);
                engine.TrainPositiveSample(pts, MepSemanticClass.PipingCylinder, 80f, XYZ.Zero);
                n++;
            }

            // Duct / tray rectangles
            for (int i = 0; i < 14; i++)
            {
                var pts = BoxShell(2.5 + rng.NextDouble() * 3, 0.4 + rng.NextDouble() * 0.6, 0.2 + rng.NextDouble() * 0.25, rng);
                var cls = i % 2 == 0 ? MepSemanticClass.Duct : MepSemanticClass.CableTray;
                engine.TrainPositiveSample(pts, cls, 300f, XYZ.Zero);
                n++;
            }

            // Wall / floor / slat negatives
            for (int i = 0; i < 24; i++)
            {
                var pts = PlanePatch(3.0 + rng.NextDouble() * 4, 2.0 + rng.NextDouble() * 3, 0.04, rng);
                engine.TrainNegativeSample(pts, MepSemanticClass.WallStructureClutter);
                n++;
            }

            // Insulation: thicker shell still a pipe
            for (int i = 0; i < 8; i++)
            {
                double dia = 0.12 + rng.NextDouble() * 0.12;
                XYZ a = XYZ.Zero;
                var pts = CylinderShell(a, a + XYZ.BasisX * 3.0, dia, 140, 90, rng);
                engine.TrainPositiveSample(pts, MepSemanticClass.PipingCylinder, (float)(dia * 2 * 304.8), XYZ.Zero);
                n++;
            }

            return n;
        }

        private static XYZ SafeDir(XYZ v)
        {
            if (v == null || v.GetLength() < 1e-9) return XYZ.BasisX;
            return v.Normalize();
        }

        private static List<XYZ> CylinderShell(XYZ start, XYZ end, double radius, double arcDeg, int count, Random rng)
        {
            var pts = new List<XYZ>(count);
            XYZ axis = SafeDir(end - start);
            XYZ n = Math.Abs(axis.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
            XYZ e1 = SafeDir(axis.CrossProduct(n));
            XYZ e2 = SafeDir(axis.CrossProduct(e1));
            double len = start.DistanceTo(end);
            double arc = arcDeg * Math.PI / 180.0;
            double a0 = -arc * 0.5;
            for (int i = 0; i < count; i++)
            {
                double t = rng.NextDouble() * len;
                double ang = a0 + rng.NextDouble() * arc;
                double rr = radius * (0.92 + rng.NextDouble() * 0.16);
                pts.Add(start + axis * t + e1 * (rr * Math.Cos(ang)) + e2 * (rr * Math.Sin(ang)));
            }
            return pts;
        }

        private static List<XYZ> ElbowCloud(double bendDeg, double r, Random rng)
        {
            XYZ o = XYZ.Zero;
            XYZ a = XYZ.BasisX * 2.2;
            double rad = bendDeg * Math.PI / 180.0;
            XYZ b = (XYZ.BasisX * Math.Cos(rad) + XYZ.BasisY * Math.Sin(rad)) * 2.2;
            var pts = CylinderShell(o, a, r, 110, 50, rng);
            pts.AddRange(CylinderShell(o, b, r, 110, 50, rng));
            return pts;
        }

        private static List<XYZ> TeeCloud(double r, Random rng)
        {
            var pts = CylinderShell(new XYZ(-2, 0, 0), new XYZ(2, 0, 0), r, 120, 70, rng);
            pts.AddRange(CylinderShell(XYZ.Zero, new XYZ(0, 1.8, 0), r, 110, 45, rng));
            return pts;
        }

        private static List<XYZ> CrossCloud(double r, Random rng)
        {
            var pts = TeeCloud(r, rng);
            pts.AddRange(CylinderShell(XYZ.Zero, new XYZ(0, -1.8, 0), r, 110, 40, rng));
            return pts;
        }

        private static List<XYZ> ReducerCloud(double rSmall, double rLarge, Random rng)
        {
            var pts = CylinderShell(new XYZ(-2, 0, 0), XYZ.Zero, rLarge, 120, 50, rng);
            pts.AddRange(CylinderShell(XYZ.Zero, new XYZ(2, 0, 0), rSmall, 120, 50, rng));
            return pts;
        }

        private static List<XYZ> UnionCloud(double r, Random rng)
        {
            var pts = CylinderShell(new XYZ(-2, 0, 0), new XYZ(-0.08, 0, 0), r, 120, 40, rng);
            pts.AddRange(CylinderShell(new XYZ(0.08, 0, 0), new XYZ(2, 0, 0), r, 120, 40, rng));
            return pts;
        }

        private static List<XYZ> BoxShell(double length, double width, double height, Random rng)
        {
            var pts = new List<XYZ>(80);
            for (int i = 0; i < 80; i++)
            {
                double t = rng.NextDouble() * length;
                int face = rng.Next(4);
                double u = (rng.NextDouble() - 0.5);
                XYZ p;
                if (face == 0) p = new XYZ(t, u * width, height * 0.5);
                else if (face == 1) p = new XYZ(t, u * width, -height * 0.5);
                else if (face == 2) p = new XYZ(t, width * 0.5, u * height);
                else p = new XYZ(t, -width * 0.5, u * height);
                pts.Add(p);
            }
            return pts;
        }

        private static List<XYZ> PlanePatch(double w, double h, double thick, Random rng)
        {
            var pts = new List<XYZ>(70);
            XYZ n = SafeDir(new XYZ(rng.NextDouble() * 0.2, rng.NextDouble() * 0.2, 1));
            XYZ t1 = SafeDir(n.CrossProduct(XYZ.BasisX.GetLength() > 0.2 ? XYZ.BasisX : XYZ.BasisY));
            XYZ t2 = SafeDir(n.CrossProduct(t1));
            for (int i = 0; i < 70; i++)
            {
                pts.Add(t1 * ((rng.NextDouble() - 0.5) * w) + t2 * ((rng.NextDouble() - 0.5) * h) + n * ((rng.NextDouble() - 0.5) * thick));
            }
            return pts;
        }
    }
}
