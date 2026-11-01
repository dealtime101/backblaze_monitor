using BackblazeMonitor.Core;

namespace BackblazeMonitor;

/// <summary>Modal box that asks for the time window ("08:00-22:00=3"). Replaces the VisualBasic InputBox.</summary>
internal sealed class ScheduleDialog : Form
{
    private readonly TextBox _text;

    private ScheduleDialog(string initial, bool topMost)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = ScheduleTexts.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = topMost;
        ClientSize = new Size(400, 210);
        Font = SystemFonts.MessageBoxFont ?? Font;

        // The prompt takes the height its text needs with the system font; the field and the buttons sit below it
        var promptHeight = DialogLayout.TextHeight(ScheduleTexts.Prompt, Font, 376);
        var prompt = new Label
        {
            Text = ScheduleTexts.Prompt,
            Location = new Point(12, 12),
            Size = new Size(376, promptHeight),
            AutoSize = false,
        };
        var fieldTop = 12 + promptHeight + 8;
        _text = new TextBox
        {
            Text = initial,
            Location = new Point(12, fieldTop),
            Size = new Size(376, 23),
            TabIndex = 0,
            AccessibleName = "Time window",
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(232, fieldTop + 28),
            Size = new Size(75, 26),
            TabIndex = 1,
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(313, fieldTop + 28),
            Size = new Size(75, 26),
            TabIndex = 2,
        };
        ClientSize = new Size(400, fieldTop + 28 + 26 + 8);
        Controls.AddRange(new Control[] { prompt, _text, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout(false);
        Shown += (_, _) =>
        {
            _text.SelectAll();
            _text.Focus();
        };
    }

    /// <summary>Shows the box. Returns the text typed, or <c>null</c> when cancelled.</summary>
    public static string? Prompt(IWin32Window? owner, string initial, bool topMost)
    {
        using var d = new ScheduleDialog(initial, topMost);
        d.StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent; // near the window that asked
        var r = owner is null ? d.ShowDialog() : d.ShowDialog(owner);
        return r == DialogResult.OK ? d._text.Text : null;
    }
}

/// <summary>Sizes a dialog to its text.</summary>
internal static class DialogLayout
{
    /// <summary>Height of <paramref name="text"/> wrapped at <paramref name="width"/> in <paramref name="font"/>, never less than the 130 px the box was drawn for.</summary>
    public static int TextHeight(string text, Font font, int width) =>
        Math.Max(130, TextRenderer.MeasureText(text, font, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + 4);
}

/// <summary>Answer of the start box.</summary>
internal enum StartChoice
{
    Cancel,
    Start,
    StartWithVacation,
}

/// <summary>
/// Start confirmation with a third button that sets the vacation mode first (MessageBox cannot do three
/// custom buttons).
/// </summary>
internal static class StartChoiceDialog
{
    public static StartChoice Show(IWin32Window? owner, string text, bool topMost)
    {
        var choice = StartChoice.Cancel;
        using var f = new Form
        {
            Text = "Confirmation",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            TopMost = topMost,
            ClientSize = new Size(430, 170),
        };
        f.AutoScaleDimensions = new SizeF(96f, 96f);
        f.AutoScaleMode = AutoScaleMode.Dpi;
        f.Font = SystemFonts.MessageBoxFont ?? f.Font;
        var label = new Label { Text = text, Location = new Point(58, 16), Size = new Size(356, 96), AutoSize = false };
        using var warning = SystemIcons.Warning.ToBitmap(); // a PictureBox does not dispose its image: this does, after the dialog
        var icon = new PictureBox { Image = warning, Location = new Point(16, 18), Size = new Size(32, 32) };
        var start = new Button { Text = "Start", Location = new Point(16, 126), Size = new Size(110, 28), TabIndex = 0 };
        var vacation = new Button
        {
            Text = $"Start + vacation {Vacation.DefaultHours} h",
            Location = new Point(134, 126),
            Size = new Size(180, 28),
            TabIndex = 1,
        };
        var cancel = new Button { Text = "Cancel", Location = new Point(322, 126), Size = new Size(90, 28), TabIndex = 2 };
        start.Click += (_, _) =>
        {
            choice = StartChoice.Start;
            f.Close();
        };
        vacation.Click += (_, _) =>
        {
            choice = StartChoice.StartWithVacation;
            f.Close();
        };
        cancel.Click += (_, _) => f.Close();
        f.Controls.AddRange(new Control[] { label, icon, start, vacation, cancel });
        f.AcceptButton = start;
        f.CancelButton = cancel;
        if (owner is null) f.ShowDialog();
        else f.ShowDialog(owner);
        return choice;
    }
}

/// <summary>The "Progress per disk" window: a read-only monospaced text.</summary>
internal static class VolumeProgressDialog
{
    /// <summary>Shows the text in a modal window.</summary>
    public static void Show(IWin32Window? owner, string text, bool topMost, Font mono)
    {
        using var f = new Form
        {
            Text = "Progress per disk",
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            ClientSize = new Size(560, 250),
            TopMost = topMost,
            ShowInTaskbar = false,
            MinimizeBox = false,
        };
        f.AutoScaleDimensions = new SizeF(96f, 96f);
        f.AutoScaleMode = AutoScaleMode.Dpi;
        var t = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            // The table is monospaced and aligned by spaces: no wrapping, and bars to reach what does not fit
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            Font = mono,
            Text = text,
            AccessibleName = "Progress per disk",
            BackColor = SystemColors.Window,
        };
        f.Controls.Add(t);
        f.Shown += (_, _) => t.Select(0, 0);
        if (owner is null) f.ShowDialog();
        else f.ShowDialog(owner);
    }
}
