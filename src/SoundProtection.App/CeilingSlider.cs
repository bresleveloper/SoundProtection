using System.ComponentModel;
using System.Drawing.Drawing2D;
using SoundProtection.Core;

namespace SoundProtection.App;

/// <summary>
/// The slider for the cap. Its scale (-60 to 0 dB) and side margins match the meter above it, so the thumb
/// sits right under the cap marker. Left of the thumb is allowed (blue), right of it is above the cap (red).
/// </summary>
internal sealed class CeilingSlider : Control
{
    public const double MinDb = -60.0;
    public const double MaxDb = 0.0;
    public const double StepDb = 0.5;

    private const float SideMargin = 4f; // the same margin the meter uses

    private double _value = Settings.DefaultCeilingDb;
    private bool _dragging;

    public CeilingSlider()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick,
            true);
        TabStop = true;
        Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(0x1B, 0x1D, 0x21);
        Height = 34;
        MinimumSize = new Size(120, 34);
    }

    /// <summary>Raised only when the user moves the slider, not when the value is set from code.</summary>
    public event Action<double>? ValueChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, MinDb, MaxDb);
            if (clamped != _value)
            {
                _value = clamped;
                Invalidate();
            }
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var db = e.KeyCode switch
        {
            Keys.Left or Keys.Down => _value - StepDb,
            Keys.Right or Keys.Up => _value + StepDb,
            Keys.PageDown => _value - 5.0,
            Keys.PageUp => _value + 5.0,
            Keys.Home => MinDb,
            Keys.End => MaxDb,
            _ => double.NaN,
        };

        if (!double.IsNaN(db))
        {
            Commit(Math.Clamp(db, MinDb, MaxDb));
            e.Handled = true;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            _dragging = true;
            SetFromX(e.X);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            SetFromX(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var width = Width - 2 * SideMargin;
        var centreY = Height / 2f;
        var thumbX = SideMargin + (float)DbScale.ToFraction(_value, MinDb, MaxDb) * width;

        using (var allowed = new SolidBrush(Color.FromArgb(0x3D, 0x8B, 0xFD)))
        {
            g.FillRectangle(allowed, SideMargin, centreY - 3, thumbX - SideMargin, 6);
        }

        using (var capped = new SolidBrush(Color.FromArgb(0x5A, 0x2A, 0x2E)))
        {
            g.FillRectangle(capped, thumbX, centreY - 3, SideMargin + width - thumbX, 6);
        }

        using (var path = RoundedRectangle(new RectangleF(thumbX - 6, centreY - 11, 12, 22), 4))
        using (var fill = new SolidBrush(Color.FromArgb(0xFF, 0xC1, 0x07)))
        {
            g.FillPath(fill, path);
        }

        if (Focused)
        {
            using var focus = new Pen(Color.FromArgb(0x9A, 0xA0, 0xA8), 1f) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }
    }

    private void SetFromX(int x)
    {
        var width = Width - 2 * SideMargin;
        var fraction = width <= 0 ? 0.0 : (x - SideMargin) / width;
        Commit(DbScale.FromFraction(fraction, MinDb, MaxDb, StepDb));
    }

    private void Commit(double db)
    {
        if (db == _value)
        {
            return;
        }

        _value = db;
        Invalidate();
        ValueChanged?.Invoke(db);
    }

    private static GraphicsPath RoundedRectangle(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
