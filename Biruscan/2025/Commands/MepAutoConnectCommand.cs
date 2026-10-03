using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Biruscan.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Biruscan.Commands
{
    /// <summary>
    /// Auto-picks Union, Elbow, Tee, or Cross.
    /// Native selection (click, Ctrl+click, window). Right-click runs; Esc ends the tool.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MepAutoConnectCommand : IExternalCommand
    {
        private const int WhMouseLl = 14;
        private const int WmRButtonDown = 0x0204;
        private const int WmRButtonUp = 0x0205;
        private const int WmRButtonDblClk = 0x0206;
        private const int WmNcRButtonDown = 0x00A4;
        private const int WmNcRButtonUp = 0x00A5;
        private const int WmContextMenu = 0x007B;
        private const int VkEscape = 0x1B;
        private const int VkRButton = 0x02;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static UIApplication _uiapp;
        private static bool _session;
        private static bool _busy;
        private static bool _rightWasDown;
        private static volatile bool _rightClickPending;
        private static EventHandler<IdlingEventArgs> _idling;
        private static IntPtr _hook;
        private static LowLevelMouseProc _hookProc;

        public static void CancelSession()
        {
            Detach();
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            if (commandData.Application.ActiveUIDocument == null)
                return Result.Cancelled;

            try
            {
                StartSession(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("MEP Auto Error", ex.Message);
                return Result.Failed;
            }
        }

        private static void StartSession(UIApplication uiapp)
        {
            Detach();
            _uiapp = uiapp;
            _session = true;
            _rightClickPending = false;
            _rightWasDown = (GetAsyncKeyState(VkRButton) & 0x8000) != 0;
            _hookProc = MouseHook;
            try
            {
                IntPtr hMod = Marshal.GetHINSTANCE(typeof(MepAutoConnectCommand).Module);
                if (hMod == IntPtr.Zero)
                    hMod = GetModuleHandle("Biruscan.dll");
                _hook = SetWindowsHookEx(WhMouseLl, _hookProc, hMod, 0);
            }
            catch { _hook = IntPtr.Zero; }

            _idling = OnIdling;
            uiapp.Idling += _idling;
        }

        private static void Detach()
        {
            _session = false;
            _busy = false;
            _rightClickPending = false;
            _rightWasDown = false;
            if (_hook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_hook); } catch { }
                _hook = IntPtr.Zero;
            }
            _hookProc = null;
            if (_uiapp != null && _idling != null)
            {
                try { _uiapp.Idling -= _idling; } catch { }
            }
            _idling = null;
        }

        private static IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _session && !_busy)
            {
                int msg = (int)wParam.ToInt64();
                if (msg == WmRButtonDown || msg == WmRButtonDblClk || msg == WmNcRButtonDown || msg == WmContextMenu)
                {
                    if (msg != WmContextMenu)
                        _rightClickPending = true;
                    return (IntPtr)1;
                }
                if (msg == WmRButtonUp || msg == WmNcRButtonUp)
                    return (IntPtr)1;
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (!_session || _busy) return;
            try { e.SetRaiseWithoutDelay(); } catch { }

            if ((GetAsyncKeyState(VkEscape) & 0x8000) != 0)
            {
                Detach();
                return;
            }

            bool rightDown = (GetAsyncKeyState(VkRButton) & 0x8000) != 0;
            if (rightDown && !_rightWasDown)
                _rightClickPending = true;
            _rightWasDown = rightDown;

            if (!_rightClickPending) return;
            _rightClickPending = false;

            UIDocument uidoc = _uiapp?.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null) return;

            var picked = CollectFromIds(doc, uidoc.Selection.GetElementIds());
            if (!HasEnoughForAuto(picked))
                return;

            _busy = true;
            try
            {
                RunAuto(doc, picked);
                try { uidoc.Selection.SetElementIds(new List<ElementId>()); } catch { }
            }
            finally
            {
                _busy = false;
            }
        }

        private static void RunAuto(Document doc, List<Element> picked)
        {
            try
            {
                var result = new MepOperationService(doc).ConnectAuto(picked);
                if (!result.Success)
                    TaskDialog.Show("MEP Auto Error", result.Message);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("MEP Auto Error", ex.Message);
            }
        }

        private static List<Element> CollectFromIds(Document doc, ICollection<ElementId> ids)
        {
            var picked = new List<Element>();
            if (ids == null) return picked;
            foreach (ElementId id in ids)
                AddPicked(picked, doc.GetElement(id));
            return picked;
        }

        private static void AddPicked(List<Element> picked, Element e)
        {
            if (e == null) return;
            if (picked.Any(x => x.Id == e.Id)) return;
            if (e is MEPCurve)
            {
                picked.Add(e);
                return;
            }
            if (e is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
            {
                int n = fi.MEPModel.ConnectorManager.Connectors.Size;
                if (n >= 2 && n <= 4)
                    picked.Add(e);
            }
        }

        private static bool HasEnoughForAuto(List<Element> picked)
        {
            int pipes = picked.OfType<MEPCurve>().Select(c => c.Id).Distinct().Count();
            bool fitting = picked.OfType<FamilyInstance>().Any(f =>
                f.MEPModel?.ConnectorManager != null
                && f.MEPModel.ConnectorManager.Connectors.Size >= 2
                && f.MEPModel.ConnectorManager.Connectors.Size <= 4);
            if (fitting && pipes >= 1) return true;
            return pipes >= 2;
        }
    }
}
