using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BiruBox.UI;

public enum BiruIconType
{
    AutoSectionBox,
    DrawBox,
    Quick,
    Toggle,
    Grow,
    Shrink,
    License
}

/// <summary>
/// Provides mathematically defined flat vector icons for BiruBox ribbon commands and dialogs.
/// Designed for clean high-contrast visibility on both Revit Light and Dark themes,
/// following the modern vector aesthetic of Revit ribbon tools (same like Vector, matching Image 2).
/// </summary>
public static class BiruIcons
{
    // Modern Vector Palette - Navy Blue Theme
    static readonly SolidColorBrush VectorNavyDeepBrush = Freeze(new SolidColorBrush(Color.FromRgb(10, 25, 47)));             // #0A192F Deep Navy
    static readonly SolidColorBrush VectorNavyBrush = Freeze(new SolidColorBrush(Color.FromRgb(13, 71, 161)));               // #0D47A1 Navy Blue
    static readonly SolidColorBrush VectorNavyLightBrush = Freeze(new SolidColorBrush(Color.FromRgb(21, 101, 192)));          // #1565C0 Royal/Light Navy
    static readonly SolidColorBrush VectorNavyFillBrush = Freeze(new SolidColorBrush(Color.FromRgb(30, 136, 229)));           // #1E88E5 Accent Navy Fill
    static readonly SolidColorBrush VectorNavyWireframeBrush = Freeze(new SolidColorBrush(Color.FromRgb(13, 71, 161)));      // #0D47A1 Navy Wireframe
    static readonly SolidColorBrush VectorWhiteWireframeBrush = Freeze(new SolidColorBrush(Color.FromRgb(236, 239, 241)));    // #ECEFF1 Light Grey/White Wireframe
    static readonly SolidColorBrush VectorSlateBrush = Freeze(new SolidColorBrush(Color.FromRgb(38, 50, 56)));               // #263238 Dark Slate
    static readonly SolidColorBrush VectorDrawBoxFillBrush = Freeze(new SolidColorBrush(Color.FromArgb(120, 13, 71, 161)));   // Semi-transparent Navy Fill
    static readonly SolidColorBrush VectorEyeIrisBrush = Freeze(new SolidColorBrush(Color.FromRgb(13, 71, 161)));            // #0D47A1 Navy Iris
    static readonly SolidColorBrush VectorEyeFillBrush = Freeze(new SolidColorBrush(Color.FromArgb(35, 13, 71, 161)));       // Subtle navy tint
    static readonly SolidColorBrush WhiteBrush = Freeze(new SolidColorBrush(Colors.White));

    // Vector Pens
    static readonly Pen VectorNavyWireframePen = Freeze(new Pen(VectorNavyWireframeBrush, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen VectorWhiteWireframePen = Freeze(new Pen(VectorWhiteWireframeBrush, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen VectorNavyBorderPen = Freeze(new Pen(VectorNavyBrush, 1.5) { LineJoin = PenLineJoin.Round });
    static readonly Pen VectorNavyDashedPen = Freeze(new Pen(VectorNavyBrush, 1.5) { DashStyle = new DashStyle(new double[] { 2.5, 2 }, 0), LineJoin = PenLineJoin.Round });
    static readonly Pen VectorEyeOutlinePen = Freeze(new Pen(VectorNavyBrush, 2.0) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen VectorArrowPen = Freeze(new Pen(VectorNavyBrush, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen VectorWhitePen = Freeze(new Pen(WhiteBrush, 0.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    static readonly Dictionary<(BiruIconType, int), ImageSource> Cache = new();

    /// <summary>
    /// Returns a vector DrawingImage for the requested icon type, scaled to the specified dimension (e.g. 16, 24, or 32).
    /// </summary>
    public static DrawingImage GetVectorDrawingImage(BiruIconType type)
    {
        DrawingGroup group = BuildDrawingGroup(type);
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Returns an ImageSource optimized for Revit Ribbon buttons and dialogs.
    /// </summary>
    public static ImageSource GetImageSource(BiruIconType type, int size = 32)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((type, size), out ImageSource? cached))
                return cached;

            ImageSource source = RenderToBitmapSource(type, size);
            Cache[(type, size)] = source;
            return source;
        }
    }

    /// <summary>
    /// Renders vector geometry onto a frozen BitmapSource at exact DPI.
    /// Eliminates any Revit internal cast exceptions when buttons enter disabled state.
    /// </summary>
    public static BitmapSource RenderToBitmapSource(BiruIconType type, int size)
    {
        DrawingGroup group = BuildDrawingGroup(type);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            // Scale from base 32x32 design coordinate space to target size
            double scale = size / 32.0;
            if (Math.Abs(scale - 1.0) > 1e-4)
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
            }
            dc.DrawDrawing(group);
            if (Math.Abs(scale - 1.0) > 1e-4)
            {
                dc.Pop();
            }
        }

        // Render at 96 DPI standard
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    static DrawingGroup BuildDrawingGroup(BiruIconType type)
    {
        var group = new DrawingGroup();
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 32, 32));

        switch (type)
        {
            case BiruIconType.AutoSectionBox:
                DrawAutoSectionBox(group);
                break;
            case BiruIconType.DrawBox:
                DrawDrawBox(group);
                break;
            case BiruIconType.Quick:
                DrawQuick(group);
                break;
            case BiruIconType.Toggle:
                DrawToggle(group);
                break;
            case BiruIconType.Grow:
                DrawGrow(group);
                break;
            case BiruIconType.Shrink:
                DrawShrink(group);
                break;
            case BiruIconType.License:
                DrawLicense(group);
                break;
        }

        group.Freeze();
        return group;
    }

    #region Vector Icon Implementations

    /// <summary>
    /// Auto-Section Box: Modern vector 3D isometric wireframe cube with solid navy core and corner crop grips.
    /// </summary>
    static void DrawAutoSectionBox(DrawingGroup group)
    {
        Point vTop = new(16, 5);
        Point vRight = new(25.5, 10.5);
        Point vCenter = new(16, 16);
        Point vLeft = new(6.5, 10.5);
        Point vBottom = new(16, 27);
        Point vBottomLeft = new(6.5, 21.5);
        Point vBottomRight = new(25.5, 21.5);

        // Center Solid Navy Cube / Cut Plane
        Point cTop = new(16, 9.5);
        Point cRight = new(21, 12.5);
        Point cCenter = new(16, 15.5);
        Point cLeft = new(11, 12.5);
        Point cBottom = new(16, 21.5);
        Point cLeftBottom = new(11, 18.5);
        Point cRightBottom = new(21, 18.5);

        StreamGeometry cutTop = new();
        using (StreamGeometryContext ctx = cutTop.Open())
        {
            ctx.BeginFigure(cTop, true, true);
            ctx.LineTo(cRight, true, false);
            ctx.LineTo(cCenter, true, false);
            ctx.LineTo(cLeft, true, false);
        }
        cutTop.Freeze();
        group.Children.Add(new GeometryDrawing(VectorNavyFillBrush, VectorNavyWireframePen, cutTop));

        StreamGeometry cutLeft = new();
        using (StreamGeometryContext ctx = cutLeft.Open())
        {
            ctx.BeginFigure(cLeft, true, true);
            ctx.LineTo(cCenter, true, false);
            ctx.LineTo(cBottom, true, false);
            ctx.LineTo(cLeftBottom, true, false);
        }
        cutLeft.Freeze();
        group.Children.Add(new GeometryDrawing(VectorNavyLightBrush, VectorNavyWireframePen, cutLeft));

        StreamGeometry cutRight = new();
        using (StreamGeometryContext ctx = cutRight.Open())
        {
            ctx.BeginFigure(cCenter, true, true);
            ctx.LineTo(cRight, true, false);
            ctx.LineTo(cRightBottom, true, false);
            ctx.LineTo(cBottom, true, false);
        }
        cutRight.Freeze();
        group.Children.Add(new GeometryDrawing(VectorNavyBrush, VectorNavyWireframePen, cutRight));

        // Wireframe Outlines
        StreamGeometry wireframe = new();
        using (StreamGeometryContext ctx = wireframe.Open())
        {
            ctx.BeginFigure(vTop, true, true);
            ctx.LineTo(vRight, true, false);
            ctx.LineTo(vCenter, true, false);
            ctx.LineTo(vLeft, true, false);

            ctx.BeginFigure(vLeft, false, false);
            ctx.LineTo(vBottomLeft, true, false);

            ctx.BeginFigure(vRight, false, false);
            ctx.LineTo(vBottomRight, true, false);

            ctx.BeginFigure(vCenter, false, false);
            ctx.LineTo(vBottom, true, false);

            ctx.BeginFigure(vBottomLeft, false, false);
            ctx.LineTo(vBottom, true, false);
            ctx.LineTo(vBottomRight, true, false);
        }
        wireframe.Freeze();
        group.Children.Add(new GeometryDrawing(null, VectorNavyWireframePen, wireframe));

        // 4 Corner Grips
        DrawGrip(group, vTop);
        DrawGrip(group, vRight);
        DrawGrip(group, vLeft);
        DrawGrip(group, vBottom);
    }

    /// <summary>
    /// Draw Box: 2D dashed navy rectangle with navy fill, navy corner grips, and drafting pencil.
    /// </summary>
    static void DrawDrawBox(DrawingGroup group)
    {
        // 2D Dashed Box with Navy Fill
        Rect rect = new(6, 9, 16, 16);
        RectangleGeometry boxGeom = new(rect, 1.0, 1.0);
        boxGeom.Freeze();
        group.Children.Add(new GeometryDrawing(VectorDrawBoxFillBrush, VectorNavyDashedPen, boxGeom));

        // 4 Corner Grips
        DrawGrip(group, new Point(6, 9));
        DrawGrip(group, new Point(22, 9));
        DrawGrip(group, new Point(22, 25));
        DrawGrip(group, new Point(6, 25));

        // Drafting Pencil at (22, 9)
        StreamGeometry pencil = new();
        using (StreamGeometryContext ctx = pencil.Open())
        {
            ctx.BeginFigure(new Point(22, 9), true, true);
            ctx.LineTo(new Point(23.5, 5.5), true, false);
            ctx.LineTo(new Point(27, 2), true, false);
            ctx.LineTo(new Point(30, 5), true, false);
            ctx.LineTo(new Point(26.5, 8.5), true, false);
            ctx.LineTo(new Point(23, 9), true, false);
        }
        pencil.Freeze();
        group.Children.Add(new GeometryDrawing(VectorNavyBrush, VectorNavyBorderPen, pencil));

        // Pencil tip (graphite)
        StreamGeometry tip = new();
        using (StreamGeometryContext ctx = tip.Open())
        {
            ctx.BeginFigure(new Point(22, 9), true, true);
            ctx.LineTo(new Point(23.5, 7.5), true, false);
            ctx.LineTo(new Point(23, 9), true, false);
        }
        tip.Freeze();
        group.Children.Add(new GeometryDrawing(VectorSlateBrush, null, tip));

        // White highlight line on pencil body
        LineGeometry pLine = new(new Point(24.5, 4.5), new Point(28, 7));
        pLine.Freeze();
        group.Children.Add(new GeometryDrawing(null, VectorWhitePen, pLine));
    }

    /// <summary>
    /// Quick: Modern navy 3D isometric cube wireframe with high-speed navy lightning bolt.
    /// </summary>
    static void DrawQuick(DrawingGroup group)
    {
        Point vTop = new(16, 5);
        Point vRight = new(25.5, 10.5);
        Point vCenter = new(16, 16);
        Point vLeft = new(6.5, 10.5);
        Point vBottom = new(16, 27);
        Point vBottomLeft = new(6.5, 21.5);
        Point vBottomRight = new(25.5, 21.5);

        StreamGeometry box = new();
        using (StreamGeometryContext ctx = box.Open())
        {
            ctx.BeginFigure(vTop, true, true);
            ctx.LineTo(vRight, true, false);
            ctx.LineTo(vCenter, true, false);
            ctx.LineTo(vLeft, true, false);

            ctx.BeginFigure(vLeft, false, false);
            ctx.LineTo(vBottomLeft, true, false);
            ctx.LineTo(vBottom, true, false);
            ctx.LineTo(vBottomRight, true, false);
            ctx.LineTo(vRight, true, false);

            ctx.BeginFigure(vCenter, false, false);
            ctx.LineTo(vBottom, true, false);
        }
        box.Freeze();
        group.Children.Add(new GeometryDrawing(null, VectorNavyWireframePen, box));

        // Navy Lightning Bolt
        StreamGeometry bolt = new();
        using (StreamGeometryContext ctx = bolt.Open())
        {
            ctx.BeginFigure(new Point(18.5, 4.0), true, true);
            ctx.LineTo(new Point(11.0, 15.5), true, false);
            ctx.LineTo(new Point(16.5, 15.5), true, false);
            ctx.LineTo(new Point(13.5, 28.0), true, false);
            ctx.LineTo(new Point(22.5, 13.5), true, false);
            ctx.LineTo(new Point(17.5, 13.5), true, false);
        }
        bolt.Freeze();
        group.Children.Add(new GeometryDrawing(VectorNavyFillBrush, VectorNavyBorderPen, bolt));

        // Speed highlight line
        StreamGeometry highlight = new();
        using (StreamGeometryContext ctx = highlight.Open())
        {
            ctx.BeginFigure(new Point(17.5, 7.0), false, false);
            ctx.LineTo(new Point(13.5, 14.5), true, false);
            ctx.LineTo(new Point(17.0, 14.5), true, false);
        }
        highlight.Freeze();
        group.Children.Add(new GeometryDrawing(null, VectorWhitePen, highlight));
    }

    /// <summary>
    /// Toggle: Clean navy eye icon with navy iris and white center pupil.
    /// </summary>
    static void DrawToggle(DrawingGroup group)
    {
        // Navy Eye Outline
        StreamGeometry eye = new();
        using (StreamGeometryContext ctx = eye.Open())
        {
            ctx.BeginFigure(new Point(4, 16), true, true);
            ctx.BezierTo(new Point(8, 9), new Point(24, 9), new Point(28, 16), true, false);
            ctx.BezierTo(new Point(24, 23), new Point(8, 23), new Point(4, 16), true, false);
        }
        eye.Freeze();
        group.Children.Add(new GeometryDrawing(VectorEyeFillBrush, VectorEyeOutlinePen, eye));

        // Iris
        EllipseGeometry iris = new(new Point(16, 16), 5.0, 5.0);
        iris.Freeze();
        group.Children.Add(new GeometryDrawing(VectorEyeIrisBrush, null, iris));

        // Pupil (Solid White)
        EllipseGeometry pupil = new(new Point(16, 16), 2.2, 2.2);
        pupil.Freeze();
        group.Children.Add(new GeometryDrawing(WhiteBrush, null, pupil));
    }

    /// <summary>
    /// Grow: Center white box with navy border and 4 bold outward navy arrows.
    /// </summary>
    static void DrawGrow(DrawingGroup group)
    {
        // Center White Box with Navy Border
        Rect centerBox = new(12, 12, 8, 8);
        RectangleGeometry boxGeom = new(centerBox, 1.0, 1.0);
        boxGeom.Freeze();
        group.Children.Add(new GeometryDrawing(WhiteBrush, VectorNavyBorderPen, boxGeom));

        // 4 Outward Navy Arrows
        // Top Arrow (pointing UP)
        DrawSimpleArrow(group, new Point(16, 12), new Point(16, 6), new Point(16, 4.5), new Point(13, 8), new Point(19, 8), VectorArrowPen, VectorNavyBrush);
        // Bottom Arrow (pointing DOWN)
        DrawSimpleArrow(group, new Point(16, 20), new Point(16, 26), new Point(16, 27.5), new Point(13, 24), new Point(19, 24), VectorArrowPen, VectorNavyBrush);
        // Left Arrow (pointing LEFT)
        DrawSimpleArrow(group, new Point(12, 16), new Point(6, 16), new Point(4.5, 16), new Point(8, 13), new Point(8, 19), VectorArrowPen, VectorNavyBrush);
        // Right Arrow (pointing RIGHT)
        DrawSimpleArrow(group, new Point(20, 16), new Point(26, 16), new Point(27.5, 16), new Point(24, 13), new Point(24, 19), VectorArrowPen, VectorNavyBrush);
    }

    /// <summary>
    /// Shrink: Center white box with navy border and 4 bold inward navy arrows.
    /// </summary>
    static void DrawShrink(DrawingGroup group)
    {
        // Center White Box with Navy Border
        Rect centerBox = new(12, 12, 8, 8);
        RectangleGeometry boxGeom = new(centerBox, 1.0, 1.0);
        boxGeom.Freeze();
        group.Children.Add(new GeometryDrawing(WhiteBrush, VectorNavyBorderPen, boxGeom));

        // 4 Inward Navy Arrows
        // Top Arrow (pointing DOWN into box)
        DrawSimpleArrow(group, new Point(16, 4.5), new Point(16, 10), new Point(16, 11.5), new Point(13, 8), new Point(19, 8), VectorArrowPen, VectorNavyBrush);
        // Bottom Arrow (pointing UP into box)
        DrawSimpleArrow(group, new Point(16, 27.5), new Point(16, 22), new Point(16, 20.5), new Point(13, 24), new Point(19, 24), VectorArrowPen, VectorNavyBrush);
        // Left Arrow (pointing RIGHT into box)
        DrawSimpleArrow(group, new Point(4.5, 16), new Point(10, 16), new Point(11.5, 16), new Point(8, 13), new Point(8, 19), VectorArrowPen, VectorNavyBrush);
        // Right Arrow (pointing LEFT into box)
        DrawSimpleArrow(group, new Point(27.5, 16), new Point(22, 16), new Point(20.5, 16), new Point(24, 13), new Point(24, 19), VectorArrowPen, VectorNavyBrush);
    }

    #endregion

    #region Helpers

    static void DrawGrip(DrawingGroup group, Point center)
    {
        EllipseGeometry grip = new(center, 1.5, 1.5);
        grip.Freeze();
        group.Children.Add(new GeometryDrawing(VectorNavyFillBrush, null, grip));
    }

    static void DrawSimpleArrow(DrawingGroup group, Point lineStart, Point lineEnd, Point tip, Point corner1, Point corner2, Pen pen, SolidColorBrush brush)
    {
        // Line shaft
        LineGeometry line = new(lineStart, lineEnd);
        line.Freeze();
        group.Children.Add(new GeometryDrawing(null, pen, line));

        // Triangular arrowhead
        StreamGeometry head = new();
        using (StreamGeometryContext ctx = head.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(corner1, true, false);
            ctx.LineTo(corner2, true, false);
        }
        head.Freeze();
        group.Children.Add(new GeometryDrawing(brush, null, head));
    }

    static void DrawLicense(DrawingGroup group)
    {
        // Vector Key Icon
        // Head / Ring of Key
        EllipseGeometry headOuter = new(new Point(12, 12), 6, 6);
        EllipseGeometry headInner = new(new Point(12, 12), 3, 3);
        CombinedGeometry keyHead = new(GeometryCombineMode.Exclude, headOuter, headInner);
        keyHead.Freeze();
        group.Children.Add(new GeometryDrawing(VectorNavyBrush, VectorNavyBorderPen, keyHead));

        // Shaft of Key
        StreamGeometry shaft = new();
        using (StreamGeometryContext ctx = shaft.Open())
        {
            ctx.BeginFigure(new Point(16, 16), true, true);
            ctx.LineTo(new Point(26, 26), true, false);
            ctx.LineTo(new Point(24, 28), true, false);
            ctx.LineTo(new Point(22, 26), true, false);
            ctx.LineTo(new Point(20, 24), true, false);
            ctx.LineTo(new Point(19, 25), true, false);
            ctx.LineTo(new Point(17, 23), true, false);
            ctx.LineTo(new Point(14, 20), true, false);
        }
        shaft.Freeze();
        group.Children.Add(new GeometryDrawing(VectorNavyBrush, VectorNavyBorderPen, shaft));
    }

    static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    #endregion
}
