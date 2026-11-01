using System.ComponentModel;
using BackblazeMonitor.Core;

namespace BackblazeMonitor;

/// <summary>Controls of the tile: same layout and texts as the original (300 x 310 client area).</summary>
internal sealed partial class MainForm
{
    private const string FilesButtonText = "Last files sent ";
    private const char ArrowDown = '▼';
    private const char ArrowUp = '▲';

    private readonly Container _components = new();
    private readonly UiStyle _style = new();
    private Icon? _appIcon;

    private StatusDot _dot = null!;
    private Label _lblStatus = null!;
    private CheckBox _btnPin = null!;
    private Label _lblDetails = null!;
    private Label _lblUpdated = null!;
    private Label _lblUpIcon = null!;
    private ActionLabel _lblSpeed = null!;
    private Label _lblNetTag = null!;
    private GraphPanel _graph = null!;
    private Label _lblNetStats = null!;
    private ActionLabel _lblRemain = null!;
    private ComboBox _cboLimit = null!;
    private Label _lblLimitMsg = null!;
    private LinkLabel _lblBzState = null!;
    private Button _btnStart = null!;
    private Button _btnStop = null!;
    private Button _btnRestart = null!;
    private Button _btnFiles = null!;
    private ListView _lvFiles = null!;
    private ToolTip _tipNet = null!;

    private static Label MakeLabel(Font font, Color color, Point at, Size size, string text = "") => new()
    {
        Font = font,
        ForeColor = color,
        Location = at,
        Size = size,
        Text = text,
        AutoSize = false,
    };

    // A label that can be reached and activated from the keyboard (see ActionLabel)
    private static ActionLabel MakeActionLabel(Font font, Color color, Point at, Size size, string text, string accessibleName, int tabIndex) => new()
    {
        Font = font,
        ForeColor = color,
        Location = at,
        Size = size,
        Text = text,
        AutoSize = false,
        Cursor = Cursors.Hand,
        AccessibleName = accessibleName,
        TabIndex = tabIndex,
    };

    private Button MakeButton(string text, int x, int tabIndex) => new()
    {
        Text = text,
        Location = new Point(x, 250),
        Size = new Size(84, 30),
        Font = _style.Details,
        FlatStyle = FlatStyle.System,
        TabIndex = tabIndex,
        TabStop = true,
    };

    private void BuildLayout()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = AppInfo.Title;
        ClientSize = new Size(300, 310);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.White;
        _appIcon = AppIcon.Load();
        if (_appIcon is not null) Icon = _appIcon;

        _tipNet = new ToolTip(_components) { AutoPopDelay = 15000 };
        var tipGeneral = new ToolTip(_components);

        // Status dot and text
        _dot = new StatusDot { Size = new Size(16, 16), Location = new Point(14, 16) };
        _lblStatus = MakeLabel(_style.Status, UiStyle.Gray, new Point(38, 12), new Size(210, 24), "Loading...");

        // "Always on top" pin (glyph from Segoe MDL2 Assets)
        _btnPin = new CheckBox
        {
            Appearance = Appearance.Button,
            Text = "",
            Font = _style.Glyph,
            Location = new Point(256, 9),
            Size = new Size(30, 28),
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = "Always on top",
            TabStop = true,
            TabIndex = 0,
        };
        _btnPin.FlatAppearance.BorderSize = 1;
        _btnPin.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        _btnPin.FlatAppearance.CheckedBackColor = Color.FromArgb(255, 213, 128);
        tipGeneral.SetToolTip(_btnPin, "Always on top");

        _lblDetails = MakeLabel(_style.Details, UiStyle.DarkGray, new Point(38, 40), new Size(248, 18));
        _lblUpdated = MakeLabel(_style.Small, UiStyle.Muted, new Point(38, 58), new Size(248, 16));

        var sep = new Panel
        {
            Location = new Point(14, 78),
            Size = new Size(272, 1),
            BackColor = Color.FromArgb(228, 230, 234),
        };

        // Network block: arrow, rate, tag
        _lblUpIcon = MakeLabel(_style.Glyph, UiStyle.IdleIcon, new Point(14, 90), new Size(22, 22), "");
        _lblSpeed = MakeActionLabel(_style.Speed, UiStyle.IdleSpeed, new Point(36, 85), new Size(140, 28), "--", "Upload rate, Enter to switch Mbps and MB/s, P for the next chart period", 8);
        _lblNetTag = MakeLabel(_style.Small, UiStyle.Muted, new Point(176, 93), new Size(110, 16));
        _lblNetTag.TextAlign = ContentAlignment.MiddleRight;

        // Chart
        _graph = new GraphPanel(_style.Small)
        {
            Location = new Point(14, 116),
            Size = new Size(272, 38),
            // A screen reader does not read a drawn panel: name and role fixed here, description redone on each
            // refresh (period and figures, see RenderNetwork) (BAC466.9)
            AccessibleName = "Upload chart",
            AccessibleRole = AccessibleRole.Chart,
        };

        _lblNetStats = MakeLabel(_style.Small, UiStyle.Muted, new Point(14, 158), new Size(272, 16));
        _lblRemain = MakeActionLabel(_style.Small, UiStyle.Muted, new Point(14, 176), new Size(272, 16), "", "Remaining to upload, Enter for the progress per disk", 9);
        tipGeneral.SetToolTip(_lblRemain, "Click or Enter: progress per disk");

        // Upload limit
        var lblLimit = MakeLabel(_style.Details, UiStyle.DarkGray, new Point(14, 201), new Size(42, 18), "Limit");
        _cboLimit = new ComboBox
        {
            Location = new Point(58, 198),
            Size = new Size(138, 22),
            Font = _style.Details,
            DropDownStyle = ComboBoxStyle.DropDownList,
            MaxDropDownItems = 17, // the 15 steps and the window entry without scrolling
            AccessibleName = "Upload limit",
            TabStop = true,
            TabIndex = 1,
        };
        foreach (var c in _choices) _cboLimit.Items.Add(c.Text);
        _lblLimitMsg = MakeLabel(_style.Small, UiStyle.Muted, new Point(202, 201), new Size(84, 18));
        _lblLimitMsg.TextAlign = ContentAlignment.MiddleRight;
        tipGeneral.SetToolTip(
            _cboLimit,
            "Throttles bztransmit.exe with a Windows QoS policy, in real Mbps.\r\n" +
            "Creating or removing the policy needs admin rights: the first\r\n" +
            "time, a UAC prompt creates a SYSTEM task that does the next ones\r\n" +
            "without a prompt (and the service actions).\r\n" +
            "The limit applies to new connections: the current block may\r\n" +
            "finish at the old rate. The graph above shows the real effect.\r\n" +
            "Time window...: a limit by day and none at night (or the reverse),\r\n" +
            "held by a SYSTEM scheduled task; a fixed step stops it.");
        tipGeneral.SetToolTip(_lblLimitMsg, "Active time window: time of the next switch and the limit that follows.");

        // Backblaze's own throttle; in manual mode the whole text is a link (click, or Tab then Enter)
        _lblBzState = new LinkLabel
        {
            Location = new Point(14, 226),
            Size = new Size(272, 18),
            Font = _style.Small,
            ForeColor = UiStyle.Muted,
            LinkColor = UiStyle.LinkAmber,
            ActiveLinkColor = UiStyle.LinkAmber,
            // Underlined (only the link area, i.e. manual mode): colour alone does not tell a link (amber on white is 5.3:1)
            LinkBehavior = LinkBehavior.AlwaysUnderline,
            AutoSize = false,
            TabStop = true,
            TabIndex = 2,
            AccessibleName = "Backblaze throttle mode",
        };

        _btnStart = MakeButton("Start", 14, 3);
        _btnStop = MakeButton("Stop", 108, 4);
        _btnRestart = MakeButton("Restart", 202, 5);

        // Last files sent: opened below the tile by the button; full path in a tooltip
        _btnFiles = new Button
        {
            Text = FilesButtonText + ArrowDown,
            Location = new Point(14, 286),
            Size = new Size(272, 18),
            Font = _style.Small,
            ForeColor = UiStyle.Muted,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = "Last files sent",
            AccessibleDescription = "Collapsed",
            TabStop = true,
            TabIndex = 6,
        };
        _btnFiles.FlatAppearance.BorderSize = 0;

        var scale = DeviceDpi / 96f;
        _lvFiles = new ListView
        {
            Location = new Point(14, 306),
            Size = new Size(272, 150),
            Font = _style.Small,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.FixedSingle,
            ShowItemToolTips = true,
            Visible = false,
            AccessibleName = "Last files sent",
            TabStop = true,
            TabIndex = 7,
        };
        _lvFiles.Columns.Add("Time", (int)(74 * scale));
        _lvFiles.Columns.Add("File", (int)(121 * scale));
        _lvFiles.Columns.Add("Size", (int)(58 * scale), HorizontalAlignment.Right);

        Controls.AddRange(new Control[]
        {
            _dot, _lblStatus, _btnPin, _lblDetails, _lblUpdated, sep, _lblUpIcon, _lblSpeed, _lblNetTag, _graph,
            _lblNetStats, _lblRemain, lblLimit, _cboLimit, _lblLimitMsg, _lblBzState, _btnStart, _btnStop,
            _btnRestart, _btnFiles, _lvFiles,
        });
        ResumeLayout(false);
    }
}
