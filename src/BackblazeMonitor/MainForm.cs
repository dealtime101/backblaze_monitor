using System.Diagnostics;
using BackblazeMonitor.Core;

namespace BackblazeMonitor;

/// <summary>
/// The tile. State and event handling; the controls are built in MainForm.Layout.cs and the logic that can be
/// tested lives in BackblazeMonitor.Core.
/// </summary>
internal sealed partial class MainForm : Form, ITrayHost
{
    private const int HeavyReadChars = 4 * 1024 * 1024;

    private readonly Settings _settings;
    private readonly List<QosChoice> _choices = QosChoices.All.ToList();
    private readonly LogMonitor _monitor = new(BzPaths.LogDir, AppPaths.History);
    private readonly RemainingTracker _remaining = new();
    private readonly BzInfoSync _bzSync = new();
    private readonly VolumeSampleStore _volumes = new(AppPaths.Volumes);
    private readonly AlertTracker _alerts = new();
    private readonly LimitMessageState _limitMsg = new();
    private readonly QosReadPacer _qosPacer = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = BzConstants.TickMs };
    private readonly HashSet<string> _loggedErrors = new();
    private readonly TrayController _tray;

    private bool _busy;          // a tick is running (its worker thread owns the monitor objects)
    private bool _workerRunning; // BackgroundPass is running on its worker thread: the monitor collections are not readable
    private bool _acting;        // a privileged action or bzcli is running
    private bool _syncing;       // programmatic update of the limit menu
    private bool _qosReading;
    private double _qosBits = -1; // policy rate read last, -1 = not read yet
    private VacationState? _vac;  // last known state of the optional vacation mode
    private int _vacLeft = 1;     // ticks before the next state read
    private bool _vacBusy;        // a state read is running
    private bool _filesOpen;
    private string _filesKey = "";
    private int? _hoverX;
    private HistoryResult? _hist;
    private string _tipText = "";
    private string _tipHelp = "";
    private bool _canStart;
    private bool _canStop;
    private bool _canRestart;

    private sealed record PassResult(
        DateTime Now,
        BzInfoSyncResult? Bz,
        ServiceSnapshot? Service,
        bool ServiceFailed,
        PauseWitness? Witness);

    public MainForm()
    {
        _settings = Settings.Load(AppPaths.Settings);
        BuildLayout();

        // Preferences first (units, period, pin), handlers after: checking the pin must not save
        _alerts.StallMin = _settings.StallAlertMin;
        _btnPin.Checked = _settings.TopMost;
        TopMost = _settings.TopMost;
        _monitor.LoadHistory();
        _volumes.Load();

        _tray = new TrayController(this, AppIcon.Load(SystemInformation.SmallIconSize) ?? Icon);
        _tray.SetStatus(UiStyle.Gray, "Backblaze");

        WireEvents();
        ApplyLimitMessage();
    }

    // ---------- ITrayHost ----------
    bool ITrayHost.CanStart => _canStart;

    bool ITrayHost.CanStop => _canStop;

    bool ITrayHost.CanRestart => _canRestart;

    public bool ShowBits => _settings.ShowBits;

    int ITrayHost.Period => _settings.Period;

    public void ShowTile()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    public void RunService(ServiceAction action) => ConfirmServiceAction(action);

    bool ITrayHost.VacationEnabled => _settings.VacationEnabled;

    string ITrayHost.VacationMenuText => Vacation.MenuText(_vac);

    bool ITrayHost.VacationStopEnabled => Vacation.StopEnabled(_vac);

    void ITrayHost.SetVacation(int hours) => SetVacation(hours);

    void ITrayHost.RefreshVacation() => _ = ReadVacationAsync();

    public void ToggleUnits()
    {
        _settings.ShowBits = !_settings.ShowBits;
        SaveSettings();
        RenderNetworkIfIdle();
    }

    public void SetPeriod(int period)
    {
        _settings.Period = Math.Clamp(period, 0, Periods.Count - 1);
        SaveSettings();
        RenderNetworkIfIdle();
    }

    public void ShowVolumes()
    {
        var vols = BackupReports.ReadVolumes(
            SharedFile.ReadText(BzPaths.TotalPath),
            SharedFile.ReadText(BzPaths.RemainPath),
            SharedFile.ReadText(BzPaths.InfoPath));
        var text = VolumeSamples.GetText(vols, _volumes.Samples, DateTime.Now);
        VolumeProgressDialog.Show(Visible ? this : null, text, TopMost, _style.Mono);
    }

    public bool IsStartupEnabled() => StartupShortcut.IsEnabled();

    public void ToggleStartup() => StartupShortcut.Toggle();

    public void Quit() => Close();

    // ---------- Wiring ----------
    private void WireEvents()
    {
        _btnPin.CheckedChanged += (_, _) =>
        {
            TopMost = _btnPin.Checked;
            _settings.TopMost = _btnPin.Checked;
            SaveSettings();
        };

        // Left click: Mbps <-> MB/s; right click: next chart period (30 min, 24 h, 7 d)
        MouseEventHandler toggleView = (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _settings.ShowBits = !_settings.ShowBits;
            else if (e.Button == MouseButtons.Right) _settings.Period = (_settings.Period + 1) % Periods.Count;
            else return;
            SaveSettings();
            RenderNetworkIfIdle();
        };
        _lblSpeed.MouseUp += toggleView;
        _lblUpIcon.MouseUp += toggleView;
        _graph.MouseUp += toggleView;
        _graph.MouseMove += (_, e) =>
        {
            if (e.X == _hoverX) return;
            _hoverX = e.X;
            UpdateHover();
        };
        _graph.MouseLeave += (_, _) =>
        {
            _hoverX = null;
            UpdateHover();
        };

        _lblRemain.Click += (_, _) => Guarded("progress per disk", ShowVolumes);
        _lblBzState.LinkClicked += (_, _) => Guarded("backblaze auto", SetBackblazeAuto);
        _cboLimit.SelectionChangeCommitted += (_, _) => Guarded("limit", OnLimitCommitted);
        // The mouse wheel over a drop-down list would change the limit unintentionally
        _cboLimit.MouseWheel += (_, e) =>
        {
            if (e is HandledMouseEventArgs h) h.Handled = true;
        };

        _btnStart.Click += (_, _) => Guarded("start", () => ConfirmServiceAction(ServiceAction.Start));
        _btnStop.Click += (_, _) => Guarded("stop", () => ConfirmServiceAction(ServiceAction.Stop));
        _btnRestart.Click += (_, _) => Guarded("restart", () => ConfirmServiceAction(ServiceAction.Restart));

        _btnFiles.Click += (_, _) => Guarded("files", ToggleRecentList);
        _lvFiles.ItemActivate += (_, _) => Guarded("open file", OpenSelectedFile);

        // When minimized, the tile leaves the taskbar for the notification area
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) Hide();
        };
        FormClosing += (_, e) =>
        {
            if (_acting && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        };
        FormClosed += (_, _) => OnClosed();

        // The timer starts once the window is laid out and shown
        Shown += (_, _) =>
        {
            Guarded("startup shortcut", StartupShortcut.RepairIfStale);
            _timer.Start();
            _ = TickAsync();
        };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    private void Guarded(string where, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(where, ex);
        }
    }

    private void SaveSettings() => _settings.Save(AppPaths.Settings);

    private void OnClosed()
    {
        _timer.Stop();
        _tray.Dispose();
        // The process ends with the window, so a running tick's own save would never come: wait for its worker
        // (a few seconds at most), then save. If it does not finish, leave the file alone rather than race it.
        if (!_workerRunning || UiWait.Until(() => !_workerRunning, 5_000, 30)) _monitor.SaveHistory();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _components.Dispose();
        }

        base.Dispose(disposing);
        if (disposing)
        {
            _style.Dispose();
            _appIcon?.Dispose();
        }
    }

    // ---------- Tick ----------
    private async Task TickAsync()
    {
        if (_busy || _acting) return;
        _busy = true;
        try
        {
            _workerRunning = true;
            PassResult pass;
            try
            {
                pass = await Task.Run(BackgroundPass);
            }
            finally
            {
                _workerRunning = false;
            }

            ApplyPass(pass);
        }
        catch (Exception ex)
        {
            LogOnce("tick", ex);
            try
            {
                _lblUpdated.Text = "Update failed - retrying";
            }
            catch
            {
                // The window may be closing
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void LogOnce(string where, Exception ex)
    {
        // Disk and process errors repeat every tick: one line per distinct error
        if (_loggedErrors.Count < 50 && _loggedErrors.Add(where + "|" + ex.GetType().Name + "|" + ex.Message)) ErrorLog.Write(where, ex);
    }

    private static DateTime? StampUtc(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.LastWriteTimeUtc : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Everything that touches disk or the service manager, on a worker thread: the UI thread never waits on it
    /// (the first log read can take seconds). Each step is isolated: one failing does not stop the others.
    /// </summary>
    private PassResult BackgroundPass()
    {
        var now = DateTime.Now;
        var read = 0;
        Step("log sample", () => read = _monitor.Sample(now));
        Step("log refresh", () => _monitor.Refresh(now));
        Step("remaining", () =>
        {
            var r = _remaining.Refresh(StampUtc(BzPaths.RemainPath), () => SharedFile.ReadText(BzPaths.RemainPath));
            if (r == RemainingRefresh.Updated) RecordVolumes();
        });

        // A failed volumes.txt write is retried every pass, whatever the report does
        Step("volumes retry", _volumes.RetryPending);

        BzInfoSyncResult? bz = null;
        Step("bzinfo", () => bz = _bzSync.Sync(StampUtc(BzPaths.InfoPath), () => SharedFile.ReadText(BzPaths.InfoPath)));

        ServiceSnapshot? svc = null;
        var failed = false;
        try
        {
            svc = ServiceMonitor.Query(BzConstants.ServiceName);
        }
        catch (Exception ex)
        {
            failed = true;
            LogOnce("service query", ex);
        }

        // The first pass churns through hundreds of MB of text that the GC then keeps: give it back
        if (read > HeavyReadChars) GC.Collect();
        return new PassResult(now, bz, svc, failed, PauseWitness.Read(BzPaths.PauseWitnessPath));
    }

    private void Step(string where, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogOnce(where, ex);
        }
    }

    // Called when the report changes (about once an hour): one extra read per hour
    private void RecordVolumes()
    {
        var vols = BackupReports.ReadVolumes(
            SharedFile.ReadText(BzPaths.TotalPath),
            SharedFile.ReadText(BzPaths.RemainPath),
            SharedFile.ReadText(BzPaths.InfoPath));
        _volumes.Record(vols, DateTimeOffset.Now.ToUnixTimeSeconds());
    }

    private void ApplyPass(PassResult r)
    {
        var now = r.Now;
        RenderNetwork(now);
        _ = UpdateRecentListAsync();
        RenderRemaining(now);
        RenderService(r.Service, r.ServiceFailed, now, r.Witness);
        SyncQos(now);
        RenderBzState(r.Bz);

        // One state read per ~5 min, off the UI thread: ssh can take seconds
        if (_settings.VacationEnabled && --_vacLeft <= 0)
        {
            _vacLeft = Vacation.ReadEveryTicks;
            _ = ReadVacationAsync();
        }

        var stall =_alerts.CheckStall(_monitor.LastSent, _remaining.Files, _bzSync.Info.Schedule, now);
        if (stall is not null) _tray.Balloon(Alerts.StallTitle, stall, ToolTipIcon.Warning);
    }

    // ---------- Rendering ----------
    private void RenderNetworkIfIdle()
    {
        // While a tick runs, its worker owns the monitor: the tick renders the new setting itself
        if (!_busy) RenderNetwork(DateTime.Now);
    }

    private void RenderNetwork(DateTime now)
    {
        var m = _monitor;
        var bits = _settings.ShowBits;
        var period = _settings.Period;
        var sending = m.State == NetState.Ok;

        _lblSpeed.Text = Formatting.FormatRate(m.Rate, bits);
        _lblSpeed.ForeColor = sending ? UiStyle.Accent : UiStyle.IdleSpeed;
        _lblUpIcon.ForeColor = sending ? UiStyle.Accent : UiStyle.IdleIcon;
        _lblNetTag.Text = NetworkTexts.GetTag(m.State);

        _hist = m.GetHistory(period, now);
        _lblNetStats.Text = NetworkTexts.GetStatsText(m.State, period, _hist, m.Peak, m.Total, bits);
        _graph.AccessibleDescription = Periods.All[period].Tip + " : " + _lblNetStats.Text;

        // Tooltip: the current file, without flickering on every tick
        _tipHelp = NetworkTexts.GetTipHelp(period);
        var tip = NetworkTexts.GetTip(m.State, m.CurrentFile, _tipHelp);
        if (tip != _tipText)
        {
            _tipText = tip;
            _tipNet.SetToolTip(_lblSpeed, tip);
        }

        _graph.Points = m.Points;
        _graph.History = _hist;
        _graph.Period = period;
        _graph.State = m.State;
        UpdateHover();
        _graph.Invalidate();
    }

    // Chart tooltip and marker: the hovered block first, then the help. Rewritten only when the pointed
    // block changes, otherwise the tooltip flickers.
    private void UpdateHover()
    {
        // The worker is rewriting the points: keep the last hit, the pass applied next redraws it.
        if (_workerRunning && _hoverX is not null) return;
        GraphHit? hit = null;
        if (_hoverX is { } x)
        {
            hit = GraphCurve.GetHit(_monitor.Points, _hist, _settings.Period, x, _graph.ClientSize.Width, DateTime.Now, _settings.ShowBits);
        }

        _graph.Hover = hit;
        var tip = NetworkTexts.GetChartTip(hit, _tipHelp, _tipText);
        if (!string.Equals(tip, _tipNet.GetToolTip(_graph), StringComparison.Ordinal))
        {
            _tipNet.SetToolTip(_graph, tip);
            _graph.Invalidate();
        }
    }

    private void RenderRemaining(DateTime now) =>
        _lblRemain.Text = Formatting.GetRemainingText(
            _remaining.Bytes, _remaining.Files, _monitor.Total / BzConstants.WindowSec, now);

    private void RenderService(ServiceSnapshot? snap, bool failed, DateTime now, PauseWitness? rawWitness)
    {
        string? statusForAlerts = null;
        Color color;
        var witness = _alerts.Effective(rawWitness);
        string? stopReason = null;
        if (snap is null)
        {
            // The query threw: show a harmless state and leave the alert tracker alone
            color = UiStyle.Gray;
            _lblStatus.Text = failed ? "Status unavailable" : "Loading...";
            _lblDetails.Text = failed ? "PID ?" : "";
            _canStart = _canStop = _canRestart = false;
        }
        else
        {
            var kind = ServiceText.Classify(snap.StatusOrNull);
            statusForAlerts = snap.Exists ? snap.Status : "";
            _lblStatus.Text = ServiceText.GetStatusText(snap.StatusOrNull);
            switch (kind)
            {
                case ServiceStatusKind.NotFound:
                    color = UiStyle.Gray;
                    _canStart = _canStop = _canRestart = false;
                    _lblDetails.Text = ServiceText.NotFoundDetails;
                    break;
                case ServiceStatusKind.Running:
                    color = UiStyle.Green;
                    (_canStart, _canStop, _canRestart) = (false, true, true);
                    break;
                case ServiceStatusKind.Stopped:
                    if (witness is not null)
                    {
                        // Wanted: not the red of a failure
                        color = UiStyle.Orange;
                        _lblStatus.Text = ServiceText.PausedByWitness;
                        stopReason = witness.Why;
                    }
                    else
                    {
                        color = UiStyle.Red;
                    }

                    (_canStart, _canStop, _canRestart) = (true, false, false);
                    break;
                default:
                    color = UiStyle.Orange;
                    _canStart = _canStop = _canRestart = false;
                    break;
            }

            if (snap.Exists) _lblDetails.Text = ServiceText.GetDetails(snap.StartType, snap.ProcessId, snap.WorkingSet, snap.PidFailed, stopReason);
        }

        _lblStatus.ForeColor = color;
        _dot.DotColor = color;
        _btnStart.Enabled = _canStart;
        _btnStop.Enabled = _canStop;
        _btnRestart.Enabled = _canRestart;
        _lblUpdated.Text = "Updated " + now.ToString("HH':'mm':'ss", BzConstants.Invariant);
        _tray.SetStatus(color, Formatting.GetTrayText(_lblStatus.Text, _lblSpeed.Text, _vac is { Active: true } ? _vac.Text : null));

        if (statusForAlerts is not null)
        {
            var msg = _alerts.OnServiceStatus(statusForAlerts, now, witness);
            if (msg is not null) _tray.Balloon(Alerts.StopTitle, msg, ToolTipIcon.Warning);
        }
    }

    private void RenderBzState(BzInfoSyncResult? result)
    {
        switch (result)
        {
            case BzInfoSyncResult.NotFound:
                _lblBzState.Text = BzInfo.NotFoundText;
                _lblBzState.LinkArea = new LinkArea(0, 0);
                // If the file comes back with the timestamp we last read, the line must still refresh
                _bzSync.Invalidate();
                break;
            case BzInfoSyncResult.Updated:
                var info = _bzSync.Info;
                _lblBzState.Text = info.StateText;
                _lblBzState.LinkArea = info.HasLink ? new LinkArea(0, _lblBzState.Text.Length) : new LinkArea(0, 0);
                break;
        }
    }

    // ---------- Limit ----------
    private void ApplyLimitMessage()
    {
        _lblLimitMsg.ForeColor = _limitMsg.Tone switch
        {
            MessageTone.Good => UiStyle.Green,
            MessageTone.Bad => UiStyle.Red,
            _ => UiStyle.Muted,
        };
        _lblLimitMsg.Text = _limitMsg.Text;
    }

    private void SetLimitMessage(string text, bool ok)
    {
        _limitMsg.Set(text, ok);
        ApplyLimitMessage();
    }

    private void SyncQos(DateTime now)
    {
        if (_limitMsg.Tick(_settings.Schedule, now, _qosBits)) ApplyLimitMessage();
        if (_qosPacer.Due()) _ = ReadQosAsync();
    }

    // The read runs in a hidden PowerShell: off the UI thread, one at a time
    private async Task ReadQosAsync()
    {
        if (_qosReading) return;
        _qosReading = true;
        try
        {
            var bits = await Task.Run(QosReader.Read);
            if (bits is { } b && !_acting) ApplyQosBits(b);
        }
        catch (Exception ex)
        {
            LogOnce("qos read", ex);
        }
        finally
        {
            _qosReading = false;
        }
    }

    private void ApplyQosBits(double bits)
    {
        bits = Math.Round(bits);
        _qosBits = bits;
        var idx = QosChoices.IndexOf(_choices, bits);
        // Policy set elsewhere with a value missing from the list: show its rate as is
        if (idx < 0)
        {
            var text = QosChoices.LabelFor(bits);
            _choices.Add(new QosChoice(text, (long)bits));
            _cboLimit.Items.Add(text);
            idx = _cboLimit.Items.Count - 1;
        }

        if (_cboLimit.SelectedIndex == idx) return;
        _syncing = true;
        try
        {
            _cboLimit.SelectedIndex = idx;
        }
        finally
        {
            _syncing = false;
        }
    }

    // SelectionChangeCommitted fires only on a choice made in the drop-down, never on a resync
    private void OnLimitCommitted()
    {
        if (_syncing || _acting) return;
        var i = _cboLimit.SelectedIndex;
        if (i < 0 || i >= _choices.Count) return;
        var bits = _choices[i].Bits;
        if (bits >= 0)
        {
            ApplyQos(QosCommands.GetCommand(bits), null, bits.ToString(BzConstants.Invariant));
            return;
        }

        var was = _settings.Schedule?.Text ?? ScheduleTexts.DefaultSpec;
        var txt = ScheduleDialog.Prompt(Visible ? this : null, was, TopMost);
        var s = Schedule.Parse(txt);
        if (s is not null)
        {
            ApplyQos(ScheduleCommands.GetSetup(s, DateTime.Today, AppPaths.PsHome), s, null);
            return;
        }

        if (!string.IsNullOrEmpty(txt)) SetLimitMessage(LimitMessages.InvalidWindow, false);
        _qosPacer.RequestSoon(); // cancelled or invalid: the menu goes back to the real state
    }

    // s: the window installed by the command, null for a fixed step (which removed it).
    // request: the step in bits for the agent, null for a window.
    private void ApplyQos(string command, Schedule? s, string? request)
    {
        _cboLimit.Enabled = false;
        SetLimitMessage(LimitMessages.Working, true);
        Refresh();
        ActionOutcome outcome;
        try
        {
            outcome = RunPrivileged(request, command);
        }
        finally
        {
            _cboLimit.Enabled = true;
        }

        if (outcome == ActionOutcome.Done)
        {
            SetLimitMessage(LimitMessages.Applied, true);
            _settings.Schedule = s;
            SaveSettings();
        }
        else
        {
            SetLimitMessage(LimitMessages.Failed, false);
        }

        _qosPacer.RequestSoon(); // re-read the real state on the next tick
    }

    // ---------- Privileged actions ----------
    // For the duration of the action neither the window nor the tray menu accepts clicks (restart, Quit) and
    // the timer stops: a stop seen before AskedAt is recorded would raise a "Backblaze stopped" alert.
    private void BeginExclusive()
    {
        _acting = true;
        UiWait.Until(() => !_busy, 30_000, 30);
        Enabled = false;
        _tray.Enabled = false;
        _timer.Stop();
    }

    private void EndExclusive()
    {
        Enabled = true;
        _tray.Enabled = true;
        _timer.Start();
        _acting = false;
    }

    private ActionOutcome RunPrivileged(string? request, string command)
    {
        BeginExclusive();
        try
        {
            return PrivilegedRunner.Run(request, command);
        }
        finally
        {
            EndExclusive();
        }
    }

    private DialogResult Msg(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) =>
        Visible && WindowState != FormWindowState.Minimized
            ? MessageBox.Show(this, text, caption, buttons, icon)
            : MessageBox.Show(text, caption, buttons, icon);

    // Start and Stop ask for confirmation; Restart is bare by decision. Also called by the tray menu.
    private void ConfirmServiceAction(ServiceAction action)
    {
        if (_acting) return;
        var ask = ServiceCommands.GetConfirmation(action);
        if (action == ServiceAction.Start && _settings.VacationEnabled)
        {
            // Starting alone may be undone by whatever stopped the service: offer to hold that off first
            var choice = StartChoiceDialog.Show(Visible ? this : null, Vacation.StartAsk, TopMost);
            if (choice == StartChoice.Cancel) return;
            // Set BEFORE the start: otherwise the other tool may stop it again in between. A failed pose is
            // said here (the tile's message is lost when it is hidden), and the choice stays the user's.
            if (choice == StartChoice.StartWithVacation && !SetVacation(Vacation.DefaultHours) &&
                Msg(Vacation.PoseFailedAsk, "Vacation mode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }
        }
        else if (ask is not null && Msg(ask, "Confirmation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        RunServiceAction(action);
    }

    // ---------- Vacation mode (optional, over ssh) ----------
    private async Task ReadVacationAsync()
    {
        if (!_settings.VacationEnabled || _vacBusy || _acting) return;
        _vacBusy = true;
        try
        {
            var (host, key) = (_settings.VacationHost, _settings.VacationKey);
            var (ok, text) = await Task.Run(() => VacationSsh.Run(host, key, "status"));
            _vac = Vacation.FromStatusCall(ok, text);
            _tray.UpdateVacation();
        }
        catch (Exception ex)
        {
            LogOnce("vacation", ex);
        }
        finally
        {
            _vacBusy = false;
        }
    }

    // 0 hours stops the mode. The window and the tray menu are locked meanwhile, the wait pumps messages. The
    // result is said where the limit messages are said.
    private bool SetVacation(int hours)
    {
        var command = Vacation.Command(hours);
        if (command is null || _acting || !_settings.VacationEnabled) return false;
        var (host, key) = (_settings.VacationHost, _settings.VacationKey);
        bool ok;
        BeginExclusive();
        try
        {
            var call = Task.Run(() => VacationSsh.Run(host, key, command));
            ok = UiWait.Until(() => call.IsCompleted, Vacation.TimeoutMs + 10_000) && call.Result.Ok;
        }
        finally
        {
            EndExclusive();
        }

        SetLimitMessage(Vacation.ResultMessage(hours, ok), ok);
        _vacLeft = 1; // read the state again on the next tick
        return ok;
    }

    // Waits for the action to finish: the state re-read afterwards is the real one. Refusal and failure are
    // reported in a balloon, since the action can also start from the tray menu with the tile hidden.
    private void RunServiceAction(ServiceAction action)
    {
        var outcome = RunPrivileged(ServiceCommands.RequestName(action), ServiceCommands.GetElevatedScript(action));
        // Recorded AFTER the action, at the time it ended: the 5 minutes of silence count from the stop, not from
        // the click (a UAC prompt left open for a few minutes would eat the guard). The timer is stopped meanwhile,
        // so no tick can see the stop before this. A failed stop or restart can leave the service stopped, and that
        // stop is ours; only a declined UAC prompt (nothing attempted) records nothing.
        if (action != ServiceAction.Start && ActionOutcomes.CountsAsAsked(outcome)) _alerts.MarkAsked(DateTime.Now);

        try
        {
            RenderService(ServiceMonitor.Query(BzConstants.ServiceName), false, DateTime.Now, PauseWitness.Read(BzPaths.PauseWitnessPath));
        }
        catch (Exception ex)
        {
            LogOnce("service query", ex);
        }

        var notice = ActionNotices.ForServiceAction(action, outcome, _lblStatus.Text);
        if (notice is not null)
        {
            _tray.Balloon(notice.Title, notice.Text, notice.IsError ? ToolTipIcon.Error : ToolTipIcon.Warning);
        }
    }

    // Switches Backblaze to "Automatic Threading/Throttle": it then manages threads and slider itself, and our
    // QoS policy remains the cap.
    private void SetBackblazeAuto()
    {
        if (_acting) return;
        if (!BzCli.Exists())
        {
            Msg(BzAutoCommands.MissingText, BzAutoCommands.MissingTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (Msg(BzAutoCommands.ConfirmText, BzAutoCommands.ConfirmTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        BzAutoOutcome outcome;
        BeginExclusive();
        try
        {
            outcome = BzCli.EnableAuto();
        }
        finally
        {
            EndExclusive();
        }

        var (text, ok) = LimitMessages.ForAuto(outcome);
        SetLimitMessage(text, ok);
        _bzSync.Invalidate(); // force a re-read of bzinfo.xml
    }

    // ---------- Last files sent ----------
    // When opened, the window grows downward, or moves up rather than leaving the screen; when reopened, the
    // list is rebuilt (sizes re-read).
    private void ToggleRecentList()
    {
        _filesOpen = !_filesOpen;
        _lvFiles.Visible = _filesOpen;
        var h = _filesOpen ? _lvFiles.Bottom + 14 : _btnFiles.Bottom + 6;
        ClientSize = new Size(ClientSize.Width, h);
        _btnFiles.Text = FilesButtonText + (_filesOpen ? ArrowUp : ArrowDown);
        var wa = Screen.FromRectangle(Bounds).WorkingArea;
        if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
        _filesKey = "";
        if (!_busy) _ = UpdateRecentListAsync();
    }

    private static string SizeText(string path)
    {
        var size = "";
        try
        {
            var fi = new FileInfo(path);
            size = "missing";
            if (fi.Exists) size = Formatting.FormatSize(fi.Length);
        }
        catch
        {
            // A path .NET refuses: the cell says so instead of staying empty (BAC466.488)
            size = "unreadable";
        }

        return size;
    }

    // Rebuilt only while open, and only when it changes. The size is read from disk (the log gives it neither
    // for batches nor for dedup; missing = moved or deleted since), off the UI thread: a disconnected network
    // drive would otherwise freeze the window.
    private async Task UpdateRecentListAsync()
    {
        try
        {
            if (!_filesOpen) return;
            var rows = _monitor.Recent.Rows();
            var key = _monitor.Recent.Key();
            if (key == _filesKey) return;
            _filesKey = key;
            var sizes = await Task.Run(() => rows.Select(r => SizeText(r.Path)).ToList());
            if (!_filesOpen || _filesKey != key) return;

            var today = DateTime.Today;
            _lvFiles.BeginUpdate();
            try
            {
                _lvFiles.Items.Clear();
                for (var i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    var item = new ListViewItem(Formatting.FormatWhen(r.Time, today));
                    item.SubItems.Add(RecentFileSet.FileName(r.Path));
                    item.SubItems.Add(sizes[i]);
                    item.Tag = r.Path;
                    item.ToolTipText = r.Path;
                    _lvFiles.Items.Add(item);
                }
            }
            finally
            {
                _lvFiles.EndUpdate();
            }

            // The time column gets its natural width, the name takes the rest
            _lvFiles.Columns[0].Width = -2;
            _lvFiles.Columns[1].Width = Math.Max(40, _lvFiles.ClientSize.Width - _lvFiles.Columns[0].Width - _lvFiles.Columns[2].Width);
        }
        catch (Exception ex)
        {
            LogOnce("recent list", ex);
        }
    }

    // Double-click or Enter: the folder in Explorer with the file selected; if the file is gone, its folder
    private void OpenSelectedFile()
    {
        if (_lvFiles.SelectedItems.Count == 0 || _lvFiles.SelectedItems[0].Tag is not string path) return;
        var dir = path.Substring(0, path.LastIndexOf('\\') + 1);
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        else if (Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
    }
}
