using System.Drawing.Drawing2D;
using BackblazeMonitor.Core;

namespace BackblazeMonitor;

/// <summary>Colors and fonts of the tile, created once and disposed with the main window.</summary>
internal sealed class UiStyle : IDisposable
{
    public static readonly Color Accent = Color.FromArgb(0, 120, 212);
    public static readonly Color Green = Color.FromArgb(46, 160, 67);
    public static readonly Color Red = Color.FromArgb(207, 34, 46);
    public static readonly Color Orange = Color.Orange;
    public static readonly Color Gray = Color.Gray;
    public static readonly Color DarkGray = Color.FromArgb(90, 90, 90);

    // Grey of the small texts (7.5 pt): 5.9:1 on white; Color.Gray (128) only reaches 3.95:1, under the 4.5:1 of
    // WCAG 1.4.3 (BAC466.10)
    public static readonly Color Muted = Color.FromArgb(100, 100, 100);
    public static readonly Color IdleSpeed = Muted;
    public static readonly Color IdleIcon = Color.FromArgb(140, 140, 140);

    // 5.3:1 on white (200, 120, 0 gave 3.4:1)
    public static readonly Color LinkAmber = Color.FromArgb(160, 90, 0);

    public Font Status { get; } = new("Segoe UI", 11f, FontStyle.Bold);

    public Font Speed { get; } = new("Segoe UI", 14f, FontStyle.Bold);

    public Font Details { get; } = new("Segoe UI", 8.5f);

    public Font Small { get; } = new("Segoe UI", 7.5f);

    public Font Glyph { get; } = new("Segoe MDL2 Assets", 11f);

    public Font Mono { get; } = new("Consolas", 9f);

    public void Dispose()
    {
        Status.Dispose();
        Speed.Dispose();
        Details.Dispose();
        Small.Dispose();
        Glyph.Dispose();
        Mono.Dispose();
    }
}

/// <summary>The status dot: a filled disc of the status color.</summary>
internal sealed class StatusDot : Control
{
    private Color _color = Color.Gray;
    private SolidBrush _brush = new(Color.Gray);

    public StatusDot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }

    public Color DotColor
    {
        get => _color;
        set
        {
            if (_color == value) return;
            _color = value;
            _brush.Dispose();
            _brush = new SolidBrush(value);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillEllipse(_brush, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _brush.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// The rate chart: owner-drawn from <see cref="GraphCurve"/> data. Brushes, pens and fonts are created once.
/// The owner pushes the data and the hover marker; this control only draws and reports the mouse column.
/// </summary>
internal sealed class GraphPanel : Control
{
    private readonly SolidBrush _back = new(Color.FromArgb(248, 249, 251));
    private readonly SolidBrush _fill = new(Color.FromArgb(55, 0, 120, 212));
    private readonly SolidBrush _message = new(UiStyle.Muted);
    private readonly Pen _line = new(UiStyle.Accent, 1.6f);
    private readonly Pen _marker = new(Color.FromArgb(150, 60, 60, 60));
    private readonly Pen _border = new(Color.FromArgb(224, 226, 230));
    private readonly StringFormat _center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    private readonly Font _font;

    public GraphPanel(Font messageFont)
    {
        _font = messageFont;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    /// <summary>Window points (period 0).</summary>
    public IReadOnlyList<LogBlock> Points { get; set; } = Array.Empty<LogBlock>();

    /// <summary>History of the period (period &gt; 0).</summary>
    public HistoryResult? History { get; set; }

    /// <summary>Index into <see cref="Periods.All"/>.</summary>
    public int Period { get; set; }

    /// <summary>Transmission state (selects the "log not found" message).</summary>
    public NetState State { get; set; } = NetState.Init;

    /// <summary>Hovered block or slot: its column is drawn as a marker.</summary>
    public GraphHit? Hover { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        g.FillRectangle(_back, 0, 0, w, h);

        var seq = new List<GraphPoint>();
        try
        {
            if (Period > 0) seq = GraphCurve.GetHistory(History, w, h);
            else if (Points.Count >= 1) seq = GraphCurve.Get(Points, DateTime.Now, w, h);
        }
        catch
        {
            seq.Clear();
        }

        if (seq.Count >= 2)
        {
            var arr = new PointF[seq.Count];
            for (var i = 0; i < arr.Length; i++) arr[i] = new PointF(seq[i].X, seq[i].Y);
            var poly = new PointF[arr.Length + 2];
            Array.Copy(arr, poly, arr.Length);
            poly[arr.Length] = new PointF(arr[^1].X, h);
            poly[arr.Length + 1] = new PointF(arr[0].X, h);
            g.FillPolygon(_fill, poly);
            g.DrawLines(_line, arr);
            if (Hover is { } hv) g.DrawLine(_marker, (float)hv.X, 0f, (float)hv.X, h);
        }
        else
        {
            var text = State == NetState.NoLog
                ? "Backblaze log not found"
                : "Nothing sent in the last " + Periods.At(Period).Text;
            g.DrawString(text, _font, _message, new RectangleF(0, 0, w, h), _center);
        }

        g.DrawRectangle(_border, 0, 0, w - 1, h - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _back.Dispose();
            _fill.Dispose();
            _message.Dispose();
            _line.Dispose();
            _marker.Dispose();
            _border.Dispose();
            _center.Dispose();
        }

        base.Dispose(disposing);
    }
}
