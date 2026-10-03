using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Linq;
using System.Collections.Generic;
using Biruscan.Services;

namespace Biruscan.Commands
{
    /// <summary>
    /// Floor Plan Tool. Pops up a two-row dialog (view-range style):
    ///   Row 1 — Top:    [Level dropdown] + [value]
    ///   Row 2 — Bottom: [Level dropdown] + [value]
    /// Values are in the project's length unit. The point cloud is cut to the band
    /// between (Bottom level + Bottom value) and (Top level + Top value).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class FloorPlanToolCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                PointCloudService pcService = new PointCloudService(doc);
                if (pcService.GetFirstPointCloudInstance() == null)
                {
                    TaskDialog.Show("Floor Plan Tool", "No point cloud found.\nPlease load a point cloud first.");
                    return Result.Cancelled;
                }

                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .ToList();

                if (levels.Count == 0)
                {
                    TaskDialog.Show("Floor Plan Tool", "No levels found in the project.\nPlease create at least one level first.");
                    return Result.Cancelled;
                }

                ForgeTypeId lengthUnit = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();

                FloorPlanLevelDialog dlg = new FloorPlanLevelDialog(levels, lengthUnit);
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return Result.Cancelled;

                if (dlg.SelectedTopLevel == null || dlg.SelectedBottomLevel == null)
                    return Result.Cancelled;

                // Each bound = its level elevation + its offset (offset in project units).
                double topElev = dlg.SelectedTopLevel.Elevation + UnitUtils.ConvertToInternalUnits(dlg.TopValue, lengthUnit);
                double bottomElev = dlg.SelectedBottomLevel.Elevation + UnitUtils.ConvertToInternalUnits(dlg.BottomValue, lengthUnit);

                double hi = Math.Max(topElev, bottomElev);
                double lo = Math.Min(topElev, bottomElev);
                double thickness = hi - lo;

                if (thickness < 1e-4)
                {
                    TaskDialog.Show("Floor Plan Tool", "The Top and Bottom resolve to the same elevation — there is nothing to cut.");
                    return Result.Cancelled;
                }

                double center = (hi + lo) / 2.0;

                ClippingService clipService = new ClippingService(doc);
                clipService.ApplyHorizontalSlice(center, thickness);
                Biruscan.UI.Views.ClippingManagerWindow.Instance?.RefreshDataPublic();

                string sym = FloorPlanLevelDialog.UnitSymbol(lengthUnit);
                TaskDialog.Show("Floor Plan Tool",
                    "Point cloud cut to band:\n" +
                    $"Top:    {dlg.SelectedTopLevel.Name} + {dlg.TopValue:F2} {sym}\n" +
                    $"Bottom: {dlg.SelectedBottomLevel.Name} + {dlg.BottomValue:F2} {sym}\n" +
                    $"Thickness: {thickness:F2} ft");

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Floor Plan Tool Error", ex.Message);
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// Two-row dialog. Each row: a label (Top/Bottom), a level dropdown, and a value box.
    /// Values are in the project's length unit.
    /// </summary>
    public class FloorPlanLevelDialog : System.Windows.Forms.Form
    {
        private readonly List<Level> _levels;
        private System.Windows.Forms.ComboBox cmbTop;
        private System.Windows.Forms.ComboBox cmbBottom;
        private System.Windows.Forms.TextBox txtTop;
        private System.Windows.Forms.TextBox txtBottom;

        public Level SelectedTopLevel { get; private set; }
        public Level SelectedBottomLevel { get; private set; }
        public double TopValue { get; private set; }
        public double BottomValue { get; private set; }

        public FloorPlanLevelDialog(List<Level> levels, ForgeTypeId lengthUnit)
        {
            _levels = levels;
            string sym = UnitSymbol(lengthUnit);

            double defTop = UnitUtils.ConvertFromInternalUnits(4.0, lengthUnit);  // 4 ft above
            double defBottom = 0.0;                                               // at the level
            TopValue = defTop;
            BottomValue = defBottom;

            Text = "Floor Plan — Cut Cloud";
            Width = 450;
            Height = 185;
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;

            string[] items = _levels
                .Select(lv => $"{lv.Name}  ({UnitUtils.ConvertFromInternalUnits(lv.Elevation, lengthUnit):F2} {sym})")
                .ToArray();

            // Row 1: Top
            var lblTop = new System.Windows.Forms.Label { Text = "Top", Left = 12, Top = 20, Width = 55 };
            cmbTop = new System.Windows.Forms.ComboBox { Left = 72, Top = 17, Width = 200, DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
            cmbTop.Items.AddRange(items);
            if (cmbTop.Items.Count > 0) cmbTop.SelectedIndex = 0;
            txtTop = new System.Windows.Forms.TextBox { Text = TopValue.ToString("F2"), Left = 278, Top = 17, Width = 85 };
            var lblTopUnit = new System.Windows.Forms.Label { Text = sym, Left = 368, Top = 20, Width = 60 };

            // Row 2: Bottom
            var lblBottom = new System.Windows.Forms.Label { Text = "Bottom", Left = 12, Top = 55, Width = 55 };
            cmbBottom = new System.Windows.Forms.ComboBox { Left = 72, Top = 52, Width = 200, DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
            cmbBottom.Items.AddRange(items);
            if (cmbBottom.Items.Count > 0) cmbBottom.SelectedIndex = 0;
            txtBottom = new System.Windows.Forms.TextBox { Text = BottomValue.ToString("F2"), Left = 278, Top = 52, Width = 85 };
            var lblBottomUnit = new System.Windows.Forms.Label { Text = sym, Left = 368, Top = 55, Width = 60 };

            var btnOK = new System.Windows.Forms.Button { Text = "OK", Left = 245, Top = 100, Width = 90 };
            var btnCancel = new System.Windows.Forms.Button { Text = "Cancel", Left = 340, Top = 100, Width = 90 };

            btnOK.Click += (s, e) =>
            {
                if (cmbTop.SelectedIndex < 0 || cmbBottom.SelectedIndex < 0)
                {
                    System.Windows.Forms.MessageBox.Show("Please select a level for both Top and Bottom.");
                    return;
                }
                SelectedTopLevel = _levels[cmbTop.SelectedIndex];
                SelectedBottomLevel = _levels[cmbBottom.SelectedIndex];
                double.TryParse(txtTop.Text, out double t);
                double.TryParse(txtBottom.Text, out double b);
                TopValue = t;
                BottomValue = b;
                DialogResult = System.Windows.Forms.DialogResult.OK;
                Close();
            };
            btnCancel.Click += (s, e) =>
            {
                DialogResult = System.Windows.Forms.DialogResult.Cancel;
                Close();
            };

            Controls.AddRange(new System.Windows.Forms.Control[]
            {
                lblTop, cmbTop, txtTop, lblTopUnit,
                lblBottom, cmbBottom, txtBottom, lblBottomUnit,
                btnOK, btnCancel
            });
            AcceptButton = btnOK;
            CancelButton = btnCancel;
        }

        /// <summary>Short symbol for common length units; falls back to the unit label.</summary>
        public static string UnitSymbol(ForgeTypeId u)
        {
            try
            {
                string id = u.TypeId;
                if (id == UnitTypeId.Millimeters.TypeId) return "mm";
                if (id == UnitTypeId.Centimeters.TypeId) return "cm";
                if (id == UnitTypeId.Decimeters.TypeId) return "dm";
                if (id == UnitTypeId.Meters.TypeId) return "m";
                if (id == UnitTypeId.Inches.TypeId) return "in";
                if (id == UnitTypeId.Feet.TypeId || id == UnitTypeId.FeetFractionalInches.TypeId) return "ft";
                return LabelUtils.GetLabelForUnit(u);
            }
            catch { return "units"; }
        }
    }
}
