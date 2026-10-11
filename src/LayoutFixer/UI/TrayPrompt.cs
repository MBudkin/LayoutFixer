using System;
using System.Drawing;
using System.Windows.Forms;
using LayoutFixer.Input;
using LayoutFixer.Localization;

namespace LayoutFixer.UI;

/// <summary>
/// A small non-activating popup near the notification area with a question and
/// two buttons ("add the word to the dictionary?", "a new version is
/// available"). It never takes the keyboard focus, so typing continues
/// undisturbed. Auto-dismisses after a while as if the user declined.
/// </summary>
internal sealed class TrayPrompt : Form
{
    // Default on-screen duration; overridable at runtime from settings. Read in
    // OnShown so a settings change applies to the next prompt.
    internal static int CurrentDisplayMs = 7000;
    private const int EdgeMargin = 12;

    private readonly Timer _timer = new();
    private readonly Label _label = new();
    private readonly Button _acceptButton = new();
    private readonly Button _declineButton = new();
    private readonly int _minimumDisplayMs;

    public TrayPrompt(string text, string acceptText, string declineText, int minimumDisplayMs = 0)
    {
        _minimumDisplayMs = minimumDisplayMs;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(LogicalToDeviceUnits(400), LogicalToDeviceUnits(120));
        BackColor = Color.FromArgb(43, 43, 46);

        _label.Dock = DockStyle.Top;
        _label.Height = LogicalToDeviceUnits(64);
        _label.ForeColor = Color.White;
        _label.Font = new Font("Segoe UI", 9F);
        _label.TextAlign = ContentAlignment.MiddleLeft;
        _label.Padding = new Padding(12, 8, 12, 4);
        _label.AutoEllipsis = true;
        _label.Text = text;

        _acceptButton.Text = acceptText;
        _declineButton.Text = declineText;
        int buttonWidth = LogicalToDeviceUnits(110);
        foreach (Button button in new[] { _declineButton, _acceptButton })
        {
            button.AutoSize = false;
            button.Size = new Size(buttonWidth, LogicalToDeviceUnits(30));
            button.Margin = new Padding(0, 0, 8, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.ForeColor = Color.White;
            button.BackColor = Color.FromArgb(60, 60, 64);
            button.FlatAppearance.BorderSize = 0;
            button.Font = new Font("Segoe UI", 9F);
            button.Cursor = Cursors.Hand;
        }

        _acceptButton.BackColor = Color.FromArgb(60, 180, 75);
        _acceptButton.Margin = new Padding(0);

        var footerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(12, 0, 12, 10)
        };
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, buttonWidth + 8));
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, buttonWidth));
        footerLayout.Controls.Add(new Label(), 0, 0);
        footerLayout.Controls.Add(_declineButton, 1, 0);
        footerLayout.Controls.Add(_acceptButton, 2, 0);

        Controls.Add(footerLayout);
        Controls.Add(_label);

        _acceptButton.Click += (_, _) => Finish(accepted: true);
        _declineButton.Click += (_, _) => Finish(accepted: false);

        _timer.Tick += (_, _) => Finish(accepted: false);

        PositionNearTray();
    }

    public event EventHandler<bool>? Decision;

    /// <summary>"Add «word» (Russian) to the user dictionary?"</summary>
    public static TrayPrompt AddToDictionary(string word, ConvertedLanguage language)
    {
        string languageLabel = language == ConvertedLanguage.Russian
            ? Localizer.Get("LanguageRussian")
            : Localizer.Get("LanguageEnglish");
        return new TrayPrompt(
            Localizer.Format("AddToDictionaryPromptText", word, languageLabel),
            Localizer.Get("AddToDictionaryYes"),
            Localizer.Get("AddToDictionaryNo"));
    }

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

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Read the current setting when the prompt appears, so duration changes
        // don't require restarting the app.
        _timer.Interval = Math.Max(CurrentDisplayMs, _minimumDisplayMs);
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void Finish(bool accepted)
    {
        _timer.Stop();
        Decision?.Invoke(this, accepted);
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
