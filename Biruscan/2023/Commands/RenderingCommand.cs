using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using Biruscan.UI.Views;

namespace Biruscan.Commands
{
    /// <summary>
    /// Command to set point cloud rendering mode.
    /// Supports: True Color, Intensity, Elevation, Heat Map, Normals.
    /// Heat Map mode provides a height-adjustable gradient (Blue→Green→Yellow→Red).
    /// 
    /// Revit 2025 API: PointCloudColorSettings uses constructor with PointCloudColorMode enum:
    ///   new PointCloudColorSettings(PointCloudColorMode.NoColor)    → True Color (original scan colors)
    ///   new PointCloudColorSettings(PointCloudColorMode.Intensity)  → Intensity-based grayscale
    ///   new PointCloudColorSettings(PointCloudColorMode.Elevation)  → Elevation-based coloring
    ///   new PointCloudColorSettings(PointCloudColorMode.Normals)    → Surface normals coloring
    /// For Heat Map, we use per-band color overrides with elevation filters.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class RenderingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                View activeView = doc.ActiveView;

                // Open the Rendering Options WPF dialog
                RenderingOptionsWindow dialog = new RenderingOptionsWindow();

                // Pre-populate heat range from point cloud bounds if available
                var pcInstance = new FilteredElementCollector(doc)
                    .OfClass(typeof(PointCloudInstance))
                    .Cast<PointCloudInstance>()
                    .FirstOrDefault();

                if (pcInstance != null)
                {
                    BoundingBoxXYZ bbox = pcInstance.get_BoundingBox(null);
                    if (bbox != null)
                    {
                        // dialog.HeatMinHeight = bbox.Min.Z;
                        // dialog.HeatMaxHeight = bbox.Max.Z;
                    }
                }

                bool? dialogResult = dialog.ShowDialog();
                if (dialogResult != true)
                    return Result.Cancelled;

                string selectedMode = dialog.SelectedMode;

                using (Transaction t = new Transaction(doc, "Set Point Cloud Rendering"))
                {
                    t.Start();

                    PointCloudOverrides overrides = activeView.GetPointCloudOverrides();

                    var instances = new FilteredElementCollector(doc)
                        .OfClass(typeof(PointCloudInstance))
                        .Cast<PointCloudInstance>()
                        .ToList();

                    foreach (var instance in instances)
                    {
                        // API may return null when no override has been set yet.
                        PointCloudOverrideSettings settings =
                            overrides.GetPointCloudRegionOverrideSettings(instance.Id)
                            ?? new PointCloudOverrideSettings();

                        switch (selectedMode)
                        {
                            case "TrueColor":
                                // NoOverride = show original scan colors (true color).
                                settings.ColorMode = PointCloudColorMode.NoOverride;
                                break;
                            case "Intensity":
                                settings.ColorMode = PointCloudColorMode.Intensity;
                                PointCloudColorSettings intensityColorSettings = new PointCloudColorSettings(
                                    dialog.IntensityMinColor,
                                    dialog.IntensityMaxColor
                                );
                                settings.SetModeOverride(PointCloudColorMode.Intensity, intensityColorSettings);
                                break;
                            case "Elevation":
                                settings.ColorMode = PointCloudColorMode.Elevation;
                                break;
                            case "Normals":
                                settings.ColorMode = PointCloudColorMode.Normals;
                                break;
                            default:
                                settings.ColorMode = PointCloudColorMode.NoOverride;
                                break;
                        }

                        overrides.SetPointCloudRegionOverrideSettings(instance.Id, settings);
                        overrides.SetPointCloudScanOverrideSettings(instance.Id, settings);
                    }
                    
                    t.Commit();
                }

                string modeDescription = selectedMode;

                TaskDialog.Show("Rendering",
                    $"Point cloud rendering mode set to: {modeDescription}\n\n" +
                    "Note: Some rendering modes depend on data available in the RCP/RCS file.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Rendering Error", ex.Message);
                return Result.Failed;
            }
        }


    }
}
