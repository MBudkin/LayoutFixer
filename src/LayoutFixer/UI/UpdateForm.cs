using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using LayoutFixer.Localization;
using LayoutFixer.Services;

namespace LayoutFixer.UI;

/// <summary>
/// "A new version is available": version, release notes and the way to get
/// it. The program never downloads or runs anything itself — the download
/// button opens the release page in the browser.
/// </summary>
internal sealed class UpdateForm : Form
{
    private readonly UpdateInfo _update;
    private readonly Action<UpdateInfo> _skip;
    private readonly Label _titleLabel = new();
    private readonly Label _versionLabel = new();
    private readonly Label _notesHeading = new();
    private readonly TextBox _notesBox = new();
    private readonly Label _howToLabel = new();
    private readonly Button _downloadButton = new();
    private readonly Button _skipButton = new();
    private readonly Button _laterButton = new();
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 20000 };

    public UpdateForm(UpdateInfo update, Action<UpdateInfo> skip)
    {
        _update = update;
        _skip = skip;

        Icon = AppIcons.Current;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(S(480), S(380));
        Size = new Size(S(560), S(480));
        MinimizeBox = false;
        MaximizeBox = false;

        BuildLayout();
        ApplyLocalization();
        Localizer.LanguageChanged += HandleLanguageChanged;
        FormClosed += (_, _) =>
        {
            Localizer.LanguageChanged -= HandleLanguageChanged;
            _toolTip.Dispose();
        };
    }

    public UpdateInfo Release => _update;

    private int S(int pixels) => LogicalToDeviceUnits(pixels);

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(S(20), S(16), S(20), S(14)),
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _titleLabel.AutoSize = true;
        _titleLabel.Font = new Font(Font.FontFamily, Font.Size * 1.45f, FontStyle.Bold);
        _titleLabel.Margin = new Padding(0, 0, 0, S(4));

        _versionLabel.AutoSize = true;
        _versionLabel.ForeColor = SystemColors.GrayText;
        _versionLabel.Margin = new Padding(0, 0, 0, S(12));

        _notesHeading.AutoSize = true;
        _notesHeading.Font = new Font(Font, FontStyle.Bold);
        _notesHeading.Margin = new Padding(0, 0, 0, S(4));

        _notesBox.Dock = DockStyle.Fill;
        _notesBox.Multiline = true;
        _notesBox.ReadOnly = true;
        _notesBox.ScrollBars = ScrollBars.Vertical;
        _notesBox.BackColor = SystemColors.Window;
        _notesBox.Text = CleanNotes(_update.Notes);

        _howToLabel.AutoSize = true;
        _howToLabel.ForeColor = SystemColors.GrayText;
        _howToLabel.Margin = new Padding(0, S(10), 0, S(4));
        root.SizeChanged += (_, _) => _howToLabel.MaximumSize = new Size(Math.Max(S(200), root.ClientSize.Width - S(44)), 0);

        foreach (Button button in new[] { _downloadButton, _skipButton, _laterButton })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(S(100), S(28));
            button.Margin = new Padding(S(8), S(3), 0, S(3));
        }

        _downloadButton.Click += (_, _) =>
        {
            ProjectLinks.OpenUrl(_update.PageUrl);
            Close();
        };
        _skipButton.Click += (_, _) =>
        {
            _skip(_update);
            Close();
        };
        _laterButton.Click += (_, _) => Close();
        AcceptButton = _downloadButton;
        CancelButton = _laterButton;

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, S(8), 0, 0),
            WrapContents = false
        };
        buttons.Controls.AddRange(new Control[] { _skipButton, _laterButton, _downloadButton });

        var header = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        header.Controls.Add(_titleLabel, 0, 0);
        header.Controls.Add(_versionLabel, 0, 1);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_notesHeading, 0, 1);
        root.Controls.Add(_notesBox, 0, 2);
        root.Controls.Add(_howToLabel, 0, 3);
        root.Controls.Add(buttons, 0, 4);
        Controls.Add(root);
        ActiveControl = _downloadButton;
    }

    private void ApplyLocalization()
    {
        Text = Localizer.Get("UpdateWindowTitle");
        _titleLabel.Text = Localizer.Format("UpdateTitle", _update.Version.ToString(3));
        string current = UpdateChecker.CurrentVersion.ToString(3);
        _versionLabel.Text = _update.PublishedAt is { } date
            ? Localizer.Format("UpdateVersionLineWithDate", current, date.ToLocalTime().ToString("d", Localizer.EffectiveCulture))
            : Localizer.Format("UpdateVersionLine", current);
        _notesHeading.Text = Localizer.Get("UpdateNotesHeading");
        if (_notesBox.TextLength == 0)
        {
            _notesBox.Text = Localizer.Get("UpdateNoNotes");
        }

        _howToLabel.Text = Localizer.Get("UpdateHowTo");
        _downloadButton.Text = Localizer.Get("UpdateDownloadButton");
        _skipButton.Text = Localizer.Get("UpdateSkipButton");
        _laterButton.Text = Localizer.Get("UpdateLaterButton");
        _toolTip.SetToolTip(_downloadButton, Localizer.Get("UpdateDownloadTooltip"));
        _toolTip.SetToolTip(_skipButton, Localizer.Get("UpdateSkipTooltip"));
        _toolTip.SetToolTip(_laterButton, Localizer.Get("UpdateLaterTooltip"));
    }

    // Release notes are Markdown; show them as readable plain text.
    internal static string CleanNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return string.Empty;
        }

        string text = notes.Replace("\r\n", "\n");
        text = Regex.Replace(text, @"^[ \t]{0,3}#{1,6}[ \t]*", string.Empty, RegexOptions.Multiline);
        text = Regex.Replace(text, @"^([ \t]*)[-*+][ \t]+", "$1• ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\*\*(.+?)\*\*|__(.+?)__", "$1$2");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\((?:[^)]+)\)", "$1");
        return text.Trim().Replace("\n", Environment.NewLine);
    }

    private void HandleLanguageChanged(object? sender, EventArgs args)
    {
        ApplyLocalization();
    }
}
