using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Biruscan.Commands
{
    /// <summary>
    /// Toggles point cloud visibility in the active view — for BOTH directly-loaded clouds
    /// and clouds inside linked Revit models — WITHOUT hiding the rest of a link.
    ///
    /// Direct clouds are toggled via the host Point Clouds category. For linked clouds we
    /// set each cloud-bearing link's display to "By Host View" so it follows the host's
    /// category visibility — then hiding the host Point Clouds category hides only the
    /// link's cloud, leaving its model geometry visible. No dialogs are shown.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class VisibilityToggleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                View view = doc.ActiveView;
                Category pcCat = Category.GetCategory(doc, BuiltInCategory.OST_PointClouds);

                // Host Point Clouds category is the master on/off state — flip it.
                bool currentlyHidden = pcCat != null && view.GetCategoryHidden(pcCat.Id);
                bool newHidden = !currentlyHidden;

                // Revit-link instances whose linked model contains a point cloud.
                var cloudLinks = new List<ElementId>();
                foreach (RevitLinkInstance link in new FilteredElementCollector(doc)
                             .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    Document ld = link.GetLinkDocument(); // null when the link is unloaded
                    if (ld == null) continue;
                    if (new FilteredElementCollector(ld).OfClass(typeof(PointCloudInstance)).Any())
                        cloudLinks.Add(link.Id);
                }

                using (Transaction t = new Transaction(doc, "Toggle Point Cloud Visibility"))
                {
                    t.Start();

                    // Directly-loaded clouds.
                    if (pcCat != null && view.CanCategoryBeHidden(pcCat.Id))
                        view.SetCategoryHidden(pcCat.Id, newHidden);

                    // Linked clouds: follow the host view (so only the cloud category is hidden,
                    // not the whole link) and undo any earlier whole-link hide.
                    if (cloudLinks.Count > 0)
                    {
                        try { view.UnhideElements(cloudLinks); } catch { }

                        foreach (var id in cloudLinks)
                        {
                            try
                            {
                                RevitLinkGraphicsSettings s = view.GetLinkOverrides(id) ?? new RevitLinkGraphicsSettings();
                                s.LinkVisibilityType = LinkVisibility.ByHostView;
                                view.SetLinkOverrides(id, s);
                            }
                            catch { }
                        }
                    }

                    t.Commit();
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Visibility Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
