using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Biruscan.Models
{
    public class FitterSettings
    {
        private static FitterSettings _instance;
        public static FitterSettings Instance => _instance ??= Load();

        public string PreferredPipeTypeName { get; set; } = "";
        public string PreferredPipingSystemTypeName { get; set; } = "";
        public string PreferredDuctTypeName { get; set; } = "";
        public string PreferredRoundDuctTypeName { get; set; } = "";
        public string PreferredDuctSystemTypeName { get; set; } = "";
        public string PreferredCableTrayTypeName { get; set; } = "";

        /// <summary>Snap measured diameters/widths to the nearest catalog size.</summary>
        public bool SnapToNominal { get; set; } = true;
        /// <summary>Only snap when the nearest catalog size is within this percent of the measurement.</summary>
        public double NominalSnapPercent { get; set; } = 12.0;
        /// <summary>Snap a nearly-axis-aligned run to X/Y/Z if the deviation is within this many degrees.</summary>
        public double OrthoSnapDegrees { get; set; } = 8.0;

        private static string SettingsFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", "2023", "Biruscan", "fitter_settings.json"
        );

        public static FitterSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var loaded = JsonSerializer.Deserialize<FitterSettings>(json) ?? new FitterSettings();
                    if (loaded.NominalSnapPercent <= 0) loaded.NominalSnapPercent = 12.0;
                    if (loaded.OrthoSnapDegrees <= 0) loaded.OrthoSnapDegrees = 8.0;
                    return loaded;
                }
            }
            catch { }
            return new FitterSettings();
        }

        /// <summary>Match a saved type name to a document type (exact, family, then contains).</summary>
        public static T MatchType<T>(IEnumerable<T> types, string preferredName) where T : Autodesk.Revit.DB.ElementType
        {
            if (types == null || string.IsNullOrWhiteSpace(preferredName)) return null;
            string want = preferredName.Trim();
            var list = types as IList<T> ?? types.ToList();
            return list.FirstOrDefault(t => string.Equals(t.Name, want, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(t => string.Equals(t.FamilyName, want, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(t => t.Name != null && t.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static double SnapInches(double measuredInches, params double[] catalog)
        {
            if (measuredInches <= 0 || catalog == null || catalog.Length == 0)
                return Math.Max(measuredInches, 0.5);

            var s = Instance;
            if (!s.SnapToNominal)
                return measuredInches;

            double best = catalog[0];
            double bestDiff = Math.Abs(measuredInches - best);
            foreach (double v in catalog)
            {
                double d = Math.Abs(measuredInches - v);
                if (d < bestDiff) { bestDiff = d; best = v; }
            }

            double allow = Math.Max(0.35, measuredInches * (s.NominalSnapPercent / 100.0));
            if (bestDiff > allow)
                return Math.Round(measuredInches * 2.0) / 2.0;
            return best;
        }

        public static XYZ OrthoSnapDirection(XYZ dir)
        {
            if (dir == null || dir.GetLength() < 1e-9) return dir;
            dir = dir.Normalize();
            double maxDeg = Instance.OrthoSnapDegrees;
            if (maxDeg <= 0) return dir;

            XYZ[] axes = { XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ, -XYZ.BasisX, -XYZ.BasisY, -XYZ.BasisZ };
            XYZ best = dir;
            double bestDot = -1;
            foreach (var a in axes)
            {
                double dot = Math.Abs(dir.DotProduct(a));
                if (dot > bestDot) { bestDot = dot; best = dir.DotProduct(a) >= 0 ? a : a.Negate(); }
            }
            double deg = Math.Acos(Math.Min(1.0, bestDot)) * 180.0 / Math.PI;
            return deg <= maxDeg ? best : dir;
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(SettingsFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch { }
        }

        public void ResetDefaults()
        {
            PreferredPipeTypeName = "";
            PreferredPipingSystemTypeName = "";
            PreferredDuctTypeName = "";
            PreferredRoundDuctTypeName = "";
            PreferredDuctSystemTypeName = "";
            PreferredCableTrayTypeName = "";
            SnapToNominal = true;
            NominalSnapPercent = 12.0;
            OrthoSnapDegrees = 8.0;
            Save();
        }
    }
}
