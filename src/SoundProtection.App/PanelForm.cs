using System.Globalization;
using SoundProtection.Core;

namespace SoundProtection.App;

/// <summary>
/// The panel: which output the sound goes to, the cap, apps and events, and the live level. With the plug-in in place the
/// level is the loudest sound before the volume slider and the status line says what protection is doing; without it the
/// level is the one at the ear and alerts are all there is.
/// </summary>
internal sealed class PanelForm : Form
{
    private const string EarLevelSliderTip = "Drag to set the cap. Anything above it counts as over. Arrow keys move it by 0.5 dB.";
    private const string StatusUnknown = "Protection status unknown";

    private static readonly Color ColBack = Color.FromArgb(0x1B, 0x1D, 0x21);
    private static readonly Color ColText = Color.FromArgb(0xE6, 0xE6, 0xE6);
    private static readonly Color ColMuted = Color.FromArgb(0x9A, 0xA0, 0xA8);
    private static readonly Color ColAmber = Color.FromArgb(0xFF, 0xC1, 0x07);
    private static readonly Color ColRed = Color.FromArgb(0xE5, 0x48, 0x4D);
    private static readonly Color ColGreen = Color.FromArgb(0x2E, 0x7D, 0x32);
    private static readonly Color ColField = Color.FromArgb(0x2B, 0x2E, 0x34);

    // Text on the dark panel needs lighter shades than the button and the big number use.
    private static readonly Color ColGoodText = Color.FromArgb(0x4C, 0xAF, 0x50);
    private static readonly Color ColBadText = Color.FromArgb(0xFF, 0x6B, 0x6B);

    private readonly Button _alertsButton = new();
    private readonly ComboBox _devices = new();
    private readonly Label _deviceLine = new();
    private readonly Label _level = new();
    private readonly Label _levelCaption = new();
    private readonly MeterBar _meter = new();
    private readonly Label _hold = new();
    private readonly NumericUpDown _ceiling = new();
    private readonly CeilingSlider _slider = new();
    private readonly Button _toneButton = new();
    private readonly Label _toneHint = new();
    private readonly Button _useHold = new();
    private readonly Label _limiter = new(); // the protection status line
    private readonly Label _warnings = new(); // under it: one line per warning
    private readonly Label _apps = new();
    private readonly Label _eventsHeader = new();
    private readonly ListBox _events = new();
    private readonly ToolTip _tips = new();

    private OutputMenu _menu = OutputMenu.Build(Array.Empty<OutputDevice>(), null);
    private bool _alertsOn = true;
    private bool _protectionMode; // true while the level shown is the one before the volume slider
    private bool _quiet; // true while the controls are being set from code, so no change events fire
    private int _eventCount;
    private double _biggestDb;
    private string _biggestText = "";

    public PanelForm()
    {
        Text = "Sound Protection";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = ColBack;
        ForeColor = ColText;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 650);
        DoubleBuffered = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(18, 14, 18, 14),
            BackColor = ColBack,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        // Header: title and the alerts switch.
        var header = Row(new ColumnStyle(SizeType.Percent, 100f), new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(MakeLabel("Sound Protection", 15f, FontStyle.Bold, ColText), 0, 0);
        StyleButton(_alertsButton, ColGreen);
        header.Controls.Add(_alertsButton, 1, 0);
        AddRow(root, header);

        // Output chooser.
        var outputRow = Row(new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.Percent, 100f));
        var outputLabel = MakeLabel("Output", 9.5f, FontStyle.Regular, ColMuted);
        outputLabel.Anchor = AnchorStyles.Left;
        outputRow.Controls.Add(outputLabel, 0, 0);
        _devices.DropDownStyle = ComboBoxStyle.DropDownList;
        _devices.FlatStyle = FlatStyle.Flat;
        _devices.BackColor = ColField;
        _devices.ForeColor = ColText;
        _devices.Dock = DockStyle.Fill;
        _devices.Margin = new Padding(10, 0, 0, 0);
        _devices.DrawMode = DrawMode.OwnerDrawFixed; // so the closed box and the list match the dark panel
        _devices.ItemHeight = 22;
        _devices.DrawItem += DrawDeviceItem;
        outputRow.Controls.Add(_devices, 1, 0);
        AddRow(root, outputRow);

        Restyle(_deviceLine, 9.5f, FontStyle.Regular, ColMuted);
        _deviceLine.Text = "";
        AddRow(root, _deviceLine);

        // The big number and what it means.
        var levelRow = Row(new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.Percent, 100f));
        Restyle(_level, 30f, FontStyle.Bold, ColText);
        _level.Margin = new Padding(0, 0, 16, 0);
        levelRow.Controls.Add(_level, 0, 0);
        Restyle(_levelCaption, 9f, FontStyle.Regular, ColMuted);
        _levelCaption.MaximumSize = new Size(330, 0);
        _levelCaption.Anchor = AnchorStyles.Left;
        levelRow.Controls.Add(_levelCaption, 1, 0);
        AddRow(root, levelRow);

        _meter.Dock = DockStyle.Fill;
        _meter.Margin = new Padding(0, 4, 0, 0);
        AddRow(root, _meter);

        // The cap slider sits right under the meter and shares its scale, so the thumb lines up with the cap marker.
        _slider.Dock = DockStyle.Fill;
        _slider.Margin = new Padding(0, 0, 0, 4);
        AddRow(root, _slider);
        _tips.SetToolTip(_slider, EarLevelSliderTip);

        // Peak, ceiling control.
        var statsRow = Row(
            new ColumnStyle(SizeType.Percent, 100f),
            new ColumnStyle(SizeType.AutoSize),
            new ColumnStyle(SizeType.AutoSize),
            new ColumnStyle(SizeType.AutoSize));
        Restyle(_hold, 9.5f, FontStyle.Regular, ColText);
        _hold.Anchor = AnchorStyles.Left;
        statsRow.Controls.Add(_hold, 0, 0);
        var ceilingLabel = MakeLabel("Cap (dB)", 9.5f, FontStyle.Regular, ColMuted);
        ceilingLabel.Anchor = AnchorStyles.Right;
        statsRow.Controls.Add(ceilingLabel, 1, 0);
        _ceiling.Minimum = (decimal)CeilingSlider.MinDb; // the range the plug-in accepts, and the slider's
        _ceiling.Maximum = (decimal)CeilingSlider.MaxDb;
        _ceiling.DecimalPlaces = 1;
        _ceiling.Increment = 0.5m;
        _ceiling.Value = (decimal)Settings.DefaultCeilingDb;
        _ceiling.Width = 80;
        _ceiling.BackColor = ColField;
        _ceiling.ForeColor = ColText;
        _ceiling.BorderStyle = BorderStyle.FixedSingle;
        _ceiling.Margin = new Padding(8, 0, 8, 0);
        statsRow.Controls.Add(_ceiling, 2, 0);
        _useHold.Text = "Use that peak";
        StyleButton(_useHold, ColField);
        statsRow.Controls.Add(_useHold, 3, 0);
        AddRow(root, statsRow);

        Restyle(_limiter, 9.5f, FontStyle.Regular, ColAmber);
        _limiter.Text = StatusUnknown;
        _limiter.MaximumSize = new Size(560, 0);
        AddRow(root, _limiter);

        Restyle(_warnings, 9.5f, FontStyle.Regular, ColAmber);
        _warnings.MaximumSize = new Size(560, 0);
        _warnings.Visible = false; // takes no room until there is a warning
        AddRow(root, _warnings);

        // Test tone: lets you try the cap without Zoom or YouTube.
        var toneRow = Row(new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.Percent, 100f));
        _toneButton.Text = "Play test tone";
        StyleButton(_toneButton, ColField);
        _toneButton.Anchor = AnchorStyles.Left;
        toneRow.Controls.Add(_toneButton, 0, 0);
        Restyle(_toneHint, 9f, FontStyle.Regular, ColMuted);
        _toneHint.Text = "1 kHz for 2 s at -20 dBFS, at your current Windows volume. Drag the cap below the tone's level to see an alert.";
        _toneHint.MaximumSize = new Size(420, 0);
        _toneHint.Margin = new Padding(10, 0, 0, 0);
        _toneHint.Anchor = AnchorStyles.Left;
        toneRow.Controls.Add(_toneHint, 1, 0);
        AddRow(root, toneRow);

        Restyle(_apps, 9.5f, FontStyle.Regular, ColText);
        _apps.MaximumSize = new Size(560, 0);
        _apps.Text = "Playing now: nothing";
        AddRow(root, _apps);

        Restyle(_eventsHeader, 9.5f, FontStyle.Bold, ColMuted);
        _eventsHeader.Text = "Events this session: none";
        _eventsHeader.Margin = new Padding(0, 8, 0, 2);
        AddRow(root, _eventsHeader);

        _events.BackColor = ColField;
        _events.ForeColor = ColText;
        _events.BorderStyle = BorderStyle.None;
        _events.IntegralHeight = false;
        _events.Font = new Font("Consolas", 9.5f);
        _events.Dock = DockStyle.Fill;
        AddRow(root, _events, fill: true);

        Controls.Add(root);

        SetAlertsInternal(true, raise: false);

        _alertsButton.Click += (_, _) => SetAlertsInternal(!_alertsOn, raise: true);
        _devices.SelectionChangeCommitted += (_, _) =>
        {
            var index = _devices.SelectedIndex;
            if (index >= 0 && index < _menu.Ids.Count)
            {
                DeviceChosen?.Invoke(_menu.Ids[index]);
            }
        };
        _ceiling.ValueChanged += (_, _) =>
        {
            if (_quiet)
            {
                return;
            }

            _slider.Value = (double)_ceiling.Value;
            CeilingChanged?.Invoke((double)_ceiling.Value);
        };
        _slider.ValueChanged += db =>
        {
            _quiet = true;
            try
            {
                _ceiling.Value = (decimal)db;
            }
            finally
            {
                _quiet = false;
            }

            CeilingChanged?.Invoke(db);
        };
        _toneButton.Click += (_, _) => PlayTestTone?.Invoke();
        _useHold.Click += (_, _) => UseHoldAsCeiling?.Invoke();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    public event Action<double>? CeilingChanged;

    public event Action<bool>? AlertsChanged;

    /// <summary>The chosen output's id, or null for "Follow Windows default".</summary>
    public event Action<string?>? DeviceChosen;

    public event Action? UseHoldAsCeiling;

    public event Action? PlayTestTone;

    public void SetOutputs(IReadOnlyList<OutputDevice> outputs, string? selectedId)
    {
        _quiet = true;
        try
        {
            _menu = OutputMenu.Build(outputs, selectedId);
            _devices.Items.Clear();
            foreach (var label in _menu.Labels)
            {
                _devices.Items.Add(label);
            }

            _devices.SelectedIndex = _menu.SelectedIndex;
        }
        finally
        {
            _quiet = false;
        }
    }

    public void SetCeiling(double db)
    {
        _quiet = true;
        try
        {
            _ceiling.Value = (decimal)Math.Clamp(db, (double)_ceiling.Minimum, (double)_ceiling.Maximum);
            _slider.Value = db; // holds itself to the same range
        }
        finally
        {
            _quiet = false;
        }
    }

    public void SetAlerts(bool on) => SetAlertsInternal(on, raise: false);

    /// <summary>
    /// Shows one moment. When <paramref name="protection"/> says the plug-in is installed and set up, the level is the loudest
    /// sound before the volume slider; when it is missing (not installed, or not working), does not cover the output that is playing,
    /// or is not known yet, it is the level at the ear.
    /// Either way the status line says what protection is doing.
    /// </summary>
    public void ShowState(PanelState s, ProtectionView? protection = null)
    {
        if (_alertsOn != s.AlertsEnabled)
        {
            SetAlertsInternal(s.AlertsEnabled, raise: false);
        }

        var volume = !s.HasDevice
            ? ""
            : s.Muted
                ? "   |   Windows volume: muted"
                : "   |   Windows volume: " + s.VolumePercent.ToString("0.#", CultureInfo.InvariantCulture) +
                  " % (" + Db.Format(s.VolumeDb) + ")";
        _deviceLine.Text = s.DeviceName + volume;

        if (protection?.State is { ShowsProtection: true } state)
        {
            SetProtectionMode(true);
            ShowProtectionLevel(s, state);
        }
        else
        {
            SetProtectionMode(false);
            ShowEarLevel(s);
        }

        ShowStatus(protection);
    }

    /// <summary>Adds a line for a level at the ear that went over the ceiling (the older display).</summary>
    public void AddEvent(OverEvent e) =>
        AddEventLine(Messages.Line(e), e.MaxOverDb, "+" + Db.Format(e.MaxOverDb));

    /// <summary>Adds a line for a sound that the plug-in turned down.</summary>
    public void AddEvent(TurnDownEvent e) =>
        AddEventLine(Messages.Line(e), e.MaxTurnDownDb, "turned down " + Db.Format(e.MaxTurnDownDb));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ShowEarLevel(PanelState s)
    {
        _level.Text = s.HasDevice ? Db.Format(s.EarDb) : "no output";
        _level.ForeColor = s.Over ? ColRed : ColText;
        _levelCaption.Text = s.Over && s.OverByDb > 0
            ? Db.Format(s.OverByDb) + " over your ceiling"
            : "at your ear, below the loudest this output can play (0 dB). Not dB SPL.";

        _meter.Set(s.DisplayDb, s.HoldDb, s.CeilingDb, s.Over);
        _hold.Text = "Last 10 s peak: " + Db.Format(s.HoldDb);
        _apps.Text = s.Apps.Count == 0
            ? "Playing now: nothing"
            : "Playing now: " + string.Join("     ", s.Apps.Take(4).Select(a => a.Name + " " + Db.Format(a.EarDb)));
    }

    // The number, the meter and the app list are about the sound before the volume slider, which is what the cap works on.
    // The peak line stays, since it is still true, but it is said to be at the ear so it is not read as the same scale.
    private void ShowProtectionLevel(PanelState s, ProtectionState p)
    {
        var turnedDown = p.TurnDownDb >= TurnDownDetector.ThresholdDb;
        _level.Text = Db.Format(p.LoudestInputDb);
        _level.ForeColor = turnedDown ? ColRed : ColText;
        _levelCaption.Text = ProtectionText.Caption(p);

        _meter.Set(p.LoudestInputDb, Db.Floor, s.CeilingDb, turnedDown);
        _hold.Text = "Last 10 s peak at your ear: " + Db.Format(s.HoldDb);
        _apps.Text = ProtectionText.PlayingNow(p);
    }

    // The status line: green when protected, amber when switched off or when sound is on an output the plug-in does not cover,
    // red when missing or not working. Each warning goes on its own line below it, in amber, so that a warning is never read
    // as part of a green "Protected".
    private void ShowStatus(ProtectionView? view)
    {
        if (view is null)
        {
            _limiter.ForeColor = ColAmber;
            _limiter.Text = StatusUnknown;
            ShowWarnings(Array.Empty<string>());
            return;
        }

        var state = view.State;
        _limiter.ForeColor = state.Status switch
        {
            ProtectionStatus.Protected or ProtectionStatus.Idle => ColGoodText,
            ProtectionStatus.Off or ProtectionStatus.NotProtected => ColAmber,
            _ => ColBadText,
        };
        _limiter.Text = state.StatusText;
        ShowWarnings(view.Warnings);
    }

    private void ShowWarnings(IReadOnlyList<string> warnings)
    {
        var show = warnings.Count > 0;
        _warnings.Text = string.Join(Environment.NewLine, warnings);
        if (_warnings.Visible != show)
        {
            _warnings.Visible = show;
        }
    }

    private void SetProtectionMode(bool on)
    {
        if (_protectionMode == on)
        {
            return;
        }

        _protectionMode = on;
        _useHold.Visible = !on; // the last peak at the ear is not a cap before the slider
        _tips.SetToolTip(_slider, on ? ProtectionText.SliderTip : EarLevelSliderTip);
    }

    private void AddEventLine(string line, double db, string biggestText)
    {
        _events.Items.Insert(0, line);
        while (_events.Items.Count > 50)
        {
            _events.Items.RemoveAt(_events.Items.Count - 1);
        }

        _eventCount++;
        if (_eventCount == 1 || db > _biggestDb)
        {
            _biggestDb = db;
            _biggestText = biggestText;
        }

        _eventsHeader.Text = $"Events this session: {_eventCount}, biggest {_biggestText}";
    }

    private void DrawDeviceItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
        {
            return;
        }

        var highlighted = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using var back = new SolidBrush(highlighted ? Color.FromArgb(0x3D, 0x8B, 0xFD) : ColField);
        e.Graphics.FillRectangle(back, e.Bounds);
        TextRenderer.DrawText(
            e.Graphics,
            _devices.Items[e.Index]?.ToString() ?? "",
            e.Font ?? Font,
            e.Bounds,
            ColText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void SetAlertsInternal(bool on, bool raise)
    {
        _alertsOn = on;
        _alertsButton.Text = on ? "Alerts: ON" : "Alerts: OFF";
        _alertsButton.BackColor = on ? ColGreen : ColField;
        if (raise)
        {
            AlertsChanged?.Invoke(on);
        }
    }

    private static Label MakeLabel(string text, float size, FontStyle style, Color color)
    {
        var label = new Label { Text = text };
        Restyle(label, size, style, color);
        return label;
    }

    private static void Restyle(Label label, float size, FontStyle style, Color color)
    {
        label.AutoSize = true;
        label.Font = new Font("Segoe UI", size, style);
        label.ForeColor = color;
        label.BackColor = ColBack;
        label.Margin = new Padding(0, 2, 0, 2);
    }

    private static void StyleButton(Button button, Color back)
    {
        button.AutoSize = true;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = back;
        button.ForeColor = ColText;
        button.Padding = new Padding(8, 3, 8, 3);
        button.Margin = new Padding(0);
        button.UseVisualStyleBackColor = false;
        button.Anchor = AnchorStyles.Right;
    }

    private static TableLayoutPanel Row(params ColumnStyle[] columns)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = columns.Length,
            RowCount = 1,
            BackColor = ColBack,
            Margin = new Padding(0, 4, 0, 4),
        };
        foreach (var column in columns)
        {
            row.ColumnStyles.Add(column);
        }

        return row;
    }

    private static void AddRow(TableLayoutPanel table, Control control, bool fill = false)
    {
        var index = table.RowStyles.Count;
        table.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100f) : new RowStyle(SizeType.AutoSize));
        table.RowCount = index + 1;
        table.Controls.Add(control, 0, index);
    }
}
