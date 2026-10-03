using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Biruscan.Services;
using Biruscan.UI.Views;
using System;
using System.Linq;

namespace Biruscan.Commands
{
    /// <summary>
    /// Stretch a straight Pipe, Duct, Conduit, or Cable Tray along its axis.
    /// Each end has a slider; maximum increment defaults to 5 meters in project units.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MepIncrementCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                MEPCurve mepCurve = uidoc.Selection.GetElementIds()
                    ?.Select(id => doc.GetElement(id))
                    .OfType<MEPCurve>()
                    .FirstOrDefault();

                if (mepCurve == null)
                {
                    try
                    {
                        Reference r = uidoc.Selection.PickObject(
                            ObjectType.Element,
                            new MepCurveSelectionFilter(),
                            "Pick a Pipe, Duct, Conduit, or Cable Tray to increment along its axis");
                        mepCurve = doc.GetElement(r.ElementId) as MEPCurve;
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }
                }

                if (mepCurve == null)
                {
                    TaskDialog.Show("Increment", "Select a Pipe, Duct, Conduit, or Cable Tray.");
                    return Result.Cancelled;
                }

                if (!(mepCurve.Location is LocationCurve loc) || !(loc.Curve is Line line) || !line.IsBound)
                {
                    TaskDialog.Show("Increment", "Increment works on a straight Pipe, Duct, Conduit, or Cable Tray.");
                    return Result.Cancelled;
                }

                XYZ orig0 = line.GetEndPoint(0);
                XYZ orig1 = line.GetEndPoint(1);
                XYZ axis = orig1 - orig0;
                if (axis.GetLength() < 1e-6)
                {
                    TaskDialog.Show("Increment", "The selected run is too short to increment.");
                    return Result.Cancelled;
                }
                XYZ dir = axis.Normalize();
                ElementId curveId = mepCurve.Id;

                ForgeTypeId lengthUnit = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
                double fiveMetersInternal = UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Meters);
                double maxDisplay = UnitUtils.ConvertFromInternalUnits(fiveMetersInternal, lengthUnit);
                string unitSymbol = UnitSymbol(lengthUnit);

                using (TransactionGroup tg = new TransactionGroup(doc, "Increment"))
                {
                    tg.Start();

                    Action<double, double> onChanged = (end1Display, end2Display) =>
                    {
                        double d0 = UnitUtils.ConvertToInternalUnits(end1Display, lengthUnit);
                        double d1 = UnitUtils.ConvertToInternalUnits(end2Display, lengthUnit);
                        using (Transaction t = new Transaction(doc, "Preview Increment"))
                        {
                            MepOperationService.AllowNetworkDisconnects(t);
                            t.Start();
                            try
                            {
                                MEPCurve live = doc.GetElement(curveId) as MEPCurve;
                                if (live?.Location is LocationCurve liveLoc)
                                {
                                    XYZ p0 = orig0 - dir * d0;
                                    XYZ p1 = orig1 + dir * d1;
                                    if (p0.DistanceTo(p1) >= 0.02)
                                    {
                                        try
                                        {
                                            liveLoc.Curve = Line.CreateBound(p0, p1);
                                        }
                                        catch
                                        {
                                            DisconnectPhysical(live);
                                            liveLoc.Curve = Line.CreateBound(p0, p1);
                                        }
                                    }
                                }
                                t.Commit();
                                uidoc.RefreshActiveView();
                            }
                            catch
                            {
                                if (t.HasStarted()) t.RollBack();
                            }
                        }
                    };

                    var win = new MepIncrementWindow(onChanged, maxDisplay, unitSymbol);
                    try { new System.Windows.Interop.WindowInteropHelper(win).Owner = commandData.Application.MainWindowHandle; } catch { }

                    if (win.ShowDialog() == true)
                    {
                        tg.Assimilate();
                        return Result.Succeeded;
                    }

                    tg.RollBack();
                    return Result.Cancelled;
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static void DisconnectPhysical(MEPCurve curve)
        {
            if (curve?.ConnectorManager == null) return;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                if (c.ConnectorType == ConnectorType.Logical || !c.IsConnected) continue;
                var refs = c.AllRefs.Cast<Connector>().ToList();
                foreach (Connector other in refs)
                {
                    try
                    {
                        if (other.Owner != null && other.Owner.Id != curve.Id)
                            c.DisconnectFrom(other);
                    }
                    catch { }
                }
            }
        }

        private static string UnitSymbol(ForgeTypeId u)
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
            catch
            {
                return "";
            }
        }
    }
}
