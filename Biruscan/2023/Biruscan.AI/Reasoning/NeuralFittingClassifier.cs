using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Biruscan.AI.Reasoning
{
    public enum MepFittingKind
    {
        PipeElbow90,
        PipeElbow45,
        PipeBendCustom,
        PipeTeeStraight,
        PipeTeeReducing,
        PipeCross4Way,
        PipeReducerConcentric,
        PipeReducerEccentric,
        PipeEndCap,
        PipeFlangeCoupling,

        DuctElbow90,
        DuctElbow45,
        DuctTeeBranch,
        DuctTapShoeTakeoff,
        DuctTransitionReducer,
        DuctCrossWye,
        DuctEndCap,

        CableTrayHorizontalBend90,
        CableTrayHorizontalBend45,
        CableTrayVerticalInsideBendRiser,
        CableTrayVerticalOutsideBendDrop,
        CableTrayHorizontalTee,
        CableTrayCross4Way,
        CableTrayReducer,

        ConduitBend90,
        ConduitBend45,
        ConduitConduletLB,
        ConduitConduletTee,
        ConduitJunctionBox
    }

    public class ClassifiedMepFitting
    {
        public MepFittingKind Kind;
        public string Service;
        public XYZ JunctionCenter;
        public List<XYZ> PortVectors = new List<XYZ>();
        public List<double> PortDiametersMm = new List<double>();
        public List<double> PortWidthsMm = new List<double>();
        public List<double> PortHeightsMm = new List<double>();
        public double PrimaryAngleDeg;
        public bool IsElevationChange;
        public double Confidence;
    }

    public static class NeuralFittingClassifier
    {
        public static ClassifiedMepFitting ClassifyJunction(
            string service,
            XYZ junctionPt,
            List<(XYZ dir, double diaMm, double? widthMm, double? heightMm)> connectedPorts)
        {
            if (connectedPorts == null || connectedPorts.Count == 0) return null;

            string s = service?.ToLowerInvariant() ?? "piping";
            int degree = connectedPorts.Count;

            var fit = new ClassifiedMepFitting
            {
                Service = s,
                JunctionCenter = junctionPt,
                PortVectors = connectedPorts.Select(p => p.dir.Normalize()).ToList(),
                PortDiametersMm = connectedPorts.Select(p => p.diaMm).ToList(),
                PortWidthsMm = connectedPorts.Select(p => p.widthMm ?? p.diaMm).ToList(),
                PortHeightsMm = connectedPorts.Select(p => p.heightMm ?? p.diaMm).ToList(),
                Confidence = 0.95
            };

            if (degree == 1)
            {
                if (s.Contains("duct")) fit.Kind = MepFittingKind.DuctEndCap;
                else fit.Kind = MepFittingKind.PipeEndCap;
                return fit;
            }

            if (degree == 2)
            {
                XYZ v1 = fit.PortVectors[0];
                XYZ v2 = fit.PortVectors[1];
                double dot = Math.Max(-1.0, Math.Min(1.0, v1.DotProduct(-v2)));
                double angleDeg = Math.Acos(dot) * (180.0 / Math.PI);
                fit.PrimaryAngleDeg = angleDeg;

                double d1 = fit.PortDiametersMm[0];
                double d2 = fit.PortDiametersMm[1];
                bool isSizeChange = Math.Abs(d1 - d2) > 5.0;

                bool isVerticalChange = Math.Abs(v1.Z) > 0.6 || Math.Abs(v2.Z) > 0.6;
                fit.IsElevationChange = isVerticalChange;

                if (angleDeg < 15.0)
                {
                    if (isSizeChange)
                    {
                        if (s.Contains("duct")) fit.Kind = MepFittingKind.DuctTransitionReducer;
                        else if (s.Contains("tray") || s.Contains("cable")) fit.Kind = MepFittingKind.CableTrayReducer;
                        else fit.Kind = MepFittingKind.PipeReducerConcentric;
                        return fit;
                    }
                    else
                    {
                        fit.Kind = MepFittingKind.PipeFlangeCoupling;
                        return fit;
                    }
                }

                if (angleDeg >= 35.0 && angleDeg <= 55.0)
                {
                    if (s.Contains("duct")) fit.Kind = MepFittingKind.DuctElbow45;
                    else if (s.Contains("tray") || s.Contains("cable"))
                        fit.Kind = MepFittingKind.CableTrayHorizontalBend45;
                    else if (s.Contains("conduit")) fit.Kind = MepFittingKind.ConduitBend45;
                    else fit.Kind = MepFittingKind.PipeElbow45;
                    return fit;
                }

                if (angleDeg >= 75.0 && angleDeg <= 105.0)
                {
                    if (s.Contains("duct")) fit.Kind = MepFittingKind.DuctElbow90;
                    else if (s.Contains("tray") || s.Contains("cable"))
                    {
                        if (isVerticalChange)
                        {
                            bool isUp = v1.Z > 0.3 || v2.Z > 0.3;
                            fit.Kind = isUp ? MepFittingKind.CableTrayVerticalInsideBendRiser
                                            : MepFittingKind.CableTrayVerticalOutsideBendDrop;
                        }
                        else
                        {
                            fit.Kind = MepFittingKind.CableTrayHorizontalBend90;
                        }
                    }
                    else if (s.Contains("conduit")) fit.Kind = MepFittingKind.ConduitBend90;
                    else fit.Kind = MepFittingKind.PipeElbow90;
                    return fit;
                }

                fit.Kind = MepFittingKind.PipeBendCustom;
                return fit;
            }

            if (degree == 3)
            {
                if (s.Contains("duct")) fit.Kind = MepFittingKind.DuctTeeBranch;
                else if (s.Contains("tray") || s.Contains("cable")) fit.Kind = MepFittingKind.CableTrayHorizontalTee;
                else if (s.Contains("conduit")) fit.Kind = MepFittingKind.ConduitConduletTee;
                else
                {
                    double dMin = fit.PortDiametersMm.Min();
                    double dMax = fit.PortDiametersMm.Max();
                    fit.Kind = (dMax - dMin > 5.0) ? MepFittingKind.PipeTeeReducing : MepFittingKind.PipeTeeStraight;
                }
                return fit;
            }

            if (degree >= 4)
            {
                if (s.Contains("duct")) fit.Kind = MepFittingKind.DuctCrossWye;
                else if (s.Contains("tray") || s.Contains("cable")) fit.Kind = MepFittingKind.CableTrayCross4Way;
                else if (s.Contains("conduit")) fit.Kind = MepFittingKind.ConduitJunctionBox;
                else fit.Kind = MepFittingKind.PipeCross4Way;
                return fit;
            }

            return fit;
        }
    }
}
