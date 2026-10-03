using Autodesk.Revit.UI;
using Biruscan.UI;

namespace Biruscan
{
    /// <summary>
    /// Revit ExternalApplication entry point for Biruscan.
    /// Builds the Ribbon tab, panels, buttons, and sub-menus across all 8 feature areas.
    /// </summary>
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            _ = Biruscan.Core.LicensingManager.InitializeAsync();

            string tabName = "Biruscan";

            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch
            {
                // Tab may already exist if reloaded
            }

            RibbonHelper helper = new RibbonHelper(application, tabName);
            BuildRibbon(helper);

            // Initialize Auto-Training & Active Learning Manager
            try
            {
                Biruscan.AI.DeepLearning.NeuralWeightsDataset.ReloadFromDisk();
                Biruscan.AI.DeepLearning.OnnxMepClassifier.TryLoad();
                if (!Biruscan.AI.DeepLearning.MepSkillCurriculum.IsTaught())
                    Biruscan.AI.DeepLearning.MepSkillCurriculum.TeachAll(epochs: 2);
                Services.Ai.AutoTrainingManager.Instance.Initialize(application);
                Services.Ai.UserModelingWatcher.Instance.Initialize(application.ControlledApplication);
            }
            catch { }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                Commands.MepAutoConnectCommand.CancelSession();
                Services.Ai.AutoTrainingManager.Instance.Shutdown(application);
                Services.Ai.UserModelingWatcher.Instance.Shutdown(application.ControlledApplication);
            }
            catch { }

            return Result.Succeeded;
        }

        private void BuildRibbon(RibbonHelper helper)
        {
            // =============================================
            // PANEL 1: Project â€” Open & Save Clip Project Files
            // =============================================
            RibbonPanel projectPanel = helper.CreatePanel("Project");

            helper.AddPushButton(projectPanel, typeof(Commands.OpenClipCommand),
                "Open\nClip", "Load and apply saved clipping state and groups from a file (.bclip).");

            helper.AddPushButton(projectPanel, typeof(Commands.SaveClipCommand),
                "Save\nClip", "Save active clipping state and groups to a file (.bclip).");

            // =============================================
            // PANEL 2: Clipping â€” Sectioning & Cut Planes
            // =============================================
            RibbonPanel clippingPanel = helper.CreatePanel("Clipping");

            helper.AddPushButton(clippingPanel, typeof(Commands.RectangularCutCommand),
                "Rectangular", "Perform rectangular crop / cut on the point cloud.");

            helper.AddPushButton(clippingPanel, typeof(Commands.PolygonalCutCommand),
                "Polygonal", "Perform arbitrary polygonal boundary cut on the point cloud.");

            helper.AddPushButton(clippingPanel, typeof(Commands.HorizontalSliceCommand),
                "Horizontal\nSlice", "Slice point cloud horizontally (Floor / Ceiling cut).");

            helper.AddPushButton(clippingPanel, typeof(Commands.VerticalSliceCommand),
                "Vertical\nSlice", "Slice point cloud vertically along a reference line.");

            helper.AddPushButton(clippingPanel, typeof(Commands.SliceForwardCommand),
                "Slice\nForward", "Step active slice forward along its normal vector.");

            helper.AddPushButton(clippingPanel, typeof(Commands.SliceBackwardCommand),
                "Slice\nBackward", "Step active slice backward along its normal vector.");

            helper.AddPushButton(clippingPanel, typeof(Commands.ClippingManagerCommand),
                "Manager", "Open the Clipping & Section Plane Manager.");

            helper.AddPushButton(clippingPanel, typeof(Commands.ClearCutsCommand),
                "Clear\nCuts", "Reset and remove all active clipping boundaries and section boxes.");

            // =============================================
            // PANEL 3: View â€” Visualization, Visibility & Sections
            // =============================================
            RibbonPanel viewPanel = helper.CreatePanel("View");

            helper.AddPushButton(viewPanel, typeof(Commands.VisibilityToggleCommand),
                "Visibility\nToggle", "Toggle visibility of point cloud scans, MEP elements, and architectural reference layers.");

            helper.AddPushButton(viewPanel, typeof(Commands.RenderingCommand),
                "Rendering", "Switch point cloud visual rendering modes: Intensity, Elevation, RGB, Normals.");

            helper.AddPushButton(viewPanel, typeof(Commands.SectionBoxCommand),
                "Section\nBox", "Create a fitted 3D Section Box around selected elements or ROI.");

            // =============================================
            // PANEL 4: Floor Plan â€” 2D Extraction Tools
            // =============================================
            RibbonPanel floorPlanPanel = helper.CreatePanel("Floor Plan");

            helper.AddPushButton(floorPlanPanel, typeof(Commands.FloorPlanToolCommand),
                "Floor Plan\nTool", "Extract 2D floor plans, wall centerlines, and room boundaries from point cloud slices.");

            // =============================================
            // PANEL 5: Fitter Tools â€” Parametric Fitting
            // =============================================
            RibbonPanel fitterToolsPanel = helper.CreatePanel("Fitter Tools");

            helper.AddPushButton(fitterToolsPanel, typeof(Commands.WallFitterCommand),
                "Wall\nFitter", "Fit architectural walls to point cloud planar surfaces.");

            helper.AddPushButton(fitterToolsPanel, typeof(Commands.PipeFitterCommand),
                "Pipe\nFitter", "Extract and place parametric Pipes from cylindrical point cloud clusters.");

            helper.AddPushButton(fitterToolsPanel, typeof(Commands.SteelFitterCommand),
                "Steel\nFitter", "Fit structural steel beams and columns (I-Beams, HSS, Channels) to scans.");

            helper.AddPushButton(fitterToolsPanel, typeof(Commands.DuctFitterCommand),
                "Duct\nFitter", "Extract rectangular and round HVAC ducts from point cloud runs.");

            helper.AddPushButton(fitterToolsPanel, typeof(Commands.CableTrayFitterCommand),
                "Cable Tray\nFitter", "Fit electrical cable trays and conduit runs to point cloud data.");

            helper.AddPushButton(fitterToolsPanel, typeof(Commands.WindowFitterCommand),
                "Window\nFitter", "Detect openings and place windows/doors in fitted walls.");

            helper.AddPushButton(fitterToolsPanel, typeof(Commands.FitGenerateMepCommand),
                "Generate\nMEP", "Edgewise geometric fit: find pipes on the whole point cloud without a clip (optional clip for a smaller region).");

            helper.AddPushButton(fitterToolsPanel, typeof(Commands.FitterSettingsCommand),
                "Fitter\nSettings", "Configure preferred element types (Pipe, Duct, Cable Tray) for the Fitter tools.");

            // =============================================
            // PANEL 6: AI Tools — ML Extraction & Training
            // =============================================
            RibbonPanel aiToolsPanel = helper.CreatePanel("AI Tools");

            helper.AddPushButton(aiToolsPanel, typeof(Commands.AiGenerateMepCommand),
                "AI Generate\nMEP", "Extract complete MEP systems using unified Standard, Trained, or Dual-Engine Combined AI.");

            helper.AddPushButton(aiToolsPanel, typeof(Commands.TrainAiModelCommand),
                "Train AI", "Supervised training dataset capture and real-time active learning from Fitter and MEP operations.");

            // =============================================
            // PANEL 8: MEP Tools - Conversion & Operations
            // =============================================
            RibbonPanel mepToolsPanel = helper.CreatePanel("MEP Tools");

            helper.AddPushButton(mepToolsPanel, typeof(Commands.MepConverterCommand),
                "Converter", "Convert between MEP element types and networks: Round Duct to Pipe, Pipe to Conduit, Conduit to Pipe, and Pipe to Round Duct.");

            var opsPulldown = helper.AddPulldownButton(mepToolsPanel, "MepOperationsDropdown", "Operation",
                "MEP connection operations: Trim and connect Elbows, Tees, and Taps across Pipes, Ducts, Conduits, and Cable Trays.");

            if (opsPulldown != null)
            {
                helper.AddPulldownItem(opsPulldown, typeof(Commands.MepAutoElbowCommand),
                    "Elbow", "Connect two MEP elements (Pipe, Duct, Conduit, Cable Tray) with an Elbow fitting, auto-aligning to a common plane/elevation.");

                helper.AddPulldownItem(opsPulldown, typeof(Commands.MepAutoTeeCommand),
                    "Tee", "Connect a branch MEP element to a main run with a Tee or Tap fitting, auto-aligning elevations and splitting the main run.");

                helper.AddPulldownItem(opsPulldown, typeof(Commands.MepAutoUnionCommand),
                    "Union", "Unify two pipes (or MEP runs) with a Union or Coupling fitting, maintaining maximum possible angle and system connection (Tab).");

                helper.AddPulldownItem(opsPulldown, typeof(Commands.MepAutoReducerCommand),
                    "Reducer", "Unify two pipes of different sizes with a Reducer/Transition while maintaining distinct axes.");

                helper.AddPulldownItem(opsPulldown, typeof(Commands.MepAutoUnifyCommand),
                    "Unify", "Merge two collinear pipes into a single continuous pipe, removing any gap or fittings between them.");

                helper.AddPulldownItem(opsPulldown, typeof(Commands.MepAutoCrossCommand),
                    "Cross", "Convert a Tee to a Cross or connect directly to an existing Cross while respecting network anchors.");

                helper.AddPulldownItem(opsPulldown, typeof(Commands.MepAutoConnectCommand),
                    "Auto", "Automatically place Union, Elbow, Tee, or Cross from the selected pipes (window or Ctrl+click).");

                helper.AddPulldownItem(opsPulldown, typeof(Commands.MepStretchCommand),
                    "Stretch", "Stretch a Pipe, Duct, Conduit, or Cable Tray along its axis. Connected fittings and runs move with each end. Maximum stretch defaults to 5 m in the project length unit.");
            }

            var fittingsPulldown = helper.AddPulldownButton(mepToolsPanel, "MepFittingsDropdown", "Fittings",
                "MEP fittings and automated routing tools: Pull stubs and directional elbows for Pipes, Ducts, and Conduits.");

            if (fittingsPulldown != null)
            {
                helper.AddPulldownItem(fittingsPulldown, typeof(Commands.MepPullCommand),
                    "Pull", "Automatically pull and draw pipe and duct stubs from all open/unconnected connectors on selected equipment or fixtures.");

                helper.AddPulldownItem(fittingsPulldown, typeof(Commands.MepElbowUpCommand),
                    "Elbow Up", "Draw an elbow bending Up (+Z) at the open end of a selected Pipe, Duct, or Conduit.");

                helper.AddPulldownItem(fittingsPulldown, typeof(Commands.MepElbowDownCommand),
                    "Elbow Down", "Draw an elbow bending Down (-Z) at the open end of a selected Pipe, Duct, or Conduit.");

                helper.AddPulldownItem(fittingsPulldown, typeof(Commands.MepElbowLeftCommand),
                    "Elbow Left", "Draw an elbow bending Left at the open end of a selected Pipe, Duct, or Conduit.");

                helper.AddPulldownItem(fittingsPulldown, typeof(Commands.MepElbowRightCommand),
                    "Elbow Right", "Draw an elbow bending Right at the open end of a selected Pipe, Duct, or Conduit.");

                helper.AddPulldownItem(fittingsPulldown, typeof(Commands.MepElbowUp45Command),
                    "Elbow Up 45", "Draw a 45-degree elbow bending Up (+45 deg elevation) at the open end of a selected Pipe, Duct, or Conduit.");

                helper.AddPulldownItem(fittingsPulldown, typeof(Commands.MepElbowDown45Command),
                    "Elbow Down 45", "Draw a 45-degree elbow bending Down (-45 deg elevation) at the open end of a selected Pipe, Duct, or Conduit.");

                helper.AddPulldownItem(fittingsPulldown, typeof(Commands.MepElbowLeft45Command),
                    "Elbow Left 45", "Draw a 45-degree elbow bending Left (45 deg) at the open end of a selected Pipe, Duct, or Conduit.");

                helper.AddPulldownItem(fittingsPulldown, typeof(Commands.MepElbowRight45Command),
                    "Elbow Right 45", "Draw a 45-degree elbow bending Right (45 deg) at the open end of a selected Pipe, Duct, or Conduit.");
                helper.AddPulldownItem(fittingsPulldown, typeof(Commands.MepElbowCustomCommand),
                    "Elbow Custom", "Draw a custom angle elbow with axial rotation at the open end of a selected Pipe, Duct, or Conduit.");

                helper.AddPulldownItem(fittingsPulldown, typeof(Commands.MepIncrementCommand),
                    "Increment", "Stretch a Pipe, Duct, Conduit, or Cable Tray along its axis. Each end has a slider; maximum increment defaults to 5 m in the project length unit.");

                helper.AddPulldownItem(fittingsPulldown, typeof(Commands.MepSettingsCommand),
                    "Settings", "Configure MEP routing settings: Pull stub lengths, elbow extension lengths, and default fitting types.");
            }
        }
    }
}