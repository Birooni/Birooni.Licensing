using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using Biruscan.Services;

namespace Biruscan.Commands
{
    /// <summary>
    /// Command to perform a polygonal cut on the point cloud.
    /// User picks multiple points (minimum 3) to define a polygon.
    /// The cut region extends through the full height (Z range) of the point cloud.
    /// Actually cuts the PointCloudInstance — persists across ALL views.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class PolygonalCutCommand : IExternalCommand
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
                    TaskDialog.Show("Polygonal Cut", "No point cloud found.\nPlease load a point cloud first.");
                    return Result.Cancelled;
                }

                List<XYZ> polygonPoints = new List<XYZ>();
                List<ElementId> tempLines = new List<ElementId>();
                ElementId tempClosingLineId = ElementId.InvalidElementId;
                SketchPlane tempSketchPlane = null;
                SketchPlane originalSketchPlane = uidoc.ActiveView.SketchPlane;
                GraphicsStyle previewStyle = null;
                Plane workPlane = null;

                // Build the work plane perpendicular to the view but THROUGH THE CLOUD CENTRE
                // (not view.Origin, which is the camera in 3D/section views). This keeps the
                // preview lines on/near the cloud so they're visible in every orientation,
                // not just Top.
                // GetPointCloudBoundingBox now returns a world AABB (Identity transform).
                BoundingBoxXYZ cloudBox = pcService.GetPointCloudBoundingBox();
                XYZ planeOrigin = uidoc.ActiveView.Origin;
                if (cloudBox != null)
                    planeOrigin = (cloudBox.Min + cloudBox.Max).Multiply(0.5);

                // Setup sketch plane + thick orange line style (real ModelCurves — no overlay).
                using (Transaction tSetup = new Transaction(doc, "Setup SketchPlane"))
                {
                    tSetup.Start();
                    workPlane = Plane.CreateByNormalAndOrigin(uidoc.ActiveView.ViewDirection, planeOrigin);
                    tempSketchPlane = SketchPlane.Create(doc, workPlane);
                    uidoc.ActiveView.SketchPlane = tempSketchPlane;
                    previewStyle = EnsureThickOrangeLineStyle(doc);
                    // Make sure Lines category is on in this view so the rubber-band is visible.
                    try
                    {
                        Category linesCat = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
                        if (linesCat != null)
                            uidoc.ActiveView.SetCategoryHidden(linesCat.Id, false);
                    }
                    catch { }
                    tSetup.Commit();
                }

                bool finish = false;
                using (var finishHook = new PolygonFinishHook())
                {
                try
                {
                    while (true)
                    {
                        XYZ pt = uidoc.Selection.PickPoint(ObjectSnapTypes.None,
                            $"Pick vertex {polygonPoints.Count + 1}  (Right-click or Enter = cut, Esc = cancel, min 3 points)");

                        // Keep every vertex on the work plane so ModelCurves always succeed.
                        if (workPlane != null)
                            pt = ProjectOntoPlane(workPlane, pt);

                        using (Transaction t = new Transaction(doc, "temp draw"))
                        {
                            t.Start();

                            if (polygonPoints.Count == 0)
                            {
                                polygonPoints.Add(pt);
                            }
                            else
                            {
                                polygonPoints.Add(pt);

                                // Edge from previous vertex → this pick (rubber-band segment).
                                XYZ prevPt = polygonPoints[polygonPoints.Count - 2];
                                if (prevPt.DistanceTo(pt) > 0.01) // Revit cannot draw very short lines
                                {
                                    ModelCurve mc = CreatePreviewEdge(doc, tempSketchPlane, prevPt, pt, previewStyle);
                                    if (mc != null) tempLines.Add(mc.Id);
                                }

                                // Closing edge last → first (updates as the polygon grows).
                                if (polygonPoints.Count >= 3)
                                {
                                    if (tempClosingLineId != ElementId.InvalidElementId)
                                        doc.Delete(tempClosingLineId);

                                    XYZ firstPt = polygonPoints[0];
                                    if (firstPt.DistanceTo(pt) > 0.01)
                                    {
                                        ModelCurve closingMc = CreatePreviewEdge(doc, tempSketchPlane, pt, firstPt, previewStyle);
                                        tempClosingLineId = closingMc != null ? closingMc.Id : ElementId.InvalidElementId;
                                    }
                                }
                            }
                            t.Commit();
                        }
                        uidoc.RefreshActiveView();
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    // PickPoint ended. A genuine Esc => cancel; right-click / Enter (captured
                    // by the input hook, which injects an Esc to stop PickPoint) => perform cut.
                    finish = finishHook.FinishRequested;
                }
                finally
                {
                    // Cleanup temp lines and sketch plane (null-safe if setup failed mid-way)
                    try
                    {
                        using (Transaction tCleanup = new Transaction(doc, "Cleanup temp drawing"))
                        {
                            tCleanup.Start();
                            foreach (var id in tempLines)
                            {
                                try { doc.Delete(id); } catch { }
                            }
                            if (tempClosingLineId != ElementId.InvalidElementId)
                            {
                                try { doc.Delete(tempClosingLineId); } catch { }
                            }

                            if (originalSketchPlane != null)
                            {
                                try { uidoc.ActiveView.SketchPlane = originalSketchPlane; } catch { }
                            }
                            if (tempSketchPlane != null)
                            {
                                try { doc.Delete(tempSketchPlane.Id); } catch { }
                            }

                            tCleanup.Commit();
                        }
                    }
                    catch { }
                }
                } // end using (finishHook) — hooks removed before any dialogs/cut

                if (!finish)
                    return Result.Cancelled; // Esc cancelled the tool — no cut performed

                if (polygonPoints.Count < 3)
                {
                    if (polygonPoints.Count > 0)
                        TaskDialog.Show("Polygonal Cut",
                            $"At least 3 points are required for a polygonal cut.\nYou picked {polygonPoints.Count} point(s).");
                    return Result.Cancelled;
                }

                ClippingService clipService = new ClippingService(doc);
                clipService.CutPointCloudScreenAlignedPolygonal(polygonPoints, uidoc.ActiveView);

                try { doc.Regenerate(); } catch { }
                try { uidoc.RefreshActiveView(); } catch { }

                Biruscan.UI.Views.ClippingManagerWindow.Instance?.RefreshDataPublic();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Polygonal Cut Error", ex.Message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// Thick orange model-line style for polygon rubber-band edges.
        /// Real ModelCurves only — no graphics overlay / DirectContext.
        /// Line weight 12 (Revit scale 1–16). Color RGB(255, 140, 0).
        /// </summary>
        private static GraphicsStyle EnsureThickOrangeLineStyle(Document doc)
        {
            const string styleName = "Biruscan Poly Cut";
            Categories categories = doc.Settings.Categories;
            Category linesCat = categories.get_Item(BuiltInCategory.OST_Lines);
            if (linesCat == null)
                return null;

            Category sub = null;
            foreach (Category c in linesCat.SubCategories)
            {
                if (string.Equals(c.Name, styleName, StringComparison.OrdinalIgnoreCase))
                {
                    sub = c;
                    break;
                }
            }

            if (sub == null)
                sub = categories.NewSubcategory(linesCat, styleName);

            // Bright orange, heavy weight so it reads over dense point clouds.
            sub.LineColor = new Color(255, 140, 0);
            try { sub.SetLineWeight(12, GraphicsStyleType.Projection); } catch { }
            try { sub.SetLineWeight(12, GraphicsStyleType.Cut); } catch { }

            return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
        }

        private static XYZ ProjectOntoPlane(Plane plane, XYZ p)
        {
            XYZ n = plane.Normal;
            double d = n.DotProduct(p - plane.Origin);
            return p - n.Multiply(d);
        }

        private static ModelCurve CreatePreviewEdge(
            Document doc, SketchPlane sketchPlane, XYZ a, XYZ b, GraphicsStyle style)
        {
            try
            {
                if (a.DistanceTo(b) < 0.01) return null;
                Line line = Line.CreateBound(a, b);
                ModelCurve mc = doc.Create.NewModelCurve(line, sketchPlane);
                if (mc != null && style != null)
                {
                    try { mc.LineStyle = style; } catch { }
                }
                return mc;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Low-level keyboard + mouse hook used during point picking. When the user presses
    /// Enter or right-clicks, it records a "finish" request and injects an Esc keystroke so
    /// the blocking PickPoint call returns. A genuine Esc passes through untouched, so it
    /// still cancels. (Revit's PickPoint otherwise ignores Enter and treats right-click as a
    /// context menu, so neither can finish on their own.)
    /// </summary>
    internal sealed class PolygonFinishHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int VK_RETURN = 0x0D;
        private const byte VK_ESCAPE = 0x1B;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private readonly HookProc _kbProc;
        private readonly HookProc _msProc;
        private IntPtr _kbHook = IntPtr.Zero;
        private IntPtr _msHook = IntPtr.Zero;

        public bool FinishRequested { get; private set; }

        public PolygonFinishHook()
        {
            _kbProc = KbCallback;
            _msProc = MsCallback;
            IntPtr hMod = GetModuleHandle(null); // handle of the running .exe — valid for LL hooks
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
            _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, hMod, 0);
        }

        private void RequestFinish()
        {
            if (FinishRequested) return;
            FinishRequested = true;
            // Inject Esc so the blocking PickPoint returns (OperationCanceledException).
            keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private IntPtr KbCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    int vk = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
                    if (vk == VK_RETURN)
                    {
                        // Finish on key-UP so both the down and up are swallowed first.
                        if (msg == WM_KEYUP || msg == WM_SYSKEYUP) RequestFinish();
                        return (IntPtr)1; // swallow Enter (down + up)
                    }
                }
            }
            return CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        private IntPtr MsCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                if (msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP)
                {
                    // Finish on button-UP, after BOTH down and up have been swallowed — Revit
                    // never sees the right-click, so no context menu is shown.
                    if (msg == WM_RBUTTONUP) RequestFinish();
                    return (IntPtr)1; // swallow right-click (down + up)
                }
            }
            return CallNextHookEx(_msHook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_kbHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
            if (_msHook != IntPtr.Zero) { UnhookWindowsHookEx(_msHook); _msHook = IntPtr.Zero; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    }
}
