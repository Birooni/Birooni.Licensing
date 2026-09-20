using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FamilyLoader
{
    public static class ThumbnailExtractor
    {
        public static ImageSource GetTextIcon(string name, int size)
        {
            DrawingVisual drawingVisual = new DrawingVisual();
            using (DrawingContext drawingContext = drawingVisual.RenderOpen())
            {
                string text = string.IsNullOrWhiteSpace(name) ? "NA" : name;
                if (text.Length > 2) text = text.Substring(0, 2);
                text = text.ToUpper(); // Optional: uppercase for better look

                // Draw Dark Blue background with Black outline
                Brush bgBrush = new SolidColorBrush(Color.FromRgb(0, 0, 139)); // Dark Blue
                Pen borderPen = new Pen(new SolidColorBrush(Color.FromRgb(0, 0, 0)), 1.5); // Black outline
                
                drawingContext.DrawRoundedRectangle(
                    bgBrush,
                    borderPen,
                    new Rect(1, 1, size - 2, size - 2),
                    size / 6.0,
                    size / 6.0);

                // Bold White brush
                Brush textBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255)); // White

                double pixelsPerDip = 1.0;
                try
                {
                    pixelsPerDip = VisualTreeHelper.GetDpi(drawingVisual).PixelsPerDip;
                }
                catch
                {
                    pixelsPerDip = 1.0;
                }

                FormattedText formattedText = new FormattedText(
                    text,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal),
                    size * 0.6,
                    textBrush,
                    pixelsPerDip);

                // Center text
                double x = (size - formattedText.Width) / 2;
                double y = (size - formattedText.Height) / 2;
                drawingContext.DrawText(formattedText, new Point(x, y));
            }

            RenderTargetBitmap rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(drawingVisual);
            rtb.Freeze();
            return rtb;
        }
    }
}
