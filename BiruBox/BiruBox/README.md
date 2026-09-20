# BiruBox

Revit add-in. Auto-Section Box for Autodesk Revit — the COINS workflow, implemented on the Revit API.

**This is not a mobile or web app.** It is a C# `IExternalApplication` that loads into Revit and drives `View3D.SetSectionBox`.

## Commands (Birooni Tools / Modify → BiruBox)

| Command | API |
| --- | --- |
| **Auto-Section Box** | Builds a `BoundingBoxXYZ` from the selection and calls `View3D.CreateIsometric` + `SetSectionBox` (includes checkbox option to align to element) |
| **Draw Box** | Drag a rectangle in any 2D view (Floor Plan, Ceiling Plan, Section, Elevation); builds a 3D section box matching the drawn area and view range / depth |
| **Quick** | Same path with last settings. Overwrites the view named `3D - Quick` |
| **Toggle** | `view.IsSectionBoxActive = !view.IsSectionBoxActive` |
| **Grow / Shrink** | Expands or contracts `GetSectionBox()` Min/Max by the last buffer |

Selection is worry-free: tags, dimensions, and text are ignored. Linked elements (`RevitLinkInstance` + `Reference.LinkedElementId`) are transformed with `GetTotalTransform()`. Scope boxes, section crops, grids, and levels have dedicated extents. Section box alignment to walls, ducts, and line-based geometry can be enabled directly via the checkbox in the Auto-Section Box dialog.

## Requirements

- Autodesk Revit **2025 or 2026** (change `RevitVersion` in the csproj for 2024 / `net48`)
- .NET SDK 8
- Visual Studio 2022 or `dotnet build`

## Build

```bat
dotnet build BiruBox.csproj -c Release
```

Revit 2026:

```bat
dotnet build BiruBox.csproj -c Release -p:RevitVersion=2026
```

Revit 2024 (Framework):

```bat
dotnet build BiruBox.csproj -c Release -p:RevitVersion=2024
```

The csproj references `Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI` so you can compile without Revit installed. The DLL only **runs** inside Revit.

## Install

From `bin/Release` after a successful build:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Or copy manually:

1. `BiruBox.dll` and `BiruBox.addin`
2. into `%APPDATA%\Autodesk\Revit\Addins\2025\`
3. Restart Revit

## Keyboard shortcuts

In Revit: **View → User Interface → Keyboard Shortcuts**. Filter for `BiruBox` and bind e.g. `BB` to Auto-Section Box, `BQ` to Quick, `BT` to Toggle.

## Source map

- `App.cs` — ribbon (`IExternalApplication`)
- `Core/SectionBoxService.cs` — tight solid geometry + crop extraction + view create/update
- `Core/DrawBoxService.cs` — 2D drawn box to 3D section box (view range + depth)
- `Core/BoxMath.cs` — `BoundingBoxXYZ` corners / transforms
- `Core/AutoSectionSettings.cs` — settings model with independent X/Y/Z offsets and unit defaults
- `Core/SessionManager.cs` — session-specific document initialization tracker
- `Core/CategoryRules.cs` — annotation / datum filters
- `UI/AutoSectionDialog.cs` — WPF dialog (target view, X/Y/Z offsets, alignment, quick)
- `UI/BiruIcons.cs` — vector graphics engine for ribbon buttons and dialog icon
- `Commands/` — one `IExternalCommand` per ribbon button
