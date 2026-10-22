using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PitGlow.Config;
using PitGlow.Engine;
using PitGlow.Rendering;
using DeviceLayout = PitGlow.Rendering.Layout;
using DrawColor = System.Drawing.Color;

namespace PitGlow.Cli;

/// <summary>One fan (or strip) whose gauge start and direction is being set.</summary>
internal sealed class CalibrationTarget
{
    public required DeviceLayout Layout { get; init; }
    public required Group Group { get; init; }
    public required ZoneCfg Zone { get; init; }
    public required string Title { get; init; }
}

/// <summary>
/// Calibration window. The selected fan plays a slow gauge sweep on the real hardware, so you
/// can see where it starts and which way it fills, and the buttons change both until it looks
/// like a tach. Everything else is dark while you calibrate.
/// </summary>
internal sealed class CalibrationForm : Form
{
    private static readonly DrawColor Back = DrawColor.FromArgb(22, 22, 26);
    private static readonly DrawColor Panel2 = DrawColor.FromArgb(34, 34, 40);
    private static readonly DrawColor Ink = DrawColor.FromArgb(235, 235, 240);
    private static readonly DrawColor Quiet = DrawColor.FromArgb(150, 150, 160);
    private static readonly DrawColor Gold = DrawColor.FromArgb(240, 194, 74);

    private readonly Settings _cfg;
    private readonly IReadOnlyList<DeviceLayout> _layouts;
    private readonly Dictionary<DeviceLayout, DeviceWorker> _workers;
    private readonly List<CalibrationTarget> _targets;
    private readonly ListBox _list;
    private readonly DialPanel _dial;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly DateTime _started = DateTime.UtcNow;

    public bool Saved { get; private set; }

    public CalibrationForm(Settings cfg, IReadOnlyList<DeviceLayout> layouts, Dictionary<DeviceLayout, DeviceWorker> workers,
                           List<CalibrationTarget> targets)
    {
        _cfg = cfg;
        _layouts = layouts;
        _workers = workers;
        _targets = targets;

        Text = "Potato PitGlow - fan calibration";
        BackColor = Back;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(980, 620);
        MinimumSize = new Size(820, 520);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        var header = new Label
        {
            Dock = DockStyle.Top, Height = 70, Padding = new Padding(18, 14, 18, 0), ForeColor = Ink,
            Text = "Pick a fan on the left and watch it in your PC. The WHITE light is where the gauge starts; " +
                   "the colors fill from there, the way the RPM gauge will. Adjust until it starts where you want " +
                   "and fills clockwise, then Save.",
        };

        _list = new ListBox
        {
            Dock = DockStyle.Left, Width = 300, BackColor = Panel2, ForeColor = Ink, BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 56, IntegralHeight = false,
        };
        foreach (var t in targets) _list.Items.Add(t);
        _list.DrawItem += DrawTarget;
        _list.SelectedIndexChanged += (_, _) => { _dial.Invalidate(); };

        _dial = new DialPanel(this) { Dock = DockStyle.Fill, BackColor = Back };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(14, 12, 14, 12), BackColor = Back,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
        };
        buttons.Controls.Add(MakeButton("◀  Move start", (_, _) => Step(-1)));
        buttons.Controls.Add(MakeButton("Move start  ▶", (_, _) => Step(+1)));
        buttons.Controls.Add(MakeButton("⟲  Flip direction", (_, _) => Flip()));
        buttons.Controls.Add(MakeButton("Copy to all fans", (_, _) => CopyToAll()));
        var save = MakeButton("Save", (_, _) => { Saved = true; Close(); });
        save.BackColor = Gold;
        save.ForeColor = DrawColor.Black;
        buttons.Controls.Add(save);
        buttons.Controls.Add(MakeButton("Cancel", (_, _) => Close()));

        var keys = new Label
        {
            Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(18, 0, 0, 0), ForeColor = Quiet,
            Text = "Keys: ← → move the start   Space flips   ↑ ↓ pick a fan   Enter saves",
        };

        Controls.Add(_dial);
        Controls.Add(keys);
        Controls.Add(buttons);
        Controls.Add(_list);
        Controls.Add(header);

        KeyDown += OnKey;
        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) => Animate();
        Shown += (_, _) =>
        {
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _timer.Start();
        };
        FormClosing += (_, _) => _timer.Stop();
    }

    private Button MakeButton(string text, EventHandler click)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, Height = 38, Padding = new Padding(10, 2, 10, 2), Margin = new Padding(0, 0, 10, 0),
            FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Ink, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = DrawColor.FromArgb(70, 70, 80);
        b.Click += click;
        return b;
    }

    internal CalibrationTarget? Selected => _list.SelectedItem as CalibrationTarget;

    private void DrawTarget(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var t = (CalibrationTarget)_list.Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(sel ? DrawColor.FromArgb(58, 52, 30) : Panel2)) e.Graphics.FillRectangle(bg, e.Bounds);
        if (sel) using (var bar = new SolidBrush(Gold)) e.Graphics.FillRectangle(bar, e.Bounds.X, e.Bounds.Y, 4, e.Bounds.Height);
        using var bold = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, t.Title, bold, new Point(e.Bounds.X + 16, e.Bounds.Y + 8), Ink);
        var way = t.Zone.Reverse ? "fills counter-clockwise (as wired)" : "fills clockwise (as wired)";
        TextRenderer.DrawText(e.Graphics, $"start LED {t.Zone.StartLed} · {way}", Font,
                              new Point(e.Bounds.X + 16, e.Bounds.Y + 30), Quiet);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left: Step(-1); break;
            case Keys.Right: Step(+1); break;
            case Keys.Space: Flip(); break;
            case Keys.Enter: Saved = true; Close(); break;
            case Keys.Up when _list.SelectedIndex > 0: _list.SelectedIndex--; break;
            case Keys.Down when _list.SelectedIndex < _list.Items.Count - 1: _list.SelectedIndex++; break;
            default: return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void Step(int by)
    {
        if (Selected is not { } t) return;
        t.Zone.StartLed = Effects.PyMod(t.Zone.StartLed + by, t.Group.Leds.Count);
        Changed();
    }

    private void Flip()
    {
        if (Selected is not { } t) return;
        t.Zone.Reverse = !t.Zone.Reverse;
        Changed();
    }

    private void CopyToAll()
    {
        if (Selected is not { } t) return;
        foreach (var other in _targets.Where(o => o != t && o.Group.Leds.Count == t.Group.Leds.Count))
            (other.Zone.StartLed, other.Zone.Reverse) = (t.Zone.StartLed, t.Zone.Reverse);
        Changed();
    }

    private void Changed()
    {
        _list.Invalidate();
        _dial.Invalidate();
    }

    /// <summary>Sweep position 0..1, looping every two seconds with a short pause when full.</summary>
    internal double Phase => Math.Min(1.0, ((DateTime.UtcNow - _started).TotalSeconds % 2.4) / 2.0);

    /// <summary>Gauge colors for n LEDs at the current phase, in gauge order (0 = start).</summary>
    internal Rgb[] GaugeColors(int n)
    {
        var colors = new Rgb[n];
        double lit = Phase * n;
        for (int k = 0; k < n; k++)
        {
            if (k == 0) colors[k] = new Rgb(255, 255, 255);
            else if (k < lit) colors[k] = Effects.GradientAt(_cfg.Tach.Gradient, n > 1 ? (double)k / (n - 1) : 1.0);
        }
        return colors;
    }

    private void Animate()
    {
        var selected = Selected;
        foreach (var layout in _layouts)
        {
            var frame = layout.Info.OriginalColors.ToArray();
            foreach (var t in _targets.Where(t => t.Layout == layout))
            {
                int n = t.Group.Leds.Count;
                var ring = new Rgb[n];
                if (t == selected)
                {
                    var gauge = GaugeColors(n);
                    for (int k = 0; k < n; k++)
                        ring[Effects.PyMod(t.Zone.StartLed + (t.Zone.Reverse ? -k : k), n)] = gauge[k];
                }
                for (int i = 0; i < n; i++) frame[t.Group.Leds[i]] = ring[i];
            }
            _workers[layout].Submit(frame);
        }
        _dial.Invalidate();
    }

    /// <summary>The on-screen dial: what the gauge will look like once the fan is set up right.</summary>
    private sealed class DialPanel : Panel
    {
        private readonly CalibrationForm _form;

        public DialPanel(CalibrationForm form)
        {
            _form = form;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var t = _form.Selected;
            if (t is null) return;
            int n = t.Group.Leds.Count;
            float cx = Width / 2f, cy = Height / 2f + 10, r = Math.Min(Width, Height) * 0.30f;
            using var title = new Font(Font.FontFamily, 14f, FontStyle.Bold);
            TextRenderer.DrawText(g, t.Title, title, new Point(24, 16), Ink);
            TextRenderer.DrawText(g, "Goal: white at the bottom-left, filling clockwise, like a tach.", Font, new Point(24, 46), Quiet);

            using (var ringPen = new Pen(DrawColor.FromArgb(55, 55, 64), 2f))
                g.DrawEllipse(ringPen, cx - r, cy - r, r * 2, r * 2);
            var colors = _form.GaugeColors(n);
            float dot = Math.Max(10f, r * 0.16f);
            for (int k = 0; k < n; k++)
            {
                // A tach dial: start at the bottom-left (7:30) and sweep 270 degrees clockwise.
                double deg = 135 + 270.0 * (n > 1 ? (double)k / (n - 1) : 0);
                double rad = deg * Math.PI / 180;
                float x = cx + r * (float)Math.Cos(rad), y = cy + r * (float)Math.Sin(rad);
                var c = colors[k];
                var fill = c == Rgb.Off ? DrawColor.FromArgb(45, 45, 52) : DrawColor.FromArgb(c.R, c.G, c.B);
                using var brush = new SolidBrush(fill);
                g.FillEllipse(brush, x - dot / 2, y - dot / 2, dot, dot);
            }
            using var small = new Font(Font.FontFamily, 9f);
            TextRenderer.DrawText(g, "start", small, new Point((int)(cx - r * 0.95f), (int)(cy + r * 0.85f)), Quiet);
            TextRenderer.DrawText(g, $"start LED {t.Zone.StartLed} of {n}   ·   {(t.Zone.Reverse ? "flipped" : "as wired")}",
                                  Font, new Point(24, Height - 40), Quiet);
        }
    }

    /// <summary>Show the window on its own UI thread. Returns true if the user saved.</summary>
    public static bool Run(Settings cfg, IReadOnlyList<DeviceLayout> layouts, Dictionary<DeviceLayout, DeviceWorker> workers,
                           List<CalibrationTarget> targets)
    {
        bool saved = false;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                try
                {
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                }
                catch (InvalidOperationException)
                {
                    // already set by an earlier window in this process
                }
                using var form = new CalibrationForm(cfg, layouts, workers, targets);
                Application.Run(form);
                saved = form.Saved;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
        return saved;
    }
}
