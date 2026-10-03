using System;
using System.Windows;
using System.Windows.Media;

namespace Biruscan.Services
{
    /// <summary>
    /// Service for generating resolution-independent WPF vector icons from SVG geometry paths.
    /// Eliminates dependency on external raster image files and guarantees crisp display on high-DPI displays.
    /// </summary>
    public static class RibbonIconService
    {
        /// <summary>
        /// Retrieves the vector DrawingImage corresponding to a given command name.
        /// </summary>
        public static ImageSource GetIcon(string commandName)
        {
            switch (commandName)
            {
                // Panel 1: Project
                case "SaveClipCommand":
                case "SaveClippingCommand":
                    return CreateVectorIcon("M15,9H5V5H15M12,19A3,3 0 0,1 9,16A3,3 0 0,1 12,13A3,3 0 0,1 15,16A3,3 0 0,1 12,19M17,3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V7L17,3Z", "#FFA000");

                case "OpenClipCommand":
                case "OpenClippingCommand":
                    return CreateVectorIcon("M19,20H4C2.89,20 2,19.1 2,18V6C2,4.89 2.89,4 4,4H10L12,6H19A2,2 0 0,1 21,8H21L4,8V18L6.14,10H23.21L20.93,18.5C20.7,19.37 19.92,20 19,20Z", "#FFA000");

                // Panel 2: Clipping
                case "RectangularCutCommand":
                    return CreateVectorIcon("M7,17V1H5V5H1V7H5V17A2,2 0 0,0 7,19H17V23H19V19H23V17H19V7A2,2 0 0,0 17,5H7V17M17,17H7V7H17V17Z", "#E91E63");

                case "PolygonalCutCommand":
                    return CreateVectorIcon("M12,2.5L2,9.8L5.8,21.5H18.2L22,9.8L12,2.5M12,5.5L19.6,11.1L16.7,19.2H7.3L4.4,11.1L12,5.5Z", "#E91E63");

                case "HorizontalSliceCommand":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M2,9.5 H22 V14.5 H2 Z", "#90CAF9"),
                        ("M2,7.5 H22 V9.5 H2 Z M2,14.5 H22 V16.5 H2 Z", "#0288D1"),
                        ("M12,1 L7.5,5.5 H10.5 V7.5 H13.5 V5.5 H16.5 Z M10.5,16.5 H13.5 V18.5 H16.5 L12,23 L7.5,18.5 H10.5 Z", "#00C853")
                    );

                case "VerticalSliceCommand":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M9.5,2 H14.5 V22 H9.5 Z", "#90CAF9"),
                        ("M7.5,2 H9.5 V22 H7.5 Z M14.5,2 H16.5 V22 H14.5 Z", "#0288D1"),
                        ("M1,12 L5.5,7.5 V10.5 H7.5 V13.5 H5.5 V16.5 Z M16.5,10.5 H18.5 V7.5 L23,12 L18.5,16.5 V13.5 H16.5 Z", "#00C853")
                    );

                case "SliceForwardCommand":
                    return CreateVectorIcon("M4,18L12.5,12L4,6V18M13,6V18L21.5,12L13,6Z", "#2196F3");

                case "SliceBackwardCommand":
                    return CreateVectorIcon("M11,18V6L2.5,12L11,18M20,18V6L11.5,12L20,18Z", "#2196F3");

                case "ClippingManagerCommand":
                    return CreateVectorIcon("M3,17V19H9V17H3M3,5V7H13V5H3M13,21V19H21V17H13V15H11V21H13M7,9V11H3V13H7V15H9V9H7M21,13V11H11V13H21M15,9H17V7H21V5H17V3H15V9Z", "#607D8B");

                case "ClearCutsCommand":
                    return CreateVectorIcon("M19,4H15.5L14.5,3H9.5L8.5,4H5V6H19M6,19A2,2 0 0,0 8,21H16A2,2 0 0,0 18,19V7H6V19Z", "#F44336");

                // Panel 3: View
                case "VisibilityToggleCommand":
                    return CreateVectorIcon("M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5M12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9Z", "#00BCD4");

                case "RenderingCommand":
                    return CreateVectorIcon("M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A2,2 0 0,0 14,20C14,19.5 13.8,19 13.4,18.6C13,18.2 12.8,17.7 12.8,17.2C12.8,16.1 13.7,15.2 14.8,15.2H16A6,6 0 0,0 22,9.2C22,5.2 17.5,2 12,2M6.5,12A1.5,1.5 0 0,1 5,10.5A1.5,1.5 0 0,1 6.5,9A1.5,1.5 0 0,1 8,10.5A1.5,1.5 0 0,1 6.5,12M9.5,8A1.5,1.5 0 0,1 8,6.5A1.5,1.5 0 0,1 9.5,5A1.5,1.5 0 0,1 11,6.5A1.5,1.5 0 0,1 9.5,8M14.5,8A1.5,1.5 0 0,1 13,6.5A1.5,1.5 0 0,1 14.5,5A1.5,1.5 0 0,1 16,6.5A1.5,1.5 0 0,1 14.5,8M17.5,12A1.5,1.5 0 0,1 16,10.5A1.5,1.5 0 0,1 17.5,9A1.5,1.5 0 0,1 19,10.5A1.5,1.5 0 0,1 17.5,12Z", "#9C27B0");

                case "SectionBoxCommand":
                    return CreateVectorIcon("M21,16.5C21,16.88 20.79,17.21 20.47,17.38L12.57,21.82C12.41,21.94 12.21,22 12,22C11.79,22 11.59,21.94 11.43,21.82L3.53,17.38C3.21,17.21 3,16.88 3,16.5V7.5C3,7.12 3.21,6.79 3.53,6.62L11.43,2.18C11.59,2.06 11.79,2 12,2C12.21,2 12.41,2.06 12.57,2.18L20.47,6.62C20.79,6.79 21,7.12 21,7.5V16.5M12,4.15L6.04,7.5L12,10.85L17.96,7.5L12,4.15M5,8.91V15.77L11,19.14V12.28L5,8.91M19,8.91L13,12.28V19.14L19,15.77V8.91Z", "#009688");

                // Panel 4: Floor Plan
                case "FloorPlanToolCommand":
                    return CreateVectorIcon("M19,3H5C3.9,3 3,3.9 3,5V19C3,20.1 3.9,21 5,21H19C20.1,21 21,20.1 21,19V5C21,3.9 20.1,3 19,3M19,19H13V15H11V19H5V5H19V19M17,7H7V9H17V7M17,11H7V13H17V11Z", "#3F51B5");

                // Panel 5: Fitter Tools
                case "WallFitterCommand":
                    return CreateVectorIcon("M19,4H5C3.89,4 3,4.89 3,6V18A2,2 0 0,0 5,20H19A2,2 0 0,0 21,18V6C21,4.89 20.1,4 19,4M19,7H14V6H19V7M12,6V7H5V6H12M5,9H10V11H5V9M12,9H17V11H12V9M19,9V11H19V9M5,13H14V15H5V13M16,13H19V15H16V13M19,18H12V17H19V18M10,18H5V17H10V18Z", "#8D6E63");

                case "PipeFitterCommand":
                    return CreateVectorIcon("M2,7 H5 V9 H19 V7 H22 V17 H19 V15 H5 V17 H2 V7 M7,11 H17 V13 H7 V11 Z", "#0288D1");

                case "SteelFitterCommand":
                    return CreateVectorIcon("M5,3H19V7H15V17H19V21H5V17H9V7H5V3M7,5V5H17V5H7M11,7V17H13V7H11M7,19H17V19H7V19Z", "#455A64");

                case "HVACFitterCommand":
                case "DuctFitterCommand":
                    return CreateVectorIcon("M2,5H22V19H2V5M4,7V17H7V7H4M9,7V17H15V7H9M17,7V17H20V7H17M10,9H14V11H10V9M10,13H14V15H10V13Z", "#00897B");

                case "CableTrayFitterCommand":
                    return CreateVectorIcon("M6,2H8V6H16V2H18V22H16V18H8V22H6V2M8,8V11H16V8H8M8,13V16H16V13H8Z", "#FFA000");

                case "WindowFitterCommand":
                    return CreateVectorIcon("M19,3H5C3.9,3 3,3.9 3,5V19C3,20.1 3.9,21 5,21H19C20.1,21 21,20.1 21,19V5C21,3.9 20.1,3 19,3M11,5V11H5V5H11M5,13H11V19H5V13M19,19H13V13H19V19M19,11H13V5H19V11Z", "#00ACC1");

                // Panel 6: System Fit
                case "FitGenerateMepCommand":
                    return CreateVectorIcon("M7.5,5.6L5,7L6.4,4.5L5,2L7.5,3.4L10,2L8.6,4.5L10,7L7.5,5.6M19.5,15.4L22,14L20.6,16.5L22,19L19.5,17.6L17,19L18.4,16.5L17,14L19.5,15.4M22,2L20.6,4.5L22,7L19.5,5.6L17,7L18.4,4.5L17,2L19.5,3.4L22,2M13.34,12.78L15.78,10.34L13.66,8.22L11.22,10.66L13.34,12.78M14.39,4.26L19.74,9.61C20.13,10 20.13,10.63 19.74,11.03L12.03,18.74C11.64,19.13 11,19.13 10.61,18.74L5.26,13.39C4.87,13 4.87,12.37 5.26,11.97L12.97,4.26C13.37,3.87 14,3.87 14.39,4.26Z", "#4CAF50");

                // Panel 7: AI Tools
                case "AiGenerateMepCommand":
                    return CreateVectorIcon("M19,1L17.74,3.75L15,5L17.74,6.26L19,9L20.25,6.26L23,5L20.25,3.75L19,1M9,4L6.5,9.5L1,12L6.5,14.5L9,20L11.5,14.5L17,12L11.5,9.5L9,4M19,15L17.74,17.75L15,19L17.74,20.26L19,23L20.25,20.26L23,19L20.25,17.75L19,15Z", "#00E676");

                case "TrainAiModelCommand":
                    return CreateVectorIcon("M12,2A3,3 0 0,0 9,5C9,5.28 9.04,5.56 9.11,5.82L5.82,9.11C5.56,9.04 5.28,9 5,9A3,3 0 0,0 2,12A3,3 0 0,0 5,15C5.28,15 5.56,14.96 5.82,14.89L9.11,18.18C9.04,18.44 9,18.72 9,19A3,3 0 0,0 12,22A3,3 0 0,0 15,19C15,18.72 14.96,18.44 14.89,18.18L18.18,14.89C18.44,14.96 18.72,15 19,15A3,3 0 0,0 22,12A3,3 0 0,0 19,9C18.72,9 18.44,9.04 18.18,9.11L14.89,5.82C14.96,5.56 15,5.28 15,5A3,3 0 0,0 12,2M12,4A1,1 0 0,1 13,5A1,1 0 0,1 12,6A1,1 0 0,1 11,5A1,1 0 0,1 12,4M5,11A1,1 0 0,1 6,12A1,1 0 0,1 5,13A1,1 0 0,1 4,12A1,1 0 0,1 5,11M19,11A1,1 0 0,1 20,12A1,1 0 0,1 19,13A1,1 0 0,1 18,12A1,1 0 0,1 19,11M12,18A1,1 0 0,1 13,19A1,1 0 0,1 12,20A1,1 0 0,1 11,19A1,1 0 0,1 12,18M12,8A4,4 0 0,1 16,12A4,4 0 0,1 12,16A4,4 0 0,1 8,12A4,4 0 0,1 12,8M12,10A2,2 0 0,0 10,12A2,2 0 0,0 12,14A2,2 0 0,0 14,12A2,2 0 0,0 12,10Z", "#2196F3");

                case "AiGenerateTrainedCommand":
                    return CreateVectorIcon("M2.81,14.12L5.64,11.29L8.16,12.61L11.7,9.06L9.58,6.94L10.99,5.53L13.12,7.65L16.66,4.11L15.34,1.59L18.17,4.42C21.3,7.55 21.3,12.63 18.17,15.76L17.75,16.18L14.92,13.35L13.5,14.77L16.34,17.6L15.92,18.02C12.79,21.15 7.71,21.15 4.58,18.02L2.81,14.12M12,12A2,2 0 0,0 10,14A2,2 0 0,0 12,16A2,2 0 0,0 14,14A2,2 0 0,0 12,12Z", "#AB47BC");

                // Panel 8: MEP Tools
                case "MepConverterCommand":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        // Top Pipe (Blue)
                        ("M2,2.5 H4.5 V7.5 H2 Z M19.5,2.5 H22 V7.5 H19.5 Z M4.5,3.5 H19.5 V6.5 H4.5 Z", "#0288D1"),
                        // Two Green Arrows (One Up, One Down)
                        ("M9,8.5 L6,11.5 H7.8 V15.5 H10.2 V11.5 H12 Z M13.8,8.5 H16.2 V12.5 H18 L15,15.5 L12,12.5 H13.8 Z", "#00C853"),
                        // Bottom Duct (Teal)
                        ("M2,16.5 H4.5 V21.5 H2 Z M19.5,16.5 H22 V21.5 H19.5 Z M4.5,17.5 H19.5 V20.5 H4.5 Z", "#00897B")
                    );

                case "MepOperationsDropdown":
                case "MepOperations":
                case "Operation":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        // Blue Horizontal Pipe with End Flanges
                        ("M2,13 H5 V21 H2 Z M19,13 H22 V21 H19 Z", "#01579B"),
                        ("M5,14.5 H19 V19.5 H5 Z", "#0288D1"),
                        ("M5,15.5 H19 V16.8 H5 Z", "#81D4FA"),
                        // Operation Symbol (Mechanical Gear) centered on pipe
                        ("M12,1 L13.2,1.3 L13.6,2.6 L14.9,3.1 L16,2.3 L16.9,3.2 L16.2,4.3 L16.6,5.5 L17.9,5.9 L17.9,7.1 L16.6,7.5 L16.2,8.7 L16.9,9.8 L16,10.7 L14.9,9.9 L13.6,10.4 L13.2,11.7 L12,12 L10.8,11.7 L10.4,10.4 L9.1,9.9 L8,10.7 L7.1,9.8 L7.8,8.7 L7.4,7.5 L6.1,7.1 L6.1,5.9 L7.4,5.5 L7.8,4.3 L7.1,3.2 L8,2.3 L9.1,3.1 L10.4,2.6 L10.8,1.3 Z", "#FFA000"),
                        ("M12,4.5 A2,2 0 1,1 11.99,4.5 Z", "#FFFFFF"),
                        ("M12,5.5 A1,1 0 1,1 11.99,5.5 Z", "#E65100")
                    );

                case "MepAutoElbowCommand":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M19,2 H22 V9.5 H19 Z M2,19 H9.5 V22 H2 Z", "#01579B"),
                        ("M3.5,19.5 A16,16 0 0,1 19.5,3.5 V9.5 A10,10 0 0,0 9.5,19.5 Z", "#0288D1"),
                        ("M4.8,19.5 A14.5,14.5 0 0,1 19.5,4.8 V6.2 A13.1,13.1 0 0,0 6.2,19.5 Z", "#81D4FA"),
                        ("M17.5,3.5 H19 V9.5 H17.5 Z M3.5,17.5 H9.5 V19 H3.5 Z", "#0277BD")
                    );

                case "MepAutoTeeCommand":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M1.5,3.5 H3.5 V11.5 H1.5 Z M20.5,3.5 H22.5 V11.5 H20.5 Z M8,20.5 H16 V22.5 H8 Z", "#01579B"),
                        ("M3.5,4.5 H20.5 V10.5 H16 Q15,10.5 15,11.5 V20.5 H9 V11.5 Q9,10.5 8,10.5 H3.5 Z", "#0288D1"),
                        ("M4,6 H20 V7.3 H4 Z M10.5,10.5 H12 V20 H10.5 Z", "#81D4FA"),
                        ("M8.5,4.5 H15.5 V5.8 H8.5 Z", "#00BCD4")
                    );

                case "MepAutoUnionCommand":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M1.5,8 H3.5 V16 H1.5 Z M20.5,8 H22.5 V16 H20.5 Z M6.5,8.5 H8 V15.5 H6.5 Z M16,8.5 H17.5 V15.5 H16 Z", "#01579B"),
                        ("M3.5,9 H8 V15 H3.5 Z M16,9 H20.5 V15 H16 Z", "#0288D1"),
                        ("M4,10.5 H7.5 V11.8 H4 Z M16.5,10.5 H20 V11.8 H16.5 Z", "#81D4FA"),
                        ("M9.5,5.5 H14.5 L16.5,7.5 V16.5 L14.5,18.5 H9.5 L7.5,16.5 V7.5 Z", "#FFA000"),
                        ("M11,5.5 H13 V18.5 H11 Z", "#FF6D00"),
                        ("M9.5,7.5 H14.5 V9 H9.5 Z M9.5,11 H14.5 V12.5 H9.5 Z M9.5,15 H14.5 V16.5 H9.5 Z", "#FFE082")
                    );

                case "MepAutoReducerCommand":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M1.5,5.5 H3.5 V18.5 H1.5 Z M20.5,8.2 H22.5 V15.8 H20.5 Z", "#01579B"),
                        ("M6.5,6 H7.8 V18 H6.5 Z M15.7,8.7 H17 V15.3 H15.7 Z", "#01579B"),
                        ("M3.5,6.5 H7.5 L16,9.25 H20.5 V14.75 H16 L7.5,17.5 H3.5 Z", "#0288D1"),
                        ("M4,8.2 H7.5 L16,10.6 H20 V11.9 H16 L7.5,9.8 H4 Z", "#81D4FA"),
                        ("M11,7.9 H12.5 V16.1 H11 Z", "#00BCD4")
                    );

                case "MepAutoUnifyCommand":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M1.5,8 H3.5 V16 H1.5 Z M20.5,8 H22.5 V16 H20.5 Z", "#01579B"),
                        ("M3.5,9 H10 V15 H3.5 Z M14,9 H20.5 V15 H14 Z", "#0288D1"),
                        ("M4,10.5 H9.5 V11.8 H4 Z M14.5,10.5 H20 V11.8 H14.5 Z", "#81D4FA"),
                        ("M9.5,8 H14.5 V16 H9.5 Z", "#FFA000"),
                        ("M11.3,7.5 H12.7 V16.5 H11.3 Z", "#FF6D00"),
                        ("M10.5,9.8 H13.5 V14.2 H10.5 Z", "#FFE082"),
                        ("M5,4.5 H7.5 L9.5,6 L7.5,7.5 H5 L6.5,6 Z M19,4.5 H16.5 L14.5,6 L16.5,7.5 H19 L17.5,6 Z", "#00C853")
                    );

                case "MepAutoCrossCommand":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M1.5,8 H3.5 V16 H1.5 Z M20.5,8 H22.5 V16 H20.5 Z M8,1.5 H16 V3.5 H8 Z M8,20.5 H16 V22.5 H8 Z", "#01579B"),
                        ("M3.5,9 H8 Q9,9 9,8 V3.5 H15 V8 Q15,9 16,9 H20.5 V15 H16 Q15,15 15,16 V20.5 H9 V16 Q9,15 8,15 H3.5 Z", "#0288D1"),
                        ("M4,10.5 H20 V11.8 H4 Z M10.5,4 H11.8 V20 H10.5 Z", "#81D4FA"),
                        ("M12,9.5 A2.5,2.5 0 1,1 11.99,9.5 Z", "#00BCD4"),
                        ("M12,10.8 A1.2,1.2 0 1,1 11.99,10.8 Z", "#FFFFFF")
                    );

                case "MepAutoConnectCommand":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M1,10 H3 V14 H1 Z M8,10 H10 V14 H8 Z", "#01579B"),
                        ("M3,10.5 H8 V13.5 H3 Z", "#0288D1"),
                        ("M14,2 H16 V6 H14 Z M18,8 H22 V10 H18 Z", "#01579B"),
                        ("M16,2.5 Q20,2.5 20,8 H18 Q18,4.5 16,4.5 Z", "#0288D1"),
                        ("M11,16 H13 V22 H11 Z M7,18 H17 V20 H7 Z", "#0288D1"),
                        ("M4,11.2 H8 V12 H4 Z M11.5,17 H12.5 V21 H11.5 Z", "#81D4FA"),
                        ("M16.5,3 L18.2,6.2 L21.8,6.6 L19,8.8 L19.8,12.2 L16.5,10.3 L13.2,12.2 L14,8.8 L11.2,6.6 L14.8,6.2 Z", "#FFA000"),
                        ("M16.5,5.2 L17.3,6.8 L19,7 L17.7,8.1 L18.1,9.7 L16.5,8.8 L14.9,9.7 L15.3,8.1 L14,7 L15.7,6.8 Z", "#FFE082")
                    );

                case "MepFittingsDropdown":
                case "Fittings":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        // 1. Top-Left: 90° Elbow Fitting (Blue)
                        ("M1.5,2 H4 V8 H1.5 Z M6.5,9 H12 V11.5 H6.5 Z", "#01579B"),
                        ("M4,2.8 H7.5 A3.5,3.5 0 0,1 11,6.3 V9 H7.5 A1.5,1.5 0 0,0 6,7.5 H4 Z", "#0288D1"),
                        ("M4,4.2 H7.2 A2.3,2.3 0 0,1 9.5,6.5 V8.2 H8.5 A1.2,1.2 0 0,0 7.2,7.1 H4 Z", "#81D4FA"),

                        // 2. Top-Right: 45° Angled Elbow Fitting (Vibrant Amber/Orange)
                        ("M12.5,9 H17 V11.5 H12.5 Z", "#E65100"),
                        ("M18,1 L22.5,5.5 L21,7 L16.5,2.5 Z", "#E65100"),
                        ("M13.5,9 L14,7 L17.5,3.5 L20,6 L16.5,9.5 H13.5 Z", "#FFA000"),
                        ("M14.5,7.8 L17.5,4.8 L18.8,6.1 L15.8,9 H14.5 Z", "#FFE082"),

                        // 3. Bottom: Full MEP Tee Fitting (Blue)
                        ("M1.5,13.5 H4 V20 H1.5 Z M20,13.5 H22.5 V20 H20 Z M8.5,21.5 H15.5 V24 H8.5 Z", "#01579B"),
                        ("M4,14.5 H20 V19 H14.5 V22 H9.5 V19 H4 Z", "#0288D1"),
                        ("M4,15.5 H20 V16.8 H4 Z", "#81D4FA"),
                        ("M11,16.8 H12.5 V21.8 H11 Z", "#81D4FA")
                    );

                case "MepPullCommand":
                case "PullCommand":
                case "Pull":
                case "MepBloomCommand":
                case "BloomCommand":
                case "Bloom":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M1.5,3 H8 V21 H1.5 Z", "#37474F"),
                        ("M2.5,4.5 H7 V19.5 H2.5 Z", "#455A64"),
                        ("M4.2,6 H5.3 V18 H4.2 Z", "#00E5FF"),
                        ("M8,5 H9.5 V10 H8 Z M8,14 H9.5 V19 H8 Z", "#FFA000"),
                        ("M9.5,5.5 H16.5 V9.5 H9.5 Z", "#0288D1"),
                        ("M9.5,6.5 H16 V7.5 H9.5 Z", "#81D4FA"),
                        ("M16.5,5 H18 V10 H16.5 Z", "#01579B"),
                        ("M19,6 V9 H20.5 L20.5,10.5 L23.5,7.5 L20.5,4.5 L20.5,6 Z", "#00C853"),
                        ("M9.5,14.5 H16.5 V18.5 H9.5 Z", "#00897B"),
                        ("M9.5,15.5 H16 V16.5 H9.5 Z", "#80CBC4"),
                        ("M16.5,14 H18 V19 H16.5 Z", "#004D40"),
                        ("M19,15 V18 H20.5 L20.5,19.5 L23.5,16.5 L20.5,13.5 L20.5,15 Z", "#00C853")
                    );

                case "MepElbowUpCommand":
                case "ElbowUpCommand":
                case "ElbowUp":
                case "Elbow Up":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M1.5,15.5 H3.5 V22.5 H1.5 Z M9,2 H15.5 V4 H9 Z", "#01579B"),
                        ("M3.5,17 H8.5 A1.5,1.5 0 0,0 10,15.5 V4 H15 V15.5 A6.5,6.5 0 0,1 8.5,22 H3.5 Z", "#0288D1"),
                        ("M4,18 H8.5 A2.5,2.5 0 0,0 11,15.5 V4.5 H12 V15.5 A5,5 0 0,1 8.5,20.5 H4 Z", "#81D4FA"),
                        ("M19,3 L23.5,7.5 H20.5 V15 H17.5 V7.5 H14.5 Z", "#00C853")
                    );

                case "MepElbowDownCommand":
                case "ElbowDownCommand":
                case "ElbowDown":
                case "Elbow Down":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M1.5,1.5 H3.5 V8.5 H1.5 Z M9,20 H15.5 V22 H9 Z", "#01579B"),
                        ("M3.5,7 H8.5 A1.5,1.5 0 0,1 10,8.5 V20 H15 V8.5 A6.5,6.5 0 0,0 8.5,2 H3.5 Z", "#0288D1"),
                        ("M4,6 H8.5 A2.5,2.5 0 0,1 11,8.5 V19.5 H12 V8.5 A5,5 0 0,0 8.5,3.5 H4 Z", "#81D4FA"),
                        ("M19,21 L23.5,16.5 H20.5 V9 H17.5 V16.5 H14.5 Z", "#00C853")
                    );

                case "MepElbowLeftCommand":
                case "ElbowLeftCommand":
                case "ElbowLeft":
                case "Elbow Left":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M15.5,20.5 H22.5 V22.5 H15.5 Z M2,9 H4 V15.5 H2 Z", "#01579B"),
                        ("M17,20.5 V15.5 A1.5,1.5 0 0,0 15.5,14 H4 V9 H15.5 A6.5,6.5 0 0,1 22,15.5 V20.5 Z", "#0288D1"),
                        ("M18,20 V15.5 A2.5,2.5 0 0,0 15.5,13 H4.5 V12 H15.5 A5,5 0 0,1 20.5,15.5 V20 Z", "#81D4FA"),
                        ("M3,5 L7.5,0.5 V3.5 H15 V6.5 H7.5 V9.5 Z", "#FF9800")
                    );

                case "MepElbowRightCommand":
                case "ElbowRightCommand":
                case "ElbowRight":
                case "Elbow Right":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M1.5,20.5 H8.5 V22.5 H1.5 Z M20,9 H22 V15.5 H20 Z", "#01579B"),
                        ("M7,20.5 V15.5 A1.5,1.5 0 0,1 8.5,14 H20 V9 H8.5 A6.5,6.5 0 0,0 2,15.5 V20.5 Z", "#0288D1"),
                        ("M6,20 V15.5 A2.5,2.5 0 0,1 8.5,13 H19.5 V12 H8.5 A5,5 0 0,0 3.5,15.5 V20 Z", "#81D4FA"),
                        ("M21,5 L16.5,0.5 V3.5 H9 V6.5 H16.5 V9.5 Z", "#FF9800")
                    );

                case "MepElbowUp45Command":
                case "ElbowUp45Command":
                case "ElbowUp45":
                case "Elbow Up 45":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M1.5,14 H3.5 V21 H1.5 Z", "#01579B"),
                        ("M3.5,15 H7.5 L16.5,6 L20,9.5 L11,18.5 L7.5,20 H3.5 Z", "#0288D1"),
                        ("M4,16 H7.5 L16.5,7 L17.5,8 L10.5,15 H4 Z", "#81D4FA"),
                        ("M16.5,6 L20,9.5 L21.5,8 L18,4.5 Z", "#01579B"),
                        ("M17.5,5.5 L20.2,8.2 L19.4,9 L16.7,6.3 Z", "#37474F"),
                        ("M8,14.5 L9.5,13 L11.5,15 L10,16.5 Z", "#FFA000"),
                        ("M7,2 H13 V8 L10.8,5.8 L4.8,11.8 L3.2,10.2 L9.2,4.2 Z", "#0288D1")
                    );

                case "MepElbowDown45Command":
                case "ElbowDown45Command":
                case "ElbowDown45":
                case "Elbow Down 45":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M1.5,3 H3.5 V10 H1.5 Z", "#01579B"),
                        ("M3.5,9 H7.5 L16.5,18 L20,14.5 L11,5.5 L7.5,4 H3.5 Z", "#0288D1"),
                        ("M4,8 H7.5 L16.5,17 L17.5,16 L10.5,9 H4 Z", "#81D4FA"),
                        ("M16.5,18 L20,14.5 L21.5,16 L18,19.5 Z", "#01579B"),
                        ("M17.5,18.5 L20.2,15.8 L19.4,15 L16.7,17.7 Z", "#37474F"),
                        ("M8,9.5 L9.5,11 L11.5,9 L10,7.5 Z", "#FFA000"),
                        ("M7,22 H13 V16 L10.8,18.2 L4.8,12.2 L3.2,13.8 L9.2,19.8 Z", "#0288D1")
                    );

                case "MepElbowLeft45Command":
                case "ElbowLeft45Command":
                case "ElbowLeft45":
                case "Elbow Left 45":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M14,20.5 H21 V22.5 H14 Z", "#01579B"),
                        ("M15,20.5 V16.5 L6,7.5 L9.5,4 L18.5,13 L20,16.5 V20.5 Z", "#0288D1"),
                        ("M16,20 V16.5 L7,7.5 L8,6.5 L15,13.5 V20 Z", "#81D4FA"),
                        ("M6,7.5 L9.5,4 L8,2.5 L4.5,6 Z", "#01579B"),
                        ("M5.5,6.5 L8.2,3.8 L9,4.6 L6.3,7.3 Z", "#37474F"),
                        ("M14.5,16 L13,14.5 L15,12.5 L16.5,14 Z", "#FFA000"),
                        ("M2,7 V13 H8 L5.8,10.8 L11.8,4.8 L10.2,3.2 L4.2,9.2 Z", "#FF9800")
                    );

                case "MepElbowRight45Command":
                case "ElbowRight45Command":
                case "ElbowRight45":
                case "Elbow Right 45":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M3,20.5 H10 V22.5 H3 Z", "#01579B"),
                        ("M9,20.5 V16.5 L18,7.5 L14.5,4 L5.5,13 L4,16.5 V20.5 Z", "#0288D1"),
                        ("M8,20 V16.5 L17,7.5 L16,6.5 L9,13.5 V20 Z", "#81D4FA"),
                        ("M18,7.5 L14.5,4 L16,2.5 L19.5,6 Z", "#01579B"),
                        ("M18.5,6.5 L15.8,3.8 L15,4.6 L17.7,7.3 Z", "#37474F"),
                        ("M9.5,16 L11,14.5 L9,12.5 L7.5,14 Z", "#FFA000"),
                        ("M22,7 V13 H16 L18.2,10.8 L12.2,4.8 L13.8,3.2 L19.8,9.2 Z", "#FF9800")
                    );
                case "MepElbowCustomCommand":
                case "ElbowCustomCommand":
                case "ElbowCustom":
                case "Elbow Custom":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M3,20.5 H10 V22.5 H3 Z", "#01579B"),
                        ("M9,20.5 V16.5 L18,7.5 L14.5,4 L5.5,13 L4,16.5 V20.5 Z", "#0288D1"),
                        ("M8,20 V16.5 L17,7.5 L16,6.5 L9,13.5 V20 Z", "#81D4FA"),
                        ("M18,7.5 L14.5,4 L16,2.5 L19.5,6 Z", "#01579B"),
                        ("M18.5,6.5 L15.8,3.8 L15,4.6 L17.7,7.3 Z", "#37474F"),
                        ("M9.5,16 L11,14.5 L9,12.5 L7.5,14 Z", "#FFA000"),
                        ("M22,7 V13 H16 L18.2,10.8 L12.2,4.8 L13.8,3.2 L19.8,9.2 Z", "#FF9800"),
                        ("M2,7 V13 H8 L5.8,10.8 L11.8,4.8 L10.2,3.2 L4.2,9.2 Z", "#FF9800")
                    );

                case "MepStretchCommand":
                case "StretchCommand":
                case "Stretch":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M2.5,18.5 L8,6.5 L21.5,6.5 L16,18.5 Z", "#455A64"),
                        ("M4,17.2 L8.6,8 L20,8 L15.4,17.2 Z", "#546E7A"),
                        ("M7,12.2 H10 V21 H7 Z", "#0288D1"),
                        ("M7,13.4 H10 V14.5 H7 Z", "#81D4FA"),
                        ("M6.4,11.4 H10.6 V13 H6.4 Z", "#01579B"),
                        ("M10,11.4 H16.2 V15.6 H10 Z", "#0288D1"),
                        ("M10,12.6 H16.2 V13.7 H10 Z", "#81D4FA"),
                        ("M16.2,12.2 H19.2 V21 H16.2 Z", "#0288D1"),
                        ("M16.2,13.4 H19.2 V14.5 H16.2 Z", "#81D4FA"),
                        ("M15.6,11.4 H19.8 V13 H15.6 Z", "#01579B"),
                        ("M10,12.8 V14.4 H8.4 L8.4,15.8 L5,13.6 L8.4,11.4 L8.4,12.8 Z", "#00C853"),
                        ("M16.2,12.8 V14.4 H17.8 L17.8,15.8 L21.2,13.6 L17.8,11.4 L17.8,12.8 Z", "#00C853")
                    );

                case "MepIncrementCommand":
                case "IncrementCommand":
                case "Increment":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M8,9.2 H16 V14.8 H8 Z", "#0288D1"),
                        ("M8,10.5 H16 V11.7 H8 Z", "#81D4FA"),
                        ("M7,8.7 H8.3 V15.3 H7 Z M15.7,8.7 H17 V15.3 H15.7 Z", "#01579B"),
                        ("M7,10 V14 H5 L5,16.8 L0.3,12 L5,7.2 L5,10 Z", "#00C853"),
                        ("M17,10 V14 H19 L19,16.8 L23.7,12 L19,7.2 L19,10 Z", "#00C853")
                    );

                case "MepSettingsCommand":
                case "MepSettings":
                case "Settings":
                case "FitterSettingsCommand":
                    return CreateCompositeVectorIcon(
                        ("M0,0 H24 V24 H0 Z", "#00000000"),
                        ("M19.43,12.98 C19.47,12.66 19.5,12.34 19.5,12 C19.5,11.66 19.47,11.34 19.43,11.02 L21.54,9.37 C21.73,9.22 21.78,8.95 21.66,8.73 L19.66,5.27 C19.54,5.05 19.27,4.97 19.05,5.05 L16.56,6.05 C16.04,5.65 15.48,5.32 14.87,5.07 L14.49,2.42 C14.46,2.18 14.25,2 14,2 L10,2 C9.75,2 9.54,2.18 9.51,2.42 L9.13,5.07 C8.52,5.32 7.96,5.66 7.44,6.05 L4.95,5.05 C4.73,4.96 4.46,5.05 4.34,5.27 L2.34,8.73 C2.21,8.95 2.27,9.22 2.46,9.37 L4.57,11.02 C4.53,11.34 4.5,11.67 4.5,12 C4.5,12.33 4.53,12.66 4.57,12.98 L2.46,14.63 C2.27,14.78 2.21,15.05 2.34,15.27 L4.34,18.73 C4.46,18.95 4.73,19.03 4.95,18.95 L7.44,17.95 C7.96,18.35 8.52,18.68 9.13,18.93 L9.51,21.58 C9.54,21.82 9.75,22 10,22 L14,22 C14.25,22 14.46,21.82 14.49,21.58 L14.87,18.93 C15.48,18.68 16.04,18.34 16.56,17.95 L19.05,18.95 C19.27,19.04 19.54,18.95 19.66,18.73 L21.66,15.27 C21.78,15.05 21.73,14.78 21.54,14.63 L19.43,12.98 Z", "#455A64"),
                        ("M12,16 C9.79,16 8,14.21 8,12 C8,9.79 9.79,8 12,8 C14.21,8 16,9.79 16,12 C16,14.21 14.21,16 12,16 Z", "#0288D1"),
                        ("M12,14 C10.9,14 10,13.1 10,12 C10,10.9 10.9,10 12,10 C13.1,10 14,10.9 14,12 C14,13.1 13.1,14 12,14 Z", "#FFA000"),
                        ("M12,12.8 C11.56,12.8 11.2,12.44 11.2,12 C11.2,11.56 11.56,11.2 12,11.2 C12.44,11.2 12.8,11.56 12.8,12 C12.8,12.44 12.44,12.8 12,12.8 Z", "#FFE082")
                    );
                // Extras / Utilities
                case "PointCloudSizeCommand":
                    return CreateVectorIcon("M19.35,10.04C18.67,6.59 15.64,4 12,4C9.11,4 6.6,5.64 5.35,8.04C2.34,8.36 0,10.91 0,14A6,6 0 0,0 6,20H19A5,5 0 0,0 24,15C24,12.36 21.95,10.22 19.35,10.04M19,18H6A4,4 0 0,1 2,14C2,11.95 3.53,10.24 5.56,10.03L6.63,9.92L7.13,8.97C8.08,7.14 9.94,6 12,6C14.62,6 16.88,7.86 17.39,10.43L17.69,11.93L19.22,12.04C20.78,12.14 22,13.45 22,15A3,3 0 0,1 19,18Z", "#00ACC1");

                default:
                    return null;
            }
        }

        /// <summary>
        /// Creates a frozen, thread-safe vector DrawingImage from SVG path data and hex color.
        /// </summary>
        public static ImageSource CreateVectorIcon(string pathData, string colorHex)
        {
            try
            {
                var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(colorHex);
                if (brush != null && brush.CanFreeze)
                {
                    brush.Freeze();
                }

                var geometry = Geometry.Parse(pathData);
                if (geometry.CanFreeze)
                {
                    geometry.Freeze();
                }

                var drawingGroup = new DrawingGroup();
                drawingGroup.Children.Add(new GeometryDrawing(brush, null, geometry));

                var drawingImage = new DrawingImage(drawingGroup);
                drawingImage.Freeze();
                return drawingImage;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Creates a multi-layer vector DrawingImage from multiple SVG path and color pairs.
        /// </summary>
        public static ImageSource CreateCompositeVectorIcon(params (string pathData, string colorHex)[] parts)
        {
            try
            {
                var drawingGroup = new DrawingGroup();
                foreach (var part in parts)
                {
                    var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(part.colorHex);
                    if (brush != null && brush.CanFreeze)
                    {
                        brush.Freeze();
                    }

                    var geometry = Geometry.Parse(part.pathData);
                    if (geometry.CanFreeze)
                    {
                        geometry.Freeze();
                    }

                    drawingGroup.Children.Add(new GeometryDrawing(brush, null, geometry));
                }

                var drawingImage = new DrawingImage(drawingGroup);
                drawingImage.Freeze();
                return drawingImage;
            }
            catch
            {
                return null;
            }
        }
    }
}