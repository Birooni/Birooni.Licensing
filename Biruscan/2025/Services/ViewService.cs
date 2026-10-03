using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Biruscan.Services
{
    /// <summary>
    /// Service for manipulating Revit views — section boxes, view ranges,
    /// floor plan creation, and rendering settings.
    /// </summary>
    public class ViewService
    {
        private readonly Document _doc;

        public ViewService(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Creates a floor plan view at the specified elevation using the point cloud bounding box.
        /// </summary>
        public ViewPlan CreateFloorPlanView(double elevation, string viewName, Level level)
        {
            using (Transaction t = new Transaction(_doc, "Create Floor Plan View"))
            {
                t.Start();

                ViewFamilyType vft = new FilteredElementCollector(_doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan);

                if (vft == null)
                {
                    t.RollBack();
                    return null;
                }

                // Use the level if its elevation matches, otherwise find or create one
                Level targetLevel = FindOrCreateLevel(elevation, viewName);

                ViewPlan floorPlan = ViewPlan.Create(_doc, vft.Id, targetLevel.Id);

                if (floorPlan != null)
                {
                    floorPlan.Name = viewName;

                    // Adjust view range to show a thin slice at this elevation
                    SetViewRange(floorPlan, elevation - 2.0, elevation + 8.0, targetLevel);
                }

                t.Commit();
                return floorPlan;
            }
        }

        /// <summary>
        /// Finds an existing level at the given elevation, or creates a new one.
        /// </summary>
        private Level FindOrCreateLevel(double elevation, string levelName)
        {
            // Check for existing level at this elevation (within tolerance)
            Level existingLevel = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => Math.Abs(l.Elevation - elevation) < 0.01);

            if (existingLevel != null)
                return existingLevel;

            // Create new level
            Level newLevel = Level.Create(_doc, elevation);
            if (newLevel != null)
            {
                try { newLevel.Name = levelName; } catch { }
            }

            return newLevel;
        }

        /// <summary>
        /// Sets the view range for a plan view to show a slice at a specific elevation.
        /// </summary>
        private void SetViewRange(ViewPlan view, double bottomOffset, double topOffset, Level level)
        {
            try
            {
                PlanViewRange viewRange = view.GetViewRange();

                // Set the bottom clip plane
                viewRange.SetLevelId(PlanViewPlane.BottomClipPlane, level.Id);
                viewRange.SetOffset(PlanViewPlane.BottomClipPlane, bottomOffset - level.Elevation);

                // Set the top clip plane
                viewRange.SetLevelId(PlanViewPlane.TopClipPlane, level.Id);
                viewRange.SetOffset(PlanViewPlane.TopClipPlane, topOffset - level.Elevation);

                // Set the view depth
                viewRange.SetLevelId(PlanViewPlane.ViewDepthPlane, level.Id);
                viewRange.SetOffset(PlanViewPlane.ViewDepthPlane, bottomOffset - level.Elevation);

                // Set cut plane at mid-height
                viewRange.SetLevelId(PlanViewPlane.CutPlane, level.Id);
                viewRange.SetOffset(PlanViewPlane.CutPlane, (topOffset + bottomOffset) / 2.0 - level.Elevation);

                view.SetViewRange(viewRange);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SetViewRange error: " + ex.Message);
            }
        }

        /// <summary>
        /// Enables and configures the section box for a 3D view.
        /// </summary>
        public void EnableSectionBox(View3D view, BoundingBoxXYZ bounds)
        {
            if (view == null) return;

            using (Transaction t = new Transaction(_doc, "Enable Section Box"))
            {
                t.Start();
                view.IsSectionBoxActive = true;
                view.SetSectionBox(bounds);
                t.Commit();
            }
        }

        /// <summary>
        /// Disables the section box for a 3D view.
        /// </summary>
        public void DisableSectionBox(View3D view)
        {
            if (view == null) return;

            using (Transaction t = new Transaction(_doc, "Disable Section Box"))
            {
                t.Start();
                view.IsSectionBoxActive = false;
                t.Commit();
            }
        }

        /// <summary>
        /// Sets the section box to a thin slice at the given elevation.
        /// </summary>
        public void SetSectionBoxSlice(View3D view, double elevation, double thickness)
        {
            if (view == null) return;

            BoundingBoxXYZ currentBox = view.GetSectionBox();

            using (Transaction t = new Transaction(_doc, "Set Section Box Slice"))
            {
                t.Start();
                if (!view.IsSectionBoxActive)
                    view.IsSectionBoxActive = true;

                currentBox.Min = new XYZ(currentBox.Min.X, currentBox.Min.Y, elevation - thickness / 2.0);
                currentBox.Max = new XYZ(currentBox.Max.X, currentBox.Max.Y, elevation + thickness / 2.0);
                view.SetSectionBox(currentBox);

                t.Commit();
            }
        }

        /// <summary>
        /// Gets the default 3D view or creates one.
        /// </summary>
        public View3D GetOrCreate3DView()
        {
            // Try to find an existing 3D view
            View3D view3d = new FilteredElementCollector(_doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate);

            if (view3d != null)
                return view3d;

            // Create a new 3D view
            ViewFamilyType vft = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);

            if (vft == null) return null;

            return View3D.CreateIsometric(_doc, vft.Id);
        }

        /// <summary>
        /// Sets the detail level of a view.
        /// </summary>
        public void SetDetailLevel(View view, ViewDetailLevel detailLevel)
        {
            using (Transaction t = new Transaction(_doc, "Set Detail Level"))
            {
                t.Start();
                view.DetailLevel = detailLevel;
                t.Commit();
            }
        }
    }
}
