using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SoundProtection.Core;

namespace SoundProtection.App;

/// <summary>
/// Draws the tray icons: a speaker on a coloured disc. Grey with a slash = off, blue = watching, red with sound waves = over
/// (something is being turned down), red with a "!" = down (protection is not installed or not working).
/// </summary>
internal static class IconFactory
{
    public static Icon Create(IconKind kind)
    {
        var fill = kind switch
        {
            IconKind.Off => Color.FromArgb(0x7A, 0x7F, 0x87),
            IconKind.Over or IconKind.Down => Color.FromArgb(0xE5, 0x48, 0x4D),
            _ => Color.FromArgb(0x2D, 0x7F, 0xF9),
        };

        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var disc = new SolidBrush(fill))
            {
                g.FillEllipse(disc, 1, 1, 30, 30);
            }

            using var white = new SolidBrush(Color.White);
            g.FillPolygon(
                white,
                new[]
                {
                    new PointF(7, 13), new PointF(12, 13), new PointF(18, 8),
                    new PointF(18, 24), new PointF(12, 19), new PointF(7, 19),
                });

            using var pen = new Pen(Color.White, kind is IconKind.Off or IconKind.Down ? 3f : 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            switch (kind)
            {
                case IconKind.Off:
                    g.DrawLine(pen, 8, 25, 25, 7);
                    break;
                case IconKind.Down:
                    g.DrawLine(pen, 24, 8, 24, 18); // the stroke of the "!"
                    g.FillEllipse(white, 22.2f, 21.2f, 3.6f, 3.6f); // and its dot
                    break;
                default:
                    g.DrawArc(pen, 14, 10, 10, 12, -50, 100);
                    g.DrawArc(pen, 12, 6, 16, 20, -50, 100);
                    break;
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
