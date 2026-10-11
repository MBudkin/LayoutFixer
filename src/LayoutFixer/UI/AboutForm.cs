using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using LayoutFixer.Localization;
using LayoutFixer.Services;

namespace LayoutFixer.UI;

internal sealed class AboutForm : Form
{
    private readonly PictureBox _logoBox = new();
    private readonly Label _titleLabel = new();
    private readonly Label _versionLabel = new();
    private readonly Label _descriptionLabel = new();
    private readonly Label _usageHeadingLabel = new();
    private readonly Label _usageListLabel = new();
    private readonly Label _usageNoteLabel = new();
    private readonly Label _privacyHeadingLabel = new();
    private readonly Label _privacyLabel = new();
    private readonly Label _dataHeadingLabel = new();
    private readonly Label _dataListLabel = new();
    private readonly LinkLabel _gitHubLink = new();
    private readonly Button _closeButton = new();
    private readonly ToolTip _toolTip = new();
    private Image? _logo;

    public AboutForm()
    {
        InitializeControls();
        ApplyLocalization();
        Localizer.LanguageChanged += HandleLanguageChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Localizer.LanguageChanged -= HandleLanguageChanged;
            _toolTip.Dispose();
            _logo?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeControls()
    {
        MinimumSize = new Size(540, 380);
        Size = new Size(600, 520);
        Icon = AppIcons.Current;
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _titleLabel.AutoSize = true;
        _titleLabel.Font = new Font(Font, FontStyle.Bold);
        _titleLabel.Margin = new Padding(0, 0, 0, 4);

        _versionLabel.AutoSize = true;
        _versionLabel.Margin = new Padding(0, 0, 0, 0);

        var titleBlock = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill
        };
        titleBlock.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        titleBlock.Controls.Add(_titleLabel, 0, 0);
        titleBlock.Controls.Add(_versionLabel, 0, 1);

        _logo = LoadLogo();
        _logoBox.Size = new Size(64, 64);
        _logoBox.SizeMode = PictureBoxSizeMode.Zoom;
        _logoBox.Margin = new Padding(0, 0, 16, 0);
        if (_logo is not null)
        {
            _logoBox.Image = _logo;
        }

        var header = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Fill
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(_logoBox, 0, 0);
        header.Controls.Add(titleBlock, 1, 0);

        // Scrollable content area so longer descriptions never get clipped.
        var content = new Panel
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 14, 0, 14)
        };

        var contentLayout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Top
        };
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 8; i++)
        {
            contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        var headingFont = new Font(Font, FontStyle.Bold);

        _descriptionLabel.AutoSize = true;
        _descriptionLabel.MaximumSize = new Size(510, 0);
        _descriptionLabel.Margin = new Padding(0, 0, 0, 14);

        _usageHeadingLabel.AutoSize = true;
        _usageHeadingLabel.Font = headingFont;
        _usageHeadingLabel.MaximumSize = new Size(510, 0);
        _usageHeadingLabel.Margin = new Padding(0, 0, 0, 6);

        _usageListLabel.AutoSize = true;
        _usageListLabel.MaximumSize = new Size(510, 0);
        _usageListLabel.Margin = new Padding(12, 0, 0, 6);

        _usageNoteLabel.AutoSize = true;
        _usageNoteLabel.MaximumSize = new Size(510, 0);
        _usageNoteLabel.Margin = new Padding(0, 0, 0, 14);

        _privacyHeadingLabel.AutoSize = true;
        _privacyHeadingLabel.Font = headingFont;
        _privacyHeadingLabel.MaximumSize = new Size(510, 0);
        _privacyHeadingLabel.Margin = new Padding(0, 0, 0, 6);

        _privacyLabel.AutoSize = true;
        _privacyLabel.MaximumSize = new Size(510, 0);
        _privacyLabel.Margin = new Padding(0, 0, 0, 14);

        _dataHeadingLabel.AutoSize = true;
        _dataHeadingLabel.Font = headingFont;
        _dataHeadingLabel.MaximumSize = new Size(510, 0);
        _dataHeadingLabel.Margin = new Padding(0, 0, 0, 6);

        _dataListLabel.AutoSize = true;
        _dataListLabel.MaximumSize = new Size(510, 0);
        _dataListLabel.Margin = new Padding(12, 0, 0, 0);

        contentLayout.Controls.Add(_descriptionLabel, 0, 0);
        contentLayout.Controls.Add(_usageHeadingLabel, 0, 1);
        contentLayout.Controls.Add(_usageListLabel, 0, 2);
        contentLayout.Controls.Add(_usageNoteLabel, 0, 3);
        contentLayout.Controls.Add(_privacyHeadingLabel, 0, 4);
        contentLayout.Controls.Add(_privacyLabel, 0, 5);
        contentLayout.Controls.Add(_dataHeadingLabel, 0, 6);
        contentLayout.Controls.Add(_dataListLabel, 0, 7);
        content.Controls.Add(contentLayout);

        _gitHubLink.AutoSize = true;
        _gitHubLink.Dock = DockStyle.Fill;
        _gitHubLink.TextAlign = ContentAlignment.MiddleLeft;
        _gitHubLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _gitHubLink.LinkClicked += (_, _) => ProjectLinks.OpenGitHub();

        _closeButton.AutoSize = true;
        _closeButton.Anchor = AnchorStyles.Right;
        _closeButton.Click += (_, _) => Close();

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Fill
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_gitHubLink, 0, 0);
        footer.Controls.Add(_closeButton, 1, 0);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(content, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
    }

    private void ApplyLocalization()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? string.Empty;
        Text = Localizer.Get("AboutWindowTitle");
        _titleLabel.Text = Localizer.Get("AppName");
        _versionLabel.Text = Localizer.Format("AboutVersionLabel", version);
        _descriptionLabel.Text = Localizer.Get("AboutDescriptionText");
        _usageHeadingLabel.Text = Localizer.Get("AboutUsageHeading");
        _usageListLabel.Text = Localizer.Get("AboutUsageText");
        _usageNoteLabel.Text = Localizer.Get("AboutUsageNote");
        _privacyHeadingLabel.Text = Localizer.Get("AboutPrivacyHeading");
        _privacyLabel.Text = Localizer.Get("AboutPrivacyText");
        _dataHeadingLabel.Text = Localizer.Get("AboutDataHeading");
        _dataListLabel.Text = Localizer.Get("AboutDataText");
        _closeButton.Text = Localizer.Get("AboutCloseButton");
        _toolTip.SetToolTip(_closeButton, Localizer.Get("AboutCloseTooltip"));
        _gitHubLink.Text = Localizer.Get("AboutGitHubLink");
        _toolTip.SetToolTip(_gitHubLink, Localizer.Get("AboutGitHubTooltip"));
    }

    private static Image? LoadLogo()
    {
        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string[] names = assembly.GetManifestResourceNames();
            string? name = Array.Find(names, n => n.EndsWith("Logo1.png", StringComparison.Ordinal));
            if (name is null)
            {
                return null;
            }

            using System.IO.Stream? stream = assembly.GetManifestResourceStream(name);
            return stream is null ? null : Image.FromStream(stream);
        }
        catch
        {
            return null;
        }
    }

    private void HandleLanguageChanged(object? sender, EventArgs args)
    {
        ApplyLocalization();
    }
}
