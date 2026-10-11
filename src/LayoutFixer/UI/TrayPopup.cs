using System;
using System.Drawing;
using System.Windows.Forms;

namespace LayoutFixer.UI;

/// <summary>
/// A tiny, non-activating popup near the notification area that shows a short
/// status message and hides itself shortly after. Unlike NotifyIcon balloons,
/// each new message replaces the current one instantly (no multi-second queue),
/// so rapid hotkey presses always reflect the latest state.
/// </summary>
internal sealed class TrayPopup : Form
{
    // Default on-screen duration; overridable at runtime from settings so the
    // value is read every time a message is shown (ApplyPopupDuration sets this).
    internal static int CurrentDisplayMs = 5000;
    private const int EdgeMargin = 12;

    private readonly Label _label = new();
    private readonly Panel _accent = new();
    private readonly Timer _timer = new();

    public TrayPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(320, 54);
        BackColor = Color.FromArgb(43, 43, 46);

        _label.Dock = DockStyle.Fill;
        _label.ForeColor = Color.White;
        _label.Font = new Font("Segoe UI", 9F);
        _label.TextAlign = ContentAlignment.MiddleLeft;
        _label.Padding = new Padding(12, 0, 12, 0);
        _label.AutoEllipsis = true;

        _accent.Dock = DockStyle.Left;
        _accent.Width = 6;
        _accent.BackColor = Color.FromArgb(60, 180, 75);

        Controls.Add(_label);
        Controls.Add(_accent);

        _timer.Interval = CurrentDisplayMs;
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Hide();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: no taskbar button
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE: never steal focus
            return cp;
        }
    }

    public void ShowMessage(string text, ToolTipIcon icon)
    {
        if (IsDisposed)
        {
            return;
        }

        _label.Text = text;
        _accent.BackColor = icon switch
        {
            ToolTipIcon.Warning => Color.FromArgb(225, 170, 45),
            ToolTipIcon.Error => Color.FromArgb(225, 80, 80),
            _ => Color.FromArgb(60, 180, 75)
        };

        PositionNearTray();

        if (!Visible)
        {
            Show();
        }

        // Re-read the duration each time so a settings change takes effect on
        // the very next notification without recreating the form.
        _timer.Interval = CurrentDisplayMs;
        _timer.Stop();
        _timer.Start();
    }

    private void PositionNearTray()
    {
        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.VirtualScreen;
        int x = area.Right - Width - EdgeMargin;
        int y = area.Bottom - Height - EdgeMargin;
        Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
    }
}
