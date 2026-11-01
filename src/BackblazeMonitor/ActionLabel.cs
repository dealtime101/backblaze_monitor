namespace BackblazeMonitor;

/// <summary>
/// A label that acts like a button for the keyboard and for assistive technology: it takes the focus (Tab),
/// shows a focus rectangle, and Enter or Space activates it, like a click. A plain Label can do none of this.
/// </summary>
internal sealed class ActionLabel : Label
{
    public ActionLabel()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    /// <summary>Enter or Space pressed while the label has the focus.</summary>
    public event EventHandler? Activated;

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space && e.Modifiers == Keys.None)
        {
            e.Handled = true;
            Activated?.Invoke(this, EventArgs.Empty);
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
    }

    protected override void OnEnter(EventArgs e)
    {
        Invalidate();
        base.OnEnter(e);
    }

    protected override void OnLeave(EventArgs e)
    {
        Invalidate();
        base.OnLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }
}
