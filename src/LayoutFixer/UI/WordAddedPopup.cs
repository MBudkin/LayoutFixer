using System;
using System.Drawing;
using System.Windows.Forms;
using LayoutFixer.Input;
using LayoutFixer.Localization;

namespace LayoutFixer.UI;

/// <summary>
/// A small non-activating popup near the notification area that confirms a word
/// was added to the user dictionary and offers a one-click "Remove" to undo it.
/// Auto-dismisses after a few seconds (keeping the word). Replaces the previous
/// popup, like the status <see cref="TrayPopup"/>.
/// </summary>
internal sealed class WordAddedPopup : Form
{
    // Default on-screen duration; overridable at runtime from settings. Read in
    // OnShown so a settings change applies to the next popup.
    internal static int CurrentDisplayMs = 5000;
    private const int EdgeMargin = 12;

    private readonly Timer _timer = new();
    private readonly Label _label = new();
    private readonly Button _undoButton = new();

    public WordAddedPopup(string word, ConvertedLanguage language)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        MinimumSize = new Size(320, 0);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.FromArgb(43, 43, 46);

        _label.ForeColor = Color.White;
        _label.Font = new Font("Segoe UI", 9F);
        _label.TextAlign = ContentAlignment.MiddleLeft;
        _label.Padding = new Padding(12, 10, 12, 6);
        _label.AutoSize = true;
        _label.MaximumSize = new Size(380, 0); // wrap long words onto extra lines
        _label.Text = Localizer.Format("NotificationWordAddedToUserDictionary", word);

        _undoButton.Text = Localizer.Get("WordAddedPopupUndo");
        _undoButton.Size = new Size(96, 30);
        _undoButton.FlatStyle = FlatStyle.Flat;
        _undoButton.ForeColor = Color.White;
        _undoButton.BackColor = Color.FromArgb(60, 60, 64);
        _undoButton.FlatAppearance.BorderSize = 0;
        _undoButton.Font = new Font("Segoe UI", 9F);
        _undoButton.Cursor = Cursors.Hand;

        var footerLayout = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(12, 0, 12, 10),
            AutoSize = true
        };
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        footerLayout.Controls.Add(new Label(), 0, 0);
        footerLayout.Controls.Add(_undoButton, 1, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.Controls.Add(_label, 0, 0);
        root.Controls.Add(footerLayout, 0, 1);

        Controls.Add(root);

        _undoButton.Click += (_, _) => Finish(undone: true);

        _timer.Tick += (_, _) => Finish(undone: false);
    }

    public event EventHandler<bool>? Decision;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // AutoSize has now settled the final height; position afterwards.
        PositionNearTray();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Read the current setting when the popup actually appears, so duration
        // changes don't require restarting the app.
        _timer.Interval = CurrentDisplayMs;
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void Finish(bool undone)
    {
        _timer.Stop();
        Decision?.Invoke(this, undone);
        Decision = null;
        if (!IsDisposed)
        {
            Close();
        }
    }

    private void PositionNearTray()
    {
        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.VirtualScreen;
        int x = area.Right - Width - EdgeMargin;
        int y = area.Bottom - Height - EdgeMargin;
        Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
    }
}
