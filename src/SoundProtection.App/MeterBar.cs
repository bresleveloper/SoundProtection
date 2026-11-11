using System.Drawing.Drawing2D;
using SoundProtection.Core;

namespace SoundProtection.App;

/// <summary>A horizontal level bar from -60 to 0 dB with a cap marker (the ceiling, in the older display) and a peak-hold tick.</summary>
internal sealed class MeterBar : Control
{
    private const double MinDb = -60.0;
    private const double MaxDb = 0.0;

    private double _display = Db.Floor;
    private double _hold = Db.Floor;
    private double _ceiling = Settings.DefaultCeilingDb;
    private bool _over;

    public MeterBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.FromArgb(0x1B, 0x1D, 0x21);
        Font = new Font("Segoe UI", 8f);
        Height = 58;
        MinimumSize = new Size(120, 58);
    }

    public void Set(double displayDb, double holdDb, double ceilingDb, bool over)
    {
        if (displayDb == _display && holdDb == _hold && ceilingDb == _ceiling && over == _over)
        {
            return;
        }

        _display = displayDb;
        _hold = holdDb;
        _ceiling = ceilingDb;
        _over = over;
        Invalidate();
    }

    private static float PositionOf(double db, float left, float width) =>
        left + (float)DbScale.ToFraction(db, MinDb, MaxDb) * width;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        float left = 4;
        var width = Width - 8f;
        var bar = new RectangleF(left, 6, width, 22);
        var ceilingX = PositionOf(_ceiling, left, width);
        var levelX = PositionOf(_display, left, width);

        using (var track = new SolidBrush(Color.FromArgb(0x2B, 0x2E, 0x34)))
        {
            g.FillRectangle(track, bar);
        }

        using (var safe = new SolidBrush(Color.FromArgb(0x3D, 0x8B, 0xFD)))
        {
            g.FillRectangle(safe, left, bar.Top, Math.Max(0, Math.Min(levelX, ceilingX) - left), bar.Height);
        }

        if (levelX > ceilingX)
        {
            using var red = new SolidBrush(Color.FromArgb(0xE5, 0x48, 0x4D));
            g.FillRectangle(red, ceilingX, bar.Top, levelX - ceilingX, bar.Height);
        }

        if (_hold > MinDb)
        {
            var holdX = PositionOf(_hold, left, width);
            using var hold = new Pen(Color.FromArgb(0xE6, 0xE6, 0xE6), 2f);
            g.DrawLine(hold, holdX, bar.Top, holdX, bar.Bottom);
        }

        using (var marker = new Pen(Color.FromArgb(0xFF, 0xC1, 0x07), 2f))
        {
            g.DrawLine(marker, ceilingX, bar.Top - 4, ceilingX, bar.Bottom + 4);
        }

        if (_over)
        {
            using var outline = new Pen(Color.FromArgb(0xE5, 0x48, 0x4D), 1.5f);
            g.DrawRectangle(outline, bar.X, bar.Y, bar.Width, bar.Height);
        }

        using var tick = new Pen(Color.FromArgb(0x6B, 0x70, 0x78));
        using var label = new SolidBrush(Color.FromArgb(0x9A, 0xA0, 0xA8));
        foreach (var db in new[] { -60, -48, -36, -24, -12, 0 })
        {
            var x = PositionOf(db, left, width);
            g.DrawLine(tick, x, bar.Bottom + 4, x, bar.Bottom + 8);
            var text = db.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var size = g.MeasureString(text, Font);
            g.DrawString(text, Font, label, Math.Clamp(x - size.Width / 2, 0, Width - size.Width), bar.Bottom + 8);
        }
    }
}
