using System.Drawing.Drawing2D;
using System.Reflection;
using BackblazeMonitor.Core;

namespace BackblazeMonitor;

/// <summary>The application icon, embedded in the executable.</summary>
internal static class AppIcon
{
    /// <summary>Loads the embedded icon at the requested size (the closest image of the .ico), or <c>null</c>.</summary>
    public static Icon? Load(Size? size = null)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("backblaze-monitor.ico");
            if (s is null) return null;
            return size is { } sz ? new Icon(s, sz) : new Icon(s);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Tray icons: the logo with a status dot, one per dot color (four at most), kept until exit. A GetHicon on
/// every refresh would leak a handle every tick.
/// </summary>
internal sealed class TrayIconFactory : IDisposable
{
    private readonly Dictionary<int, Icon> _icons = new();

    // Icons replaced after a size change: the NotifyIcon may still show one, so they go at the next change or at exit
    private readonly List<Icon> _retired = new();
    private Bitmap? _logo;
    private Size _size;

    public TrayIconFactory(Icon? logoSource)
    {
        _size = SystemInformation.SmallIconSize;
        _logo = ToBitmap(logoSource);
    }

    private static Bitmap? ToBitmap(Icon? source)
    {
        if (source is null) return null;
        try
        {
            return source.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Re-reads the system's small icon size (it changes with the display scale). When it moved, the cached
    /// icons are retired and the logo is taken again from <paramref name="logoSource"/> at the new size.
    /// Returns whether the caller must fetch its icon again.
    /// </summary>
    public bool Resize(Icon? logoSource)
    {
        var size = SystemInformation.SmallIconSize;
        if (size == _size) return false;
        _size = size;
        foreach (var i in _retired) i.Dispose();
        _retired.Clear();
        _retired.AddRange(_icons.Values);
        _icons.Clear();
        _logo?.Dispose();
        _logo = ToBitmap(logoSource);
        return true;
    }

    /// <summary>The icon for a status color (created on first use).</summary>
    public Icon Get(Color color)
    {
        var key = color.ToArgb();
        if (_icons.TryGetValue(key, out var cached)) return cached;
        var icon = Create(color);
        _icons[key] = icon;
        return icon;
    }

    private Icon Create(Color color)
    {
        using var bmp = new Bitmap(_size.Width, _size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_logo is not null) g.DrawImage(_logo, 0, 0, _size.Width, _size.Height);
            // White disc under the dot: without it, the "stopped" red gets lost in the red logo
            var d = (int)(_size.Width * 0.625);
            var x = _size.Width - d;
            var y = _size.Height - d;
            using var brush = new SolidBrush(color);
            g.FillEllipse(Brushes.White, x, y, d - 1, d - 1);
            g.FillEllipse(brush, x + 1.5f, y + 1.5f, d - 4, d - 4);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var wrapper = Icon.FromHandle(handle);
            // Clone copies the image into a handle the Icon owns; the temporary one is destroyed
            return (Icon)wrapper.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        foreach (var i in _icons.Values) i.Dispose();
        foreach (var i in _retired) i.Dispose();
        _icons.Clear();
        _retired.Clear();
        _logo?.Dispose();
    }
}

/// <summary>What the tray menu needs from the main window.</summary>
internal interface ITrayHost
{
    bool CanStart { get; }

    bool CanStop { get; }

    bool CanRestart { get; }

    bool ShowBits { get; }

    int Period { get; }

    void ShowTile();

    void RunService(ServiceAction action);

    void ToggleUnits();

    void SetPeriod(int period);

    void ShowVolumes();

    /// <summary>The optional vacation mode is set up (settings.txt): its menu exists.</summary>
    bool VacationEnabled { get; }

    string VacationMenuText { get; }

    bool VacationStopEnabled { get; }

    /// <summary>Sets the vacation mode for that many hours; 0 stops it.</summary>
    void SetVacation(int hours);

    /// <summary>Asks for a fresh read of the vacation state (answered later, through <see cref="TrayController.UpdateVacation"/>).</summary>
    void RefreshVacation();

    bool IsStartupEnabled();

    void ToggleStartup();

    void Quit();
}

/// <summary>
/// Notification-area icon: icon follows the status dot, tooltip gives the state and the rate, minimizing
/// the tile tucks it away here, double-click or "Show" reopens it.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly ITrayHost _host;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly TrayIconFactory _icons;
    private readonly ToolStripMenuItem _start;
    private readonly ToolStripMenuItem _stop;
    private readonly ToolStripMenuItem _restart;
    private readonly ToolStripMenuItem _units;
    private readonly ToolStripMenuItem _graph;
    private readonly ToolStripMenuItem _logon;
    private readonly ToolStripMenuItem? _vac;
    private readonly ToolStripMenuItem? _vacOff;
    private readonly Icon? _fallbackLogo;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private Color _color = Color.Gray;
    private string _text = "";

    public TrayController(ITrayHost host, Icon? logoSource)
    {
        _host = host;
        _fallbackLogo = logoSource;
        _icons = new TrayIconFactory(logoSource);
        _menu = new ContextMenuStrip();
        var show = new ToolStripMenuItem("Show");
        _start = new ToolStripMenuItem("Start");
        _stop = new ToolStripMenuItem("Stop");
        _restart = new ToolStripMenuItem("Restart");
        _units = new ToolStripMenuItem("Rate in MB/s");
        var vols = new ToolStripMenuItem("Progress per disk…");
        _graph = new ToolStripMenuItem("Graph");
        foreach (var p in Periods.All) _graph.DropDownItems.Add(new ToolStripMenuItem(p.Text));
        _logon = new ToolStripMenuItem("Start at sign-in");
        var quit = new ToolStripMenuItem("Quit");

        var items = new List<ToolStripItem> { show, _start, _stop, _restart, new ToolStripSeparator() };
        if (host.VacationEnabled)
        {
            _vac = new ToolStripMenuItem("Vacation mode");
            foreach (var h in Vacation.Hours) _vac.DropDownItems.Add(new ToolStripMenuItem(Vacation.Label(h)));
            _vac.DropDownItems.Add(new ToolStripSeparator());
            _vacOff = new ToolStripMenuItem("Stop vacation mode");
            _vac.DropDownItems.Add(_vacOff);
            _vac.DropDownItemClicked += (_, e) =>
            {
                if (e.ClickedItem == _vacOff)
                {
                    Safe(() => host.SetVacation(0));
                    return;
                }

                var i = e.ClickedItem is null ? -1 : _vac.DropDownItems.IndexOf(e.ClickedItem);
                if (i >= 0 && i < Vacation.Hours.Count) Safe(() => host.SetVacation(Vacation.Hours[i]));
            };
            items.Add(_vac);
        }

        items.AddRange(new ToolStripItem[] { _units, vols, _graph, new ToolStripSeparator(), _logon, quit });
        _menu.Items.AddRange(items.ToArray());

        show.Click += (_, _) => Safe(host.ShowTile);
        _start.Click += (_, _) => Safe(() => host.RunService(ServiceAction.Start));
        _stop.Click += (_, _) => Safe(() => host.RunService(ServiceAction.Stop));
        _restart.Click += (_, _) => Safe(() => host.RunService(ServiceAction.Restart));
        _units.Click += (_, _) => Safe(host.ToggleUnits);
        vols.Click += (_, _) => Safe(host.ShowVolumes);
        _logon.Click += (_, _) => Safe(host.ToggleStartup);
        quit.Click += (_, _) => Safe(host.Quit);
        _graph.DropDownItemClicked += (_, e) =>
        {
            var i = e.ClickedItem is null ? -1 : _graph.DropDownItems.IndexOf(e.ClickedItem);
            if (i >= 0) Safe(() => host.SetPeriod(i));
        };
        _menu.Opening += (_, _) => Safe(UpdateMenu);

        _tray = new NotifyIcon
        {
            Icon = _icons.Get(_color),
            Text = "Backblaze",
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (_, _) => Safe(host.ShowTile);
        _tray.BalloonTipClicked += (_, _) => Safe(host.ShowTile);
        _tray.Visible = true;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    // A display scale change moves the system's icon size: the icons are drawn again at the new size. The event
    // comes from another thread.
    private void OnDisplayChanged(object? sender, EventArgs e) => _ui?.Post(_ => Safe(RebuildIcons), null);

    private void RebuildIcons()
    {
        if (_icons.Resize(AppIcon.Load(SystemInformation.SmallIconSize) ?? _fallbackLogo)) _tray.Icon = _icons.Get(_color);
    }

    /// <summary>Menu enabled or not (it is off while a privileged action runs).</summary>
    public bool Enabled
    {
        get => _menu.Enabled;
        set => _menu.Enabled = value;
    }

    /// <summary>Icon color and tooltip. Only touched when they changed.</summary>
    public void SetStatus(Color color, string text)
    {
        if (color != _color)
        {
            _color = color;
            _tray.Icon = _icons.Get(color);
        }

        // Whatever the caller sends is cut to what NotifyIcon accepts, and remembered only once it is set: a
        // throw must not leave the old tooltip frozen as if it were current
        text = Formatting.CapTrayText(text);
        if (text != _text)
        {
            _tray.Text = text;
            _text = text;
        }
    }

    /// <summary>Vacation entry text and stop item, from the last known state.</summary>
    public void UpdateVacation()
    {
        if (_vac is null || _vacOff is null) return;
        _vac.Text = _host.VacationMenuText;
        _vacOff.Enabled = _host.VacationStopEnabled;
    }

    /// <summary>Shows a balloon.</summary>
    public void Balloon(string title, string text, ToolTipIcon icon)
    {
        try
        {
            _tray.ShowBalloonTip(10000, title, text, icon);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("balloon", ex);
        }
    }

    // Same enabled state as the buttons; the checkmark is re-read on every opening
    private void UpdateMenu()
    {
        _start.Enabled = _host.CanStart;
        _stop.Enabled = _host.CanStop;
        _restart.Enabled = _host.CanRestart;
        _logon.Checked = _host.IsStartupEnabled();
        if (_vac is not null)
        {
            // Last known state at once; the fresh read, when it answers, updates the text
            UpdateVacation();
            _host.RefreshVacation();
        }

        _units.Checked = !_host.ShowBits;
        for (var i = 0; i < _graph.DropDownItems.Count; i++)
        {
            ((ToolStripMenuItem)_graph.DropDownItems[i]).Checked = i == _host.Period;
        }
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("tray", ex);
        }
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _icons.Dispose();
    }
}
