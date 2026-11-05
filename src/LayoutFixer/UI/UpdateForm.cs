using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LayoutFixer.Diagnostics;
using LayoutFixer.Localization;
using LayoutFixer.Services;

namespace LayoutFixer.UI;

/// <summary>
/// Release notes and an explicit, cancellable download followed by replacement
/// of the running executable and restart in its existing directory.
/// </summary>
internal sealed class UpdateForm : Form
{
    private readonly UpdateInfo _update;
    private readonly Action<UpdateInfo> _skip;
    private readonly Action _exitForUpdate;
    private readonly bool _canInstall;
    private readonly Label _titleLabel = new();
    private readonly Label _versionLabel = new();
    private readonly Label _notesHeading = new();
    private readonly RichTextBox _notesBox = new();
    private readonly Label _howToLabel = new();
    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Button _downloadButton = new();
    private readonly Button _skipButton = new();
    private readonly Button _laterButton = new();
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 20000 };
    private CancellationTokenSource? _downloadCancellation;
    private bool _startingInstaller;
    private bool _installerStarted;
    private int _progress;

    public UpdateForm(UpdateInfo update, Action<UpdateInfo> skip, Action? exitForUpdate = null)
    {
        _update = update;
        _skip = skip;
        _exitForUpdate = exitForUpdate ?? Application.Exit;
        _canInstall = UpdateInstaller.CanInstall(update);

        Icon = AppIcons.Current;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(S(580), S(420));
        Size = new Size(S(680), S(540));
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
    public bool IsBusy => _downloadCancellation is not null;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The handoff lasts only a moment; do not close the window halfway
        // through starting the helper. Downloads themselves can be cancelled.
        if (_startingInstaller && !_installerStarted && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
        }
        else if (!_installerStarted)
        {
            _downloadCancellation?.Cancel();
        }

        base.OnFormClosing(e);
    }

    private int S(int pixels) => LogicalToDeviceUnits(pixels);

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(S(20), S(16), S(20), S(14)),
            RowCount = 7
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
        _notesBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        _notesBox.BorderStyle = BorderStyle.FixedSingle;
        _notesBox.BackColor = SystemColors.Window;
        _notesBox.ForeColor = SystemColors.WindowText;
        _notesBox.DetectUrls = true;
        _notesBox.LinkClicked += (_, args) =>
        {
            if (ReleaseNotesRenderer.IsWebLink(args.LinkText))
            {
                ProjectLinks.OpenUrl(args.LinkText!);
            }
        };

        _howToLabel.AutoSize = true;
        _howToLabel.ForeColor = SystemColors.GrayText;
        _howToLabel.Margin = new Padding(0, S(10), 0, S(4));
        root.SizeChanged += (_, _) => _howToLabel.MaximumSize = new Size(Math.Max(S(200), root.ClientSize.Width - S(44)), 0);

        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(0, S(4), 0, S(4));
        root.SizeChanged += (_, _) => _statusLabel.MaximumSize = new Size(Math.Max(S(200), root.ClientSize.Width - S(44)), 0);
        _progressBar.Dock = DockStyle.Top;
        _progressBar.Height = S(14);
        _progressBar.Visible = false;

        foreach (Button button in new[] { _downloadButton, _skipButton, _laterButton })
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(S(100), S(28));
            button.Margin = new Padding(S(8), S(3), 0, S(3));
        }

        _downloadButton.Click += async (_, _) =>
        {
            if (_canInstall)
            {
                await DownloadAndInstallAsync();
            }
            else
            {
                ProjectLinks.OpenUrl(_update.PageUrl);
            }
        };
        _skipButton.Click += (_, _) =>
        {
            _skip(_update);
            Close();
        };
        _laterButton.Click += (_, _) =>
        {
            if (IsBusy) _downloadCancellation?.Cancel();
            else Close();
        };
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
        root.Controls.Add(_statusLabel, 0, 4);
        root.Controls.Add(_progressBar, 0, 5);
        root.Controls.Add(buttons, 0, 6);
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
        string notes = string.IsNullOrWhiteSpace(_update.Notes) ? Localizer.Get("UpdateNoNotes") : _update.Notes;
        _notesBox.Rtf = ReleaseNotesRenderer.Render(notes, Font.FontFamily.Name, Font.SizeInPoints).Rtf;
        _notesBox.Select(0, 0);

        _howToLabel.Text = Localizer.Get(_canInstall ? "UpdateHowTo" : "UpdateManualHowTo");
        _downloadButton.Text = Localizer.Get(_canInstall ? "UpdateDownloadButton" : "UpdateReleasePageButton");
        _skipButton.Text = Localizer.Get("UpdateSkipButton");
        _laterButton.Text = Localizer.Get(IsBusy ? "UpdateCancelButton" : "UpdateLaterButton");
        _toolTip.SetToolTip(_downloadButton, Localizer.Get(_canInstall ? "UpdateDownloadTooltip" : "UpdateReleasePageTooltip"));
        _toolTip.SetToolTip(_skipButton, Localizer.Get("UpdateSkipTooltip"));
        _toolTip.SetToolTip(_laterButton, Localizer.Get(IsBusy ? "UpdateCancelTooltip" : "UpdateLaterTooltip"));
        if (IsBusy) UpdateProgressText();
    }

    private async Task DownloadAndInstallAsync()
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        _downloadButton.Enabled = false;
        _skipButton.Enabled = false;
        _progressBar.Value = _progress = 0;
        _progressBar.Visible = true;
        ApplyLocalization();

        try
        {
            var progress = new Progress<int>(value =>
            {
                if (IsDisposed || !ReferenceEquals(_downloadCancellation, cancellation)) return;
                _progressBar.Value = _progress = Math.Clamp(value, 0, 100);
                UpdateProgressText();
            });
            using PreparedUpdate prepared = await UpdateInstaller.PrepareAsync(_update, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _startingInstaller = true;
            _laterButton.Enabled = false;
            UpdateProgressText();
            await prepared.StartInstallAsync(cancellation.Token);
            _installerStarted = true;
            _exitForUpdate();
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) _statusLabel.Text = Localizer.Get("UpdateDownloadCancelled");
        }
        catch (Exception ex)
        {
            Logger.Log($"Update installation failed: {ex.Message}");
            if (!IsDisposed)
            {
                _statusLabel.Text = Localizer.Get("UpdateInstallFailed");
                _toolTip.SetToolTip(_statusLabel, ex.Message);
            }
        }
        finally
        {
            _downloadCancellation = null;
            _startingInstaller = false;
            if (!IsDisposed && !_installerStarted)
            {
                _downloadButton.Enabled = true;
                _skipButton.Enabled = true;
                _laterButton.Enabled = true;
                _progressBar.Visible = false;
                ApplyLocalization();
            }
        }
    }

    private void UpdateProgressText()
    {
        _statusLabel.Text = _startingInstaller
            ? Localizer.Get("UpdateInstalling")
            : Localizer.Format("UpdateDownloading", _progress);
    }

    // Plain-text representation for callers that do not display RichEdit content.
    internal static string CleanNotes(string notes)
    {
        return ReleaseNotesRenderer.Render(notes).Text;
    }

    private void HandleLanguageChanged(object? sender, EventArgs args)
    {
        ApplyLocalization();
    }
}
