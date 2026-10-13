using System.Drawing.Drawing2D;

namespace LogRAM.Npp;

internal static class ToolbarIcons
{
    internal static Bitmap Create(string action, bool dark, int size)
    {
        var image = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(size / 20f, size / 20f);
        using var pen = new Pen(dark ? Color.Gainsboro : Color.FromArgb(55, 65, 75), 1.7f)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
        };
        switch (action)
        {
            case "search":
                graphics.DrawEllipse(pen, 3, 3, 10, 10);
                graphics.DrawLine(pen, 12, 12, 17, 17);
                break;
            case "cancel":
                graphics.DrawLine(pen, 5, 5, 15, 15);
                graphics.DrawLine(pen, 5, 15, 15, 5);
                break;
            case "refresh":
                graphics.DrawArc(pen, 3, 3, 14, 14, 40, 285);
                graphics.DrawLines(pen, new PointF[] { new(12, 3), new(16, 3), new(16, 7) });
                break;
            case "live":
                graphics.DrawLines(pen, new PointF[] { new(2, 10), new(6, 10), new(8, 4), new(11, 16), new(14, 10), new(18, 10) });
                break;
            case "go":
                graphics.DrawLines(pen, new PointF[] { new(3, 4), new(3, 10), new(17, 10) });
                graphics.DrawLines(pen, new PointF[] { new(12, 5), new(17, 10), new(12, 15) });
                break;
            case "previous":
                graphics.DrawLine(pen, 10, 16, 10, 4);
                graphics.DrawLines(pen, new PointF[] { new(5, 9), new(10, 4), new(15, 9) });
                break;
            case "next":
                graphics.DrawLine(pen, 10, 4, 10, 16);
                graphics.DrawLines(pen, new PointF[] { new(5, 11), new(10, 16), new(15, 11) });
                break;
            case "export":
                graphics.DrawLine(pen, 10, 3, 10, 12);
                graphics.DrawLines(pen, new PointF[] { new(6, 8), new(10, 12), new(14, 8) });
                graphics.DrawLines(pen, new PointF[] { new(3, 13), new(3, 17), new(17, 17), new(17, 13) });
                break;
            default:
                image.Dispose();
                throw new ArgumentOutOfRangeException(nameof(action));
        }
        return image;
    }
}
