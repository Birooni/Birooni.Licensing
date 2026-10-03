using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;

namespace Biruscan.Services.Ai
{
    /// <summary>
    /// Gathers one training sample from the active document: the point-cloud ROI
    /// plus every visible/selected MEP element (pipes / ducts / conduits / fittings)
    /// as supervised ground truth for the in-process C# trainer.
    /// </summary>
    public sealed class TrainingDataCollector
    {
        public BirooniTrainSampleRequest Collect(Document doc, string serviceLabel)
        {
            if (doc == null) return null;

            var roiSvc = new RoiPointExportService();
            List<double[]> points = roiSvc.ExportCurrentCutRoiPoints(doc);
            points = RoiPointExportService.Downsample(points, 15000);
            if (points.Count < 30)
            {
                List<XYZ> dense = EdgewiseMepExtractor.LoadDenseWorldPoints(doc, out _);
                points = new List<double[]>(dense.Count);
                foreach (XYZ p in dense)
                    points.Add(new[] { p.X, p.Y, p.Z });
                points = RoiPointExportService.Downsample(points, 15000);
            }
            if (points.Count < 30) return null;

            View activeView = doc.ActiveView;

            HashSet<ElementId> selectionFilter = null;
            try
            {
                var uiDoc = new Autodesk.Revit.UI.UIDocument(doc);
                var sel = uiDoc.Selection.GetElementIds();
                if (sel != null && sel.Count > 0)
                    selectionFilter = new HashSet<ElementId>(sel);
            }
            catch { selectionFilter = null; }

            List<BirooniTrainingElement> elements = new List<BirooniTrainingElement>();
            elements.AddRange(CollectPipes(doc, activeView, selectionFilter));
            elements.AddRange(CollectDucts(doc, activeView, selectionFilter));
            elements.AddRange(CollectConduits(doc, activeView, selectionFilter));
            elements.AddRange(CollectFittings(doc, activeView, selectionFilter));

            if (elements.Count == 0) return null;

            string primaryTypeName = string.Empty;
            string systemTypeName = string.Empty;
            string levelName = string.Empty;
            bool insulationPresent = false;

            Element firstElem = null;
            if (selectionFilter != null && selectionFilter.Count > 0)
            {
                firstElem = doc.GetElement(selectionFilter.First());
            }
            if (firstElem == null && activeView != null)
            {
                firstElem = new FilteredElementCollector(doc, activeView.Id)
                    .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory>
                    {
                        BuiltInCategory.OST_PipeCurves,
                        BuiltInCategory.OST_DuctCurves,
                        BuiltInCategory.OST_Conduit,
                        BuiltInCategory.OST_CableTray
                    }))
                    .FirstOrDefault();
            }

            if (firstElem != null)
            {
                var typeElem = doc.GetElement(firstElem.GetTypeId());
                primaryTypeName = typeElem?.Name ?? firstElem.Name;

                var sysParam = firstElem.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)
                            ?? firstElem.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                if (sysParam != null && sysParam.HasValue)
                {
                    var sysTypeElem = doc.GetElement(sysParam.AsElementId());
                    systemTypeName = sysTypeElem?.Name ?? "";
                }

                var lvlParam = firstElem.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM)
                            ?? firstElem.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                if (lvlParam != null && lvlParam.HasValue)
                {
                    var lvlElem = doc.GetElement(lvlParam.AsElementId());
                    levelName = lvlElem?.Name ?? "";
                }

                var insulParam = firstElem.get_Parameter(BuiltInParameter.RBS_PIPE_INSULATION_THICKNESS)
                              ?? firstElem.get_Parameter(BuiltInParameter.RBS_REFERENCE_INSULATION_THICKNESS);
                if (insulParam != null && insulParam.HasValue && insulParam.AsDouble() > 0.001)
                {
                    insulationPresent = true;
                }
                else
                {
                    try
                    {
                        var insulIds = InsulationLiningBase.GetInsulationIds(doc, firstElem.Id);
                        if (insulIds != null && insulIds.Count > 0) insulationPresent = true;
                    }
                    catch { }
                }
            }

            return new BirooniTrainSampleRequest
            {
                ProjectName = doc.Title ?? "default",
                RevitDocHash = (doc.PathName ?? string.Empty).GetHashCode().ToString("X8"),
                Service = serviceLabel,
                UnitsIn = "feet",
                Points = points,
                Elements = elements,
                PrimaryTypeName = primaryTypeName,
                SystemTypeName = systemTypeName,
                LevelName = levelName,
                InsulationPresent = insulationPresent
            };
        }

        private static IEnumerable<BirooniTrainingElement> CollectPipes(Document doc, View view, HashSet<ElementId> sel)
        {
            FilteredElementCollector coll;
            try { coll = view != null ? new FilteredElementCollector(doc, view.Id).OfClass(typeof(Pipe)) : new FilteredElementCollector(doc).OfClass(typeof(Pipe)); }
            catch { coll = new FilteredElementCollector(doc).OfClass(typeof(Pipe)); }

            foreach (Pipe pipe in coll.OfType<Pipe>())
            {
                if (sel != null && !sel.Contains(pipe.Id)) continue;
                if (!(pipe.Location is LocationCurve lc) || !(lc.Curve is Line line)) continue;
                yield return new BirooniTrainingElement
                {
                    Kind = ClassifyPipeKind(line),
                    Start = new[] { line.GetEndPoint(0).X, line.GetEndPoint(0).Y, line.GetEndPoint(0).Z },
                    End = new[] { line.GetEndPoint(1).X, line.GetEndPoint(1).Y, line.GetEndPoint(1).Z },
                    DiameterMm = TryGetDiameterMm(pipe),
                };
            }
        }

        private static IEnumerable<BirooniTrainingElement> CollectDucts(Document doc, View view, HashSet<ElementId> sel)
        {
            FilteredElementCollector coll;
            try { coll = view != null ? new FilteredElementCollector(doc, view.Id).OfClass(typeof(Duct)) : new FilteredElementCollector(doc).OfClass(typeof(Duct)); }
            catch { coll = new FilteredElementCollector(doc).OfClass(typeof(Duct)); }

            foreach (Duct duct in coll.OfType<Duct>())
            {
                if (sel != null && !sel.Contains(duct.Id)) continue;
                if (!(duct.Location is LocationCurve lc) || !(lc.Curve is Line line)) continue;
                yield return new BirooniTrainingElement
                {
                    Kind = ClassifyPipeKind(line),
                    Start = new[] { line.GetEndPoint(0).X, line.GetEndPoint(0).Y, line.GetEndPoint(0).Z },
                    End = new[] { line.GetEndPoint(1).X, line.GetEndPoint(1).Y, line.GetEndPoint(1).Z },
                    DiameterMm = TryGetDiameterMm(duct),
                };
            }
        }

        private static IEnumerable<BirooniTrainingElement> CollectConduits(Document doc, View view, HashSet<ElementId> sel)
        {
            FilteredElementCollector coll;
            try { coll = view != null ? new FilteredElementCollector(doc, view.Id).OfClass(typeof(Conduit)) : new FilteredElementCollector(doc).OfClass(typeof(Conduit)); }
            catch { coll = new FilteredElementCollector(doc).OfClass(typeof(Conduit)); }

            foreach (Conduit conduit in coll.OfType<Conduit>())
            {
                if (sel != null && !sel.Contains(conduit.Id)) continue;
                if (!(conduit.Location is LocationCurve lc) || !(lc.Curve is Line line)) continue;
                yield return new BirooniTrainingElement
                {
                    Kind = ClassifyPipeKind(line),
                    Start = new[] { line.GetEndPoint(0).X, line.GetEndPoint(0).Y, line.GetEndPoint(0).Z },
                    End = new[] { line.GetEndPoint(1).X, line.GetEndPoint(1).Y, line.GetEndPoint(1).Z },
                    DiameterMm = TryGetDiameterMm(conduit),
                };
            }
        }

        private static IEnumerable<BirooniTrainingElement> CollectFittings(Document doc, View view, HashSet<ElementId> sel)
        {
            BuiltInCategory[] cats =
            {
                BuiltInCategory.OST_PipeFitting,
                BuiltInCategory.OST_DuctFitting,
                BuiltInCategory.OST_ConduitFitting,
            };

            foreach (var cat in cats)
            {
                FilteredElementCollector coll;
                try { coll = view != null ? new FilteredElementCollector(doc, view.Id).OfCategory(cat).WhereElementIsNotElementType() : new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType(); }
                catch { coll = new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType(); }

                foreach (FamilyInstance fi in coll.OfType<FamilyInstance>())
                {
                    if (sel != null && !sel.Contains(fi.Id)) continue;
                    if (fi.MEPModel == null || fi.MEPModel.ConnectorManager == null) continue;

                    int connectorCount = 0;
                    foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors) connectorCount++;
                    string kind = ClassifyFittingKind(fi, connectorCount);

                    XYZ centre = (fi.Location as LocationPoint)?.Point;
                    if (centre == null)
                    {
                        double sx = 0, sy = 0, sz = 0; int n = 0;
                        foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors) { sx += c.Origin.X; sy += c.Origin.Y; sz += c.Origin.Z; n++; }
                        if (n == 0) continue;
                        centre = new XYZ(sx / n, sy / n, sz / n);
                    }

                    yield return new BirooniTrainingElement
                    {
                        Kind = kind,
                        Center = new[] { centre.X, centre.Y, centre.Z },
                        DiameterMm = TryGetFittingDiameterMm(fi),
                    };
                }
            }
        }

        private static string ClassifyPipeKind(Line line)
        {
            XYZ dir = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
            double absZ = Math.Abs(dir.Z);
            if (absZ > 0.95) return dir.Z > 0 ? "riser_up" : "riser_drop";
            return "pipe";
        }

        private static string ClassifyFittingKind(FamilyInstance fi, int connectorCount)
        {
            try
            {
                var partTypeParam = fi.Symbol?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                if (partTypeParam != null && partTypeParam.HasValue)
                {
                    int pt = partTypeParam.AsInteger();
                    switch (pt)
                    {
                        case (int)PartType.Elbow: return "elbow";
                        case (int)PartType.Tee: return "tee";
                        case (int)PartType.Cross: return "cross";
                        case (int)PartType.TapPerpendicular:
                        case (int)PartType.TapAdjustable: return "tee";
                        case (int)PartType.Transition: return "reducer";
                        case (int)PartType.Union: return "union";
                        case (int)PartType.Cap: return "endcap";
                    }
                }
            }
            catch { }

            if (connectorCount == 2)
                return ClassifyTwoPortFitting(fi);
            switch (connectorCount)
            {
                case 1: return "endcap";
                case 3: return "tee";
                case 4: return "cross";
                default: return "tee";
            }
        }

        private static string ClassifyTwoPortFitting(FamilyInstance fi)
        {
            try
            {
                var ports = new List<Connector>();
                foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                {
                    if (c.ConnectorType != ConnectorType.Logical) ports.Add(c);
                    if (ports.Count == 2) break;
                }
                if (ports.Count < 2) return "elbow";
                XYZ d0 = ports[0].CoordinateSystem.BasisZ;
                XYZ d1 = ports[1].CoordinateSystem.BasisZ;
                double align = Math.Abs(d0.DotProduct(d1));
                if (align >= 0.96)
                {
                    double r0 = ports[0].Radius;
                    double r1 = ports[1].Radius;
                    return Math.Abs(r0 - r1) > Math.Max(r0, r1) * 0.12 ? "reducer" : "union";
                }
            }
            catch { }
            return "elbow";
        }

        private static double TryGetDiameterMm(MEPCurve curve)
        {
            try
            {
                Parameter p = curve.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                              ?? curve.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                              ?? curve.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                if (p != null && p.StorageType == StorageType.Double)
                    return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters);
            }
            catch { }
            return 0.0;
        }

        private static double TryGetFittingDiameterMm(FamilyInstance fi)
        {
            try
            {
                foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                    if (c.Shape == ConnectorProfileType.Round && c.Radius > 0)
                        return UnitUtils.ConvertFromInternalUnits(c.Radius * 2.0, UnitTypeId.Millimeters);
            }
            catch { }
            return 0.0;
        }
    }
}
