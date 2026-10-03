# Biruscan — Build & Installation Guide

## Overview

**Biruscan** is a C# Revit add-in that replicates the core functionality of CloudWorx for Revit 1.0.2 for working with RCP/RCS point cloud data inside Autodesk Revit 2025.

## Tools Included (Matching CloudWorx for Revit 1.0.2)

### Project Panel
| Tool | Description |
|------|-------------|
| Toggle Visibility | Show or hide the loaded point cloud in the current view |
| Cloud Size [Medium] | Cycle point cloud display size: High → Medium → Smallest (click to change) |

### Clipping Panel
| Tool | Description |
|------|-------------|
| Slice | Cut the point cloud along X, Y, or Z axis (2-point pick). Cuts the point cloud itself, not just the view. |
| Quick Slice | Quickly cut the point cloud at a picked Z elevation |
| Rectangular Cut | Cut the point cloud with a rectangle (2 diagonal corners), full Z height |
| Polygonal Cut | Cut the point cloud with a polygon (N vertices, min 3), full Z height |
| Clipping Manager | Manage all cuts — create, apply, delete, or reset |
| Limit Box | Cut the point cloud with a 3D limit box (2 corner picks) |

### View Panel
| Tool | Description |
|------|-------------|
| Rendering Options | Set point cloud rendering mode (True Color, Intensity, Elevation, Heat Map, Normals) |
| Section Box | Enable, disable, or reset the section box around the point cloud |

### Floor Plan Panel
| Tool | Description |
|------|-------------|
| Floor Plan Tool | Create a floor plan view from the point cloud at a picked elevation |

### Fitter Tools Panel
| Tool | Description |
|------|-------------|
| Wall Fitter | Fit a Revit wall to point cloud data (2-point or Region Grow mode) |
| Pipe Fitter | Fit a Revit pipe between two points on the point cloud |
| Steel Fitter | Fit structural steel framing between two points |
| HVAC Fitter | Fit ductwork between two points on the point cloud |
| Window Fitter | Fit a window into a wall at a picked point |
| Cable Tray Fitter | Fit cable tray between two points on the point cloud |

---

## Rendering Modes

| Mode | Description |
|------|-------------|
| True Color | Display point cloud in original scan colors |
| Intensity | Grayscale based on scan reflectivity intensity |
| Elevation | Color-code points by Z value (height) |
| **Heat Map** | **Blue→Cyan→Green→Yellow→Red gradient based on elevation, with adjustable Min/Max height range** |
| Normals | Color by surface normal direction |

### Heat Map Mode
The Heat Map rendering mode displays the point cloud with a temperature-style gradient:
- **Blue** = Lowest elevation (Min Height)
- **Cyan → Green → Yellow** = Mid-range elevations
- **Red** = Highest elevation (Max Height)

You can adjust the **Min Height** and **Max Height** values to focus the gradient on a specific range. For example, if you only care about elevations between 10ft and 25ft, set Min Height to 10 and Max Height to 25 to see maximum color variation in that range.

---

## Cloud Size Button

The **Cloud Size** button cycles through three display density levels each time you click it:

| Size | Detail Level | Point Scale | Description |
|------|-------------|-------------|-------------|
| **High** | Fine | 1x (Full) | All points visible, maximum detail |
| **Medium** | Medium | 2x (Reduced) | Moderate point density, faster performance |
| **Smallest** | Coarse | 5x (Sparse) | Minimal points, best overview performance |

The button text updates to show the current size level (e.g., `Cloud Size [High]`). Click again to cycle to the next size.

---

## Prerequisites

1. **Visual Studio 2022** (Community edition or higher)
2. **.NET Framework 4.8** SDK
3. **Autodesk Revit 2025** installed (for API DLL references)
4. **WPF** workloads for Visual Studio

---

## Build Instructions

### Step 1: Copy Project Files

Copy the entire `Biruscan` folder to your local development machine, e.g.:
```
C:\RevitPlugins\Biruscan\
```

### Step 2: Update Revit API References

Open `Biruscan.csproj` and update the `RevitInstallDir` property to match your Revit installation:

```xml
<RevitInstallDir>C:\Program Files\Autodesk\Revit 2025\</RevitInstallDir>
```

### Step 3: Open in Visual Studio

1. Open Visual Studio 2022
2. File → Open → Project/Solution
3. Navigate to `Biruscan.csproj`
4. Wait for NuGet restore and project loading

### Step 4: Build the Project

1. Set configuration to **Release | x64**
2. Build → Build Solution (Ctrl+Shift+B)
3. The output DLL and .addin file will be automatically copied to:
   ```
   %APPDATA%\Autodesk\Revit\Addins\2025\
   ```

### Step 5: Verify Installation

1. Open Revit 2025
2. The **"Biruscan"** tab should appear in the ribbon
3. If the tab does not appear, check the Revit journal file for errors

---

## Manual Installation (Alternative)

If the auto-copy doesn't work, manually copy:

1. Copy `Biruscan.dll` from `bin\Release\` to:
   ```
   %APPDATA%\Autodesk\Revit\Addins\2025\
   ```

2. Copy `Biruscan.addin` to:
   ```
   %APPDATA%\Autodesk\Revit\Addins\2025\
   ```

3. Edit the `.addin` file and verify the `<Assembly>` path points to the DLL.

---

## Usage Workflow

### Quick Start — Loading and Viewing a Point Cloud

1. Open a Revit 2025 project
2. Load your RCP/RCS point cloud using Revit's native Insert → Point Cloud
3. Go to the **Biruscan** tab
4. Use **Rendering Options** → Heat Map to visualize elevation with color gradient
5. Use **Cloud Size** button to adjust point density (click to cycle: High → Medium → Smallest)
6. Switch to a 3D view and use **Quick Slice** to cut the cloud at a floor elevation
7. Use **Rectangular Cut** or **Polygonal Cut** to clip the cloud to a specific region
8. Use **Floor Plan Tool** to create a plan view at that level
9. Use **Wall Fitter** or other fitter tools to model from the cloud

### Typical Modeling Workflow

```
Load RCP/RCS (native Revit) → Heat Map Rendering → Cloud Size → QuickSlice → Floor Plan → Fitter Tools
```

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Tab doesn't appear in Revit | Check .addin file path; ensure DLL and .addin are in the same Addins folder |
| Wall/Pipe Fitter fails | Ensure you have the corresponding family types loaded in the Revit project (basic walls, pipe types, etc.) |
| Section Box not working | Switch to a 3D view first; Section Box only works in 3D views |
| Clipping Manager is empty | Create clips using Slice or QuickSlice first; they appear in the manager |
| Rendering mode doesn't change | Some RCP/RCS files may not contain intensity or normal data |
| Heat Map shows solid color | Adjust the Min/Max Height range to match your point cloud's actual elevation range |
| Cloud Size doesn't change | Verify the view is active; the detail level change applies to the current view |

---

## Known Limitations

1. **No LGSx support** — Only RCP/RCS files are supported (Revit native format)
2. **No point cloud streaming** — Only local RCP/RCS files are supported (no remote streaming servers)
3. **Rendering limited** — Revit's point cloud rendering API is more limited than CloudWorx's custom rendering engine
4. **Auto-fitting is basic** — The fitter tools use point-picking rather than advanced point cloud analysis (RANSAC, region growing, etc.)
5. **Performance** — Large point clouds may be slower without the optimized rendering engine that CloudWorx uses
6. **No clash detection** — Clash Marker and clash resolution features are not included

---

## Project File Structure

```
Biruscan/
├── Biruscan.csproj                     # MSBuild project file
├── Biruscan.addin                      # Revit add-in manifest
├── App.cs                              # Main entry point & ribbon setup
├── Properties/
│   └── AssemblyInfo.cs                 # Assembly metadata
├── Commands/
│   ├── SliceCommand.cs                 # Slice through point cloud
│   ├── QuickSliceCommand.cs            # Quick slice at elevation
│   ├── ClippingManagerCommand.cs       # Clipping Manager dialog
│   ├── LimitBoxCommand.cs              # Limit Box definition
│   ├── FloorPlanToolCommand.cs         # Floor Plan creation
│   ├── WallFitterCommand.cs            # Wall fitting
│   ├── PipeFitterCommand.cs            # Pipe fitting
│   ├── SteelFitterCommand.cs           # Steel fitting
│   ├── HVACFitterCommand.cs            # HVAC/duct fitting
│   ├── WindowFitterCommand.cs          # Window fitting
│   ├── CableTrayFitterCommand.cs       # Cable tray fitting
│   ├── RectangularCutCommand.cs       # Rectangular cut (2-corner pick)
│   ├── PolygonalCutCommand.cs          # Polygonal cut (multi-point pick)
│   ├── RenderingCommand.cs             # Rendering mode control (incl. Heat Map)
│   ├── VisibilityToggleCommand.cs      # Toggle visibility
│   ├── SectionBoxCommand.cs            # Section box control
│   └── PointCloudSizeCommand.cs        # Cloud size cycling (High/Medium/Smallest)
├── Services/
│   ├── PointCloudService.cs            # Point cloud loading & management
│   ├── ClippingService.cs              # Clipping/slicing logic
│   ├── FitterService.cs                # Element fitting logic
│   └── ViewService.cs                  # View manipulation
├── Models/
│   ├── ClippingRecord.cs               # Clipping record data model
│   ├── SlicePlane.cs                   # Slice plane definition
│   ├── FitterResult.cs                 # Fitter operation result
│   └── PointCloudInfo.cs               # Point cloud metadata
├── UI/
│   ├── RibbonHelper.cs                 # Ribbon creation helper
│   └── Views/
│       ├── ClippingManagerWindow.xaml/.cs   # Clipping Manager dialog
│       ├── FloorPlanDialog.xaml/.cs         # Floor Plan creation dialog
│       ├── RenderingOptionsWindow.xaml/.cs  # Rendering options dialog (incl. Heat Map)
│       └── LimitBoxWindow.xaml/.cs          # Limit Box definition dialog
└── Resources/
    └── Icons/                          # Button icons (optional PNGs)
```

---

## License

This project is provided as-is for educational and personal use. It is not affiliated with or endorsed by Leica Geosystems, Hexagon, or Autodesk. The CloudWorx name and related trademarks belong to Leica Geosystems / Hexagon.
