using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using LayoutFixer.Diagnostics;
using LayoutFixer.Input;
using LayoutFixer.Localization;
using LayoutFixer.Services;
using LayoutFixer.Settings;

namespace LayoutFixer.UI;

internal enum SettingsPage
{
    General,
    Correction,
    Dictionaries,
    Exclusions,
    Hotkeys,
    Sound,
    Advanced
}

/// <summary>
/// The settings window: a navigation list on the left and one scrollable page
/// per topic on the right. Every change is saved immediately; the footer shows
/// the save status and the version.
/// </summary>
internal sealed class SettingsForm : Form
{
    // Remember the last opened page for the session.
    private static SettingsPage s_lastPage = SettingsPage.General;

    private readonly AppSettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly UserDictionary _userDictionary;
    private readonly UserReplacements _userReplacements;
    private readonly UpdateCoordinator _updates;
    private readonly Action _settingsChanged;
    private readonly Action _suspendHotkeys;
    private readonly Action _resumeHotkeys;
    private readonly Action _openUserDictionary;
    private readonly Action _openUserReplacements;
    private readonly Action<UpdateInfo> _showUpdate;
    private readonly Action<string> _notify;
    private readonly AutostartService _autostartService = new();
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 20000 };

    // Text/tooltip resource keys of static controls, applied by ApplyLocalization.
    private readonly List<(Control Control, string Key)> _texts = new();
    private readonly List<(Control Control, string Key)> _tips = new();
    private readonly List<Label> _wrappingLabels = new();

    private readonly ListBox _nav = new();
    private readonly Panel _pageHost = new();
    private readonly Label _pageTitle = new();
    private readonly Label _pageDescription = new();
    private readonly Dictionary<SettingsPage, Panel> _pages = new();

    // General
    private readonly CheckBox _startWithWindowsCheckBox = new();
    private readonly ComboBox _languageComboBox = new();
    private readonly RadioButton _trayIconProgramRadio = new();
    private readonly RadioButton _trayIconLanguageRadio = new();
    private readonly RadioButton _trayIconFlagRadio = new();
    private readonly CheckBox _showNotificationsCheckBox = new();
    private readonly NumericUpDown _notificationDurationUpDown = new();
    private readonly CheckBox _checkForUpdatesCheckBox = new();
    private readonly Button _checkUpdatesNowButton = new();
    private readonly LinkLabel _updateStatusLabel = new();

    // Correction
    private readonly CheckBox _autoModeCheckBox = new();
    private readonly CheckBox _tabAsSeparatorCheckBox = new();
    private readonly CheckBox _alwaysSingleLettersCheckBox = new();
    private readonly CheckBox _autoCapitalizeCheckBox = new();
    private readonly CheckBox _fixAccidentalCapsLockCheckBox = new();
    private readonly CheckBox _fixTwoCapitalsCheckBox = new();

    // Dictionaries & replacements
    private readonly Label _dictionarySummaryLabel = new();
    private readonly Button _openUserDictionaryButton = new();
    private readonly GroupBox _manualLearnGroup = new();
    private readonly RadioButton _manualLearnRememberRadio = new();
    private readonly RadioButton _manualLearnAskRadio = new();
    private readonly RadioButton _manualLearnNeverRadio = new();
    private readonly Label _manualLearnOffNote = new();
    private readonly GroupBox _selectionLearnGroup = new();
    private readonly RadioButton _selectionLearnAskRadio = new();
    private readonly RadioButton _selectionLearnNeverRadio = new();
    private readonly CheckBox _autoReplaceCheckBox = new();
    private readonly CheckBox _respectPunctuationCheckBox = new();
    private readonly Label _replacementsSummaryLabel = new();
    private readonly Button _openUserReplacementsButton = new();

    // Exclusions
    private readonly CheckBox _disableInFullscreenCheckBox = new();
    private readonly ListBox _excludedAppsList = new();
    private readonly ComboBox _excludedAppInput = new();
    private readonly Button _addExcludedAppButton = new();
    private readonly Button _removeExcludedAppButton = new();

    // Hotkeys
    private readonly TextBox _convertLastWordHotkeyTextBox = new();
    private readonly TextBox _convertSelectionHotkeyTextBox = new();
    private readonly TextBox _toggleEnabledHotkeyTextBox = new();
    private readonly TextBox _toggleCaseHotkeyTextBox = new();
    private readonly TextBox _transliterateHotkeyTextBox = new();
    private readonly CheckBox _invertEachCharacterCaseCheckBox = new();
    private readonly Button _restoreHotkeysButton = new();

    // Sound
    private readonly CheckBox _playSoundCheckBox = new();
    private readonly Label _soundVariantLabel = new();
    private readonly ComboBox _soundComboBox = new();
    private readonly Button _soundPreviewButton = new();
    private readonly Label _customSoundLabel = new();
    private readonly TextBox _customSoundTextBox = new();
    private readonly Button _customSoundBrowseButton = new();
    private readonly Label _volumeLabel = new();
    private readonly TrackBar _volumeTrackBar = new();
    private readonly Label _volumeValueLabel = new();

    // Advanced
    private readonly CheckBox _clearBufferCheckBox = new();
    private readonly CheckBox _debugLogCheckBox = new();

    // Footer
    private readonly Label _statusLabel = new();
    private readonly Label _versionLabel = new();
    private readonly Button _closeButton = new();

    private bool _updatingUi;
    private bool _lastManualCheckFailed;

    public SettingsForm(
        AppSettingsService settingsService,
        AppSettings settings,
        UserDictionary userDictionary,
        UserReplacements userReplacements,
        UpdateCoordinator updates,
        Action settingsChanged,
        Action suspendHotkeys,
        Action resumeHotkeys,
        Action openUserDictionary,
        Action openUserReplacements,
        Action<UpdateInfo> showUpdate,
        Action<string> notify)
    {
        _settingsService = settingsService;
        _settings = settings;
        _userDictionary = userDictionary;
        _userReplacements = userReplacements;
        _updates = updates;
        _settingsChanged = settingsChanged;
        _suspendHotkeys = suspendHotkeys;
        _resumeHotkeys = resumeHotkeys;
        _openUserDictionary = openUserDictionary;
        _openUserReplacements = openUserReplacements;
        _showUpdate = showUpdate;
        _notify = notify;

        InitializeControls();
        ApplySettingsToControls();
        ApplyLocalization();
        SelectPage(s_lastPage);

        Localizer.LanguageChanged += HandleLanguageChanged;
        _userDictionary.Changed += HandleDataChanged;
        _userReplacements.Changed += HandleDataChanged;
        _updates.StateChanged += HandleUpdateStateChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Localizer.LanguageChanged -= HandleLanguageChanged;
            _userDictionary.Changed -= HandleDataChanged;
            _userReplacements.Changed -= HandleDataChanged;
            _updates.StateChanged -= HandleUpdateStateChanged;
            _resumeHotkeys();
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    public void SelectPage(SettingsPage page)
    {
        int index = (int)page;
        if (index >= 0 && index < _nav.Items.Count && _nav.SelectedIndex != index)
        {
            _nav.SelectedIndex = index;
        }

        ShowPage(page);
    }

    /// <summary>Re-reads every control from the settings object (changed from the tray menu or a hotkey).</summary>
    public void RefreshFromSettings()
    {
        ApplySettingsToControls();
        UpdateLearnGroupTitles();
        UpdateDependentControls();
    }

    // Called by the tray context after the tray menu toggled a program.
    public void ReloadExcludedApps()
    {
        _excludedAppsList.BeginUpdate();
        _excludedAppsList.Items.Clear();
        foreach (string app in _settings.ExcludedApps)
        {
            _excludedAppsList.Items.Add(app);
        }

        _excludedAppsList.EndUpdate();
        _removeExcludedAppButton.Enabled = _excludedAppsList.SelectedIndices.Count > 0;
    }

    // ── layout ───────────────────────────────────────────────────────────────

    private int S(int pixels) => LogicalToDeviceUnits(pixels);

    private void InitializeControls()
    {
        Icon = AppIcons.Current;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(S(720), S(520));
        Size = new Size(S(820), S(640));
        BackColor = SystemColors.Window;

        BuildNavigation();

        _pageTitle.AutoSize = true;
        _pageTitle.Font = new Font(Font.FontFamily, Font.Size * 1.45f, FontStyle.Bold);
        _pageTitle.Margin = new Padding(0, 0, 0, S(2));

        _pageDescription.AutoSize = true;
        _pageDescription.ForeColor = SystemColors.GrayText;
        _pageDescription.Margin = new Padding(0, 0, 0, S(6));
        _wrappingLabels.Add(_pageDescription);

        var header = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Padding = new Padding(S(24), S(18), S(24), S(4))
        };
        header.Controls.Add(_pageTitle, 0, 0);
        header.Controls.Add(_pageDescription, 0, 1);

        _pageHost.Dock = DockStyle.Fill;
        _pageHost.SizeChanged += (_, _) => UpdateWrapWidths();

        foreach (SettingsPage page in Enum.GetValues<SettingsPage>())
        {
            var scroller = new Panel
            {
                AutoScroll = true,
                Dock = DockStyle.Fill,
                Padding = new Padding(S(24), S(4), S(16), S(16)),
                Visible = false
            };
            _pages[page] = scroller;
            _pageHost.Controls.Add(scroller);
        }

        BuildGeneralPage(Body(SettingsPage.General));
        BuildCorrectionPage(Body(SettingsPage.Correction));
        BuildDictionariesPage(Body(SettingsPage.Dictionaries));
        BuildExclusionsPage(Body(SettingsPage.Exclusions));
        BuildHotkeysPage(Body(SettingsPage.Hotkeys));
        BuildSoundPage(Body(SettingsPage.Sound));
        BuildAdvancedPage(Body(SettingsPage.Advanced));

        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(_pageHost);
        content.Controls.Add(header);

        var separator = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = SystemColors.ControlLight };
        var footer = BuildFooter();

        Controls.Add(content);
        Controls.Add(_nav);
        Controls.Add(separator);
        Controls.Add(footer);
    }

    private void BuildNavigation()
    {
        _nav.Dock = DockStyle.Left;
        _nav.Width = S(190);
        _nav.BorderStyle = BorderStyle.None;
        _nav.BackColor = SystemColors.Control;
        _nav.DrawMode = DrawMode.OwnerDrawFixed;
        _nav.ItemHeight = S(36);
        _nav.IntegralHeight = false;
        _nav.Padding = new Padding(0, S(8), 0, 0);
        foreach (SettingsPage page in Enum.GetValues<SettingsPage>())
        {
            _nav.Items.Add(page);
        }

        _nav.DrawItem += DrawNavItem;
        _nav.SelectedIndexChanged += (_, _) =>
        {
            if (_nav.SelectedIndex >= 0)
            {
                ShowPage((SettingsPage)_nav.SelectedIndex);
            }
        };
    }

    private void DrawNavItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
        {
            return;
        }

        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool highContrast = SystemInformation.HighContrast;
        Color back = selected
            ? highContrast ? SystemColors.Highlight : Color.FromArgb(218, 230, 246)
            : _nav.BackColor;
        Color fore = selected && highContrast ? SystemColors.HighlightText : SystemColors.ControlText;

        using (var brush = new SolidBrush(back))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }

        if (selected)
        {
            using var accent = new SolidBrush(highContrast ? SystemColors.HighlightText : Color.FromArgb(0, 103, 192));
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top + S(8), S(3), e.Bounds.Height - S(16));
        }

        string text = Localizer.Get(PageTitleKey((SettingsPage)e.Index));
        Rectangle textBounds = Rectangle.FromLTRB(e.Bounds.Left + S(16), e.Bounds.Top, e.Bounds.Right - S(4), e.Bounds.Bottom);
        using Font font = selected ? new Font(_nav.Font, FontStyle.Bold) : new Font(_nav.Font, FontStyle.Regular);
        TextRenderer.DrawText(
            e.Graphics,
            text,
            font,
            textBounds,
            fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 3,
            Dock = DockStyle.Bottom,
            Padding = new Padding(S(16), S(10), S(16), S(12))
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _statusLabel.AutoSize = false;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.ForeColor = SystemColors.GrayText;

        _versionLabel.AutoSize = true;
        _versionLabel.Anchor = AnchorStyles.Right;
        _versionLabel.Margin = new Padding(S(12), 0, S(12), 0);
        _versionLabel.ForeColor = SystemColors.GrayText;

        _closeButton.AutoSize = true;
        _closeButton.MinimumSize = new Size(S(96), 0);
        _closeButton.Click += (_, _) => Close();
        Register(_closeButton, "SettingsCloseButton", "SettingsCloseTooltip");
        CancelButton = _closeButton;

        footer.Controls.Add(_statusLabel, 0, 0);
        footer.Controls.Add(_versionLabel, 1, 0);
        footer.Controls.Add(_closeButton, 2, 0);
        return footer;
    }

    private TableLayoutPanel Body(SettingsPage page)
    {
        var body = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _pages[page].Controls.Add(body);
        return body;
    }

    private void ShowPage(SettingsPage page)
    {
        s_lastPage = page;
        foreach ((SettingsPage key, Panel panel) in _pages)
        {
            panel.Visible = key == page;
        }

        _pageTitle.Text = Localizer.Get(PageTitleKey(page));
        _pageDescription.Text = Localizer.Get(PageTitleKey(page) + "Description");
        UpdateWrapWidths();
    }

    private static string PageTitleKey(SettingsPage page) => "SettingsPage" + page;

    // Wrapped labels need an explicit maximum width; follow the window size.
    private void UpdateWrapWidths()
    {
        int width = Math.Max(S(200), _pageHost.ClientSize.Width - S(64));
        foreach (Label label in _wrappingLabels)
        {
            int indent = label.Margin.Left;
            label.MaximumSize = new Size(Math.Max(S(120), width - indent), 0);
        }
    }

    private void Register(Control control, string? textKey, string? tooltipKey = null)
    {
        if (textKey is not null)
        {
            _texts.Add((control, textKey));
        }

        if (tooltipKey is not null)
        {
            _tips.Add((control, tooltipKey));
        }
    }

    private Label AddSection(TableLayoutPanel body, string key)
    {
        var label = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, body.Controls.Count == 0 ? S(6) : S(18), 0, S(6))
        };
        Register(label, key);
        body.Controls.Add(label);
        return label;
    }

    private Label AddNote(TableLayoutPanel body, string key, int indent = 0, Label? label = null)
    {
        label ??= new Label();
        label.AutoSize = true;
        label.ForeColor = SystemColors.GrayText;
        label.Margin = new Padding(indent, 0, 0, S(6));
        Register(label, key);
        _wrappingLabels.Add(label);
        body.Controls.Add(label);
        return label;
    }

    private CheckBox AddCheck(TableLayoutPanel body, CheckBox checkBox, string key, string tooltipKey, int indent = 0)
    {
        checkBox.AutoSize = true;
        checkBox.Margin = new Padding(indent, S(3), 0, S(3));
        checkBox.CheckedChanged += HandleGeneralSettingChanged;
        Register(checkBox, key, tooltipKey);
        body.Controls.Add(checkBox);
        return checkBox;
    }

    private static FlowLayoutPanel AddRow(TableLayoutPanel body, int indent, params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(indent, 0, 0, 0),
            WrapContents = true
        };
        row.Controls.AddRange(controls);
        body.Controls.Add(row);
        return row;
    }

    private Label RowLabel(string key, string? tooltipKey = null)
    {
        var label = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, S(7), S(8), S(3))
        };
        Register(label, key, tooltipKey);
        return label;
    }

    private Button MakeButton(Button button, string key, string? tooltipKey, Action onClick)
    {
        button.AutoSize = true;
        button.MinimumSize = new Size(S(96), 0);
        button.Margin = new Padding(0, S(3), S(8), S(3));
        button.Click += (_, _) => onClick();
        Register(button, key, tooltipKey);
        return button;
    }

    // ── pages ────────────────────────────────────────────────────────────────

    private void BuildGeneralPage(TableLayoutPanel body)
    {
        AddSection(body, "SettingsSectionStartup");
        AddCheck(body, _startWithWindowsCheckBox, "SettingsStartWithWindows", "SettingsStartWithWindowsTooltip");

        _languageComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _languageComboBox.Width = S(220);
        _languageComboBox.Margin = new Padding(0, S(3), 0, S(3));
        _languageComboBox.SelectedIndexChanged += HandleLanguageSelectionChanged;
        Register(_languageComboBox, null, "SettingsInterfaceLanguageTooltip");
        AddRow(body, 0, RowLabel("SettingsInterfaceLanguageLabel"), _languageComboBox);

        foreach (RadioButton radio in new[] { _trayIconLanguageRadio, _trayIconFlagRadio, _trayIconProgramRadio })
        {
            radio.AutoSize = true;
            radio.Margin = new Padding(0, S(6), S(14), S(3));
            radio.CheckedChanged += HandleGeneralSettingChanged;
        }

        Register(_trayIconLanguageRadio, "SettingsTrayIconLanguage", "SettingsTrayIconLanguageTooltip");
        Register(_trayIconFlagRadio, "SettingsTrayIconFlag", "SettingsTrayIconFlagTooltip");
        Register(_trayIconProgramRadio, "SettingsTrayIconProgram", "SettingsTrayIconProgramTooltip");
        // The radio buttons share one container, which makes them one group.
        AddRow(body, 0, RowLabel("SettingsTrayIconGroup", "SettingsTrayIconTooltip"), _trayIconLanguageRadio, _trayIconFlagRadio, _trayIconProgramRadio);

        AddSection(body, "SettingsSectionNotifications");
        AddCheck(body, _showNotificationsCheckBox, "SettingsShowNotifications", "SettingsShowNotificationsTooltip");
        _notificationDurationUpDown.Minimum = 1;
        _notificationDurationUpDown.Maximum = 300;
        _notificationDurationUpDown.Width = S(64);
        _notificationDurationUpDown.Margin = new Padding(0, S(3), S(6), S(3));
        _notificationDurationUpDown.ValueChanged += HandleGeneralSettingChanged;
        Register(_notificationDurationUpDown, null, "SettingsNotificationDurationTooltip");
        AddRow(body, 0, RowLabel("SettingsNotificationDurationLabel", "SettingsNotificationDurationTooltip"), _notificationDurationUpDown, RowLabel("SettingsNotificationDurationUnit"));

        AddSection(body, "SettingsSectionUpdates");
        AddCheck(body, _checkForUpdatesCheckBox, "SettingsCheckForUpdates", "SettingsCheckForUpdatesTooltip");
        MakeButton(_checkUpdatesNowButton, "SettingsCheckUpdatesNowButton", "SettingsCheckUpdatesNowTooltip", CheckUpdatesNow);
        _updateStatusLabel.AutoSize = true;
        _updateStatusLabel.Margin = new Padding(0, S(8), 0, S(3));
        _updateStatusLabel.LinkBehavior = LinkBehavior.HoverUnderline;
        _updateStatusLabel.LinkClicked += (_, _) =>
        {
            if (_updates.AvailableUpdate is { } update)
            {
                _showUpdate(update);
            }
        };
        AddRow(body, 0, _checkUpdatesNowButton, _updateStatusLabel);
        AddNote(body, "SettingsUpdatesPrivacyNote");
    }

    private void BuildCorrectionPage(TableLayoutPanel body)
    {
        AddSection(body, "SettingsSectionLayout");
        AddCheck(body, _autoModeCheckBox, "SettingsAutoMode", "SettingsAutoModeTooltip");
        AddNote(body, "SettingsAutoModeNote", S(20));
        AddCheck(body, _alwaysSingleLettersCheckBox, "SettingsAlwaysConvertSingleLetters", "SettingsAlwaysConvertSingleLettersTooltip");
        AddCheck(body, _tabAsSeparatorCheckBox, "SettingsTabAsSeparator", "SettingsTabAsSeparatorTooltip");

        AddSection(body, "SettingsSectionCase");
        AddCheck(body, _autoCapitalizeCheckBox, "SettingsAutoCapitalizeSentences", "SettingsAutoCapitalizeSentencesTooltip");
        AddCheck(body, _fixAccidentalCapsLockCheckBox, "SettingsFixAccidentalCapsLock", "SettingsFixAccidentalCapsLockTooltip");
        AddCheck(body, _fixTwoCapitalsCheckBox, "SettingsFixTwoInitialCapitals", "SettingsFixTwoInitialCapitalsTooltip");
    }

    private void BuildDictionariesPage(TableLayoutPanel body)
    {
        AddSection(body, "SettingsSectionUserDictionary");
        AddNote(body, "SettingsUserDictionaryNote");
        _dictionarySummaryLabel.AutoSize = true;
        _dictionarySummaryLabel.Margin = new Padding(0, S(8), S(12), S(3));
        MakeButton(_openUserDictionaryButton, "SettingsOpenUserDictionaryButton", "SettingsOpenUserDictionaryButtonTooltip", () => _openUserDictionary());
        AddRow(body, 0, _openUserDictionaryButton, _dictionarySummaryLabel);

        BuildLearnGroup(
            body,
            _manualLearnGroup,
            "SettingsManualLearnGroupTooltip",
            (_manualLearnRememberRadio, "SettingsManualLearnRemember", "SettingsManualLearnRememberTooltip"),
            (_manualLearnAskRadio, "SettingsManualLearnAsk", "SettingsManualLearnAskTooltip"),
            (_manualLearnNeverRadio, "SettingsManualLearnNever", "SettingsManualLearnNeverTooltip"));
        AddNote(body, "SettingsManualLearnOffNote", S(4), _manualLearnOffNote);
        BuildLearnGroup(
            body,
            _selectionLearnGroup,
            "SettingsSelectionLearnGroupTooltip",
            (_selectionLearnAskRadio, "SettingsSelectionLearnAsk", "SettingsSelectionLearnAskTooltip"),
            (_selectionLearnNeverRadio, "SettingsSelectionLearnNever", "SettingsSelectionLearnNeverTooltip"));

        AddSection(body, "SettingsSectionReplacements");
        AddCheck(body, _autoReplaceCheckBox, "SettingsAutoReplace", "SettingsAutoReplaceTooltip");
        AddCheck(body, _respectPunctuationCheckBox, "UserReplacementsRespectPunctuation", "UserReplacementsRespectPunctuationTooltip", S(20));
        _replacementsSummaryLabel.AutoSize = true;
        _replacementsSummaryLabel.Margin = new Padding(0, S(8), S(12), S(3));
        MakeButton(_openUserReplacementsButton, "SettingsOpenUserReplacementsButton", "SettingsOpenUserReplacementsButtonTooltip", () => _openUserReplacements());
        AddRow(body, 0, _openUserReplacementsButton, _replacementsSummaryLabel);
    }

    private void BuildLearnGroup(
        TableLayoutPanel body,
        GroupBox group,
        string tooltipKey,
        params (RadioButton Radio, string Key, string TooltipKey)[] options)
    {
        group.AutoSize = true;
        group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        group.Dock = DockStyle.Fill;
        group.Margin = new Padding(0, S(10), 0, S(2));
        group.Padding = new Padding(S(8), S(4), S(8), S(4));
        Register(group, null, tooltipKey);

        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };

        foreach ((RadioButton radio, string key, string tip) in options)
        {
            radio.AutoSize = true;
            radio.Margin = new Padding(S(4), S(3), S(14), S(3));
            radio.CheckedChanged += HandleGeneralSettingChanged;
            Register(radio, key, tip);
            flow.Controls.Add(radio);
        }

        group.Controls.Add(flow);
        body.Controls.Add(group);
    }

    private void BuildExclusionsPage(TableLayoutPanel body)
    {
        AddSection(body, "SettingsSectionAlwaysOff");
        AddCheck(body, _disableInFullscreenCheckBox, "SettingsDisableInFullscreen", "SettingsDisableInFullscreenTooltip");
        AddNote(body, "SettingsPasswordFieldsNote");

        AddSection(body, "SettingsSectionExcludedApps");
        AddNote(body, "SettingsExcludedAppsLabel");

        _excludedAppInput.DropDownStyle = ComboBoxStyle.DropDown;
        _excludedAppInput.Width = S(260);
        _excludedAppInput.Margin = new Padding(0, S(4), S(8), S(3));
        _excludedAppInput.DropDown += (_, _) => FillRunningApps();
        _excludedAppInput.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                AddExcludedApp();
                args.SuppressKeyPress = true;
            }
        };
        Register(_excludedAppInput, null, "SettingsExcludedAppsInputTooltip");
        MakeButton(_addExcludedAppButton, "SettingsExcludedAppsAdd", null, AddExcludedApp);
        MakeButton(_removeExcludedAppButton, "SettingsExcludedAppsRemove", null, RemoveExcludedApp);
        AddRow(body, 0, _excludedAppInput, _addExcludedAppButton, _removeExcludedAppButton);

        _excludedAppsList.Dock = DockStyle.Fill;
        _excludedAppsList.Height = S(200);
        _excludedAppsList.IntegralHeight = false;
        _excludedAppsList.SelectionMode = SelectionMode.MultiExtended;
        _excludedAppsList.Margin = new Padding(0, S(6), 0, 0);
        _excludedAppsList.SelectedIndexChanged += (_, _) =>
            _removeExcludedAppButton.Enabled = _excludedAppsList.SelectedIndices.Count > 0;
        _excludedAppsList.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Delete)
            {
                RemoveExcludedApp();
            }
        };
        body.Controls.Add(_excludedAppsList);
    }

    private void BuildHotkeysPage(TableLayoutPanel body)
    {
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, S(6), 0, 0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(200)));

        AddHotkeyRow(table, "SettingsHotkeyConvertLastWord", _convertLastWordHotkeyTextBox, value => _settings.Hotkeys.ConvertLastWord = value);
        AddHotkeyRow(table, "SettingsHotkeyConvertSelection", _convertSelectionHotkeyTextBox, value => _settings.Hotkeys.ConvertSelection = value);
        AddHotkeyRow(table, "SettingsHotkeyToggleEnabled", _toggleEnabledHotkeyTextBox, value => _settings.Hotkeys.ToggleEnabled = value);
        AddHotkeyRow(table, "SettingsHotkeyToggleCase", _toggleCaseHotkeyTextBox, value => _settings.Hotkeys.ToggleCase = value);
        AddHotkeyRow(table, "SettingsHotkeyTransliterate", _transliterateHotkeyTextBox, value => _settings.Hotkeys.Transliterate = value);
        body.Controls.Add(table);

        AddNote(body, "SettingsHotkeyHint").Margin = new Padding(0, S(8), 0, S(4));
        MakeButton(_restoreHotkeysButton, "SettingsRestoreHotkeysButton", "SettingsRestoreHotkeysTooltip", RestoreDefaultHotkeys);
        AddRow(body, 0, _restoreHotkeysButton);

        AddSection(body, "SettingsSectionCaseHotkey");
        AddCheck(body, _invertEachCharacterCaseCheckBox, "SettingsInvertEachCharacterCase", "SettingsInvertEachCharacterCaseTooltip");
    }

    private void AddHotkeyRow(TableLayoutPanel table, string key, TextBox textBox, Action<string> updateSetting)
    {
        Label label = RowLabel(key);
        label.Margin = new Padding(0, S(7), S(16), S(3));

        textBox.Dock = DockStyle.Fill;
        textBox.ReadOnly = true;
        textBox.BackColor = SystemColors.Window;
        textBox.TextAlign = HorizontalAlignment.Center;
        textBox.Margin = new Padding(0, S(4), 0, S(4));
        textBox.Cursor = Cursors.Hand;
        textBox.Enter += (_, _) =>
        {
            _suspendHotkeys();
            textBox.SelectAll();
        };
        textBox.Leave += (_, _) => _resumeHotkeys();
        textBox.KeyDown += (_, args) => CaptureHotkey(args, textBox, updateSetting);
        Register(textBox, null, "SettingsHotkeyTextBoxTooltip");

        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(label, 0, row);
        table.Controls.Add(textBox, 1, row);
    }

    private void BuildSoundPage(TableLayoutPanel body)
    {
        _playSoundCheckBox.AutoSize = true;
        _playSoundCheckBox.Margin = new Padding(0, S(6), 0, S(6));
        _playSoundCheckBox.CheckedChanged += HandleSoundChanged;
        Register(_playSoundCheckBox, "SettingsPlaySound", "SettingsPlaySoundTooltip");
        body.Controls.Add(_playSoundCheckBox);

        var table = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Margin = new Padding(S(20), 0, 0, 0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach ((Label label, string key) in new[]
                 {
                     (_soundVariantLabel, "SettingsSoundVariantLabel"),
                     (_customSoundLabel, "SettingsSoundCustomPathLabel"),
                     (_volumeLabel, "SettingsSoundVolumeLabel")
                 })
        {
            label.AutoSize = true;
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(0, S(4), S(10), S(4));
            Register(label, key);
        }

        _soundComboBox.Dock = DockStyle.Fill;
        _soundComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _soundComboBox.Margin = new Padding(0, S(4), 0, S(4));
        // Placeholder items so ApplySoundControls can select an index before
        // RebuildSoundItems localizes them.
        _soundComboBox.Items.AddRange(new object[] { "1", "2", "3" });
        _soundComboBox.SelectedIndex = 0;
        _soundComboBox.SelectedIndexChanged += HandleSoundChanged;
        Register(_soundComboBox, null, "SettingsSoundVariantTooltip");

        MakeButton(_soundPreviewButton, "SettingsSoundPreviewButton", "SettingsSoundPreviewTooltip", PreviewSound);
        _soundPreviewButton.Margin = new Padding(S(8), S(3), 0, S(3));

        _customSoundTextBox.Dock = DockStyle.Fill;
        _customSoundTextBox.ReadOnly = true;
        _customSoundTextBox.Margin = new Padding(0, S(4), 0, S(4));
        MakeButton(_customSoundBrowseButton, "SettingsSoundBrowseButton", "SettingsSoundBrowseTooltip", BrowseCustomSound);
        _customSoundBrowseButton.Margin = new Padding(S(8), S(3), 0, S(3));

        _volumeTrackBar.Dock = DockStyle.Fill;
        _volumeTrackBar.Minimum = 0;
        _volumeTrackBar.Maximum = 100;
        _volumeTrackBar.TickFrequency = 10;
        _volumeTrackBar.SmallChange = 1;
        _volumeTrackBar.LargeChange = 10;
        _volumeTrackBar.Margin = new Padding(0, S(6), 0, S(6));
        _volumeTrackBar.Scroll += (_, _) => HandleSoundChanged(null, EventArgs.Empty);
        Register(_volumeTrackBar, null, "SettingsSoundVolumeTooltip");

        _volumeValueLabel.AutoSize = true;
        _volumeValueLabel.Anchor = AnchorStyles.Left;
        _volumeValueLabel.Margin = new Padding(S(8), 0, 0, 0);

        table.Controls.Add(_soundVariantLabel, 0, 0);
        table.Controls.Add(_soundComboBox, 1, 0);
        table.Controls.Add(_soundPreviewButton, 2, 0);
        table.Controls.Add(_customSoundLabel, 0, 1);
        table.Controls.Add(_customSoundTextBox, 1, 1);
        table.Controls.Add(_customSoundBrowseButton, 2, 1);
        table.Controls.Add(_volumeLabel, 0, 2);
        table.Controls.Add(_volumeTrackBar, 1, 2);
        table.Controls.Add(_volumeValueLabel, 2, 2);
        body.Controls.Add(table);
    }

    private void BuildAdvancedPage(TableLayoutPanel body)
    {
        AddSection(body, "SettingsSectionBackup");
        AddNote(body, "SettingsBackupNote");
        AddRow(
            body,
            0,
            MakeButton(new Button(), "SettingsExportAllButton", "SettingsExportAllTooltip", ExportAll),
            MakeButton(new Button(), "SettingsImportAllButton", "SettingsImportAllTooltip", ImportAll));

        AddSection(body, "SettingsSectionInput");
        AddCheck(body, _clearBufferCheckBox, "SettingsClearBufferAfterConversion", "SettingsClearBufferAfterConversionTooltip");

        AddSection(body, "SettingsSectionDiagnostics");
        AddCheck(body, _debugLogCheckBox, "SettingsDebugLog", "SettingsDebugLogTooltip");
        AddRow(
            body,
            0,
            MakeButton(new Button(), "SettingsOpenDebugLogButton", "SettingsOpenDebugLogTooltip", OpenDebugLog),
            MakeButton(new Button(), "SettingsOpenDataFolderButton", "SettingsOpenDataFolderTooltip", () => ProjectLinks.OpenFolder(AppPaths.DataDirectory)));

        AddSection(body, "SettingsSectionFeedback");
        AddRow(
            body,
            0,
            MakeButton(new Button(), "ReportIssueButton", "ReportIssueTooltip", ProjectLinks.OpenIssues),
            MakeButton(new Button(), "SettingsProjectPageButton", "AboutGitHubTooltip", ProjectLinks.OpenGitHub));
    }

    // ── settings <-> controls ────────────────────────────────────────────────

    private void ApplySettingsToControls()
    {
        bool wasUpdating = _updatingUi;
        _updatingUi = true;
        _startWithWindowsCheckBox.Checked = _settings.StartWithWindows;
        _showNotificationsCheckBox.Checked = _settings.ShowNotifications;
        _notificationDurationUpDown.Value = Math.Clamp(_settings.NotificationDurationSec, 1, 300);
        _trayIconProgramRadio.Checked = _settings.TrayIconMode == TrayIconMode.Program;
        _trayIconLanguageRadio.Checked = _settings.TrayIconMode == TrayIconMode.Language;
        _trayIconFlagRadio.Checked = _settings.TrayIconMode == TrayIconMode.Flag;
        _checkForUpdatesCheckBox.Checked = _settings.CheckForUpdates;

        _autoModeCheckBox.Checked = _settings.AutoMode;
        _alwaysSingleLettersCheckBox.Checked = _settings.AlwaysConvertSingleLetters;
        _tabAsSeparatorCheckBox.Checked = _settings.TabAsWordSeparator;
        _autoCapitalizeCheckBox.Checked = _settings.AutoCapitalizeSentences;
        _fixAccidentalCapsLockCheckBox.Checked = _settings.FixAccidentalCapsLock;
        _fixTwoCapitalsCheckBox.Checked = _settings.FixTwoInitialCapitals;

        _manualLearnRememberRadio.Checked = _settings.ManualLearnMode == RememberMode.Remember;
        _manualLearnAskRadio.Checked = _settings.ManualLearnMode == RememberMode.Ask;
        _manualLearnNeverRadio.Checked = _settings.ManualLearnMode == RememberMode.Never;
        // Selection learning only offers Ask/Never.
        _selectionLearnAskRadio.Checked = _settings.SelectionLearnMode != RememberMode.Never;
        _selectionLearnNeverRadio.Checked = _settings.SelectionLearnMode == RememberMode.Never;
        _autoReplaceCheckBox.Checked = _settings.AutoReplace;
        _respectPunctuationCheckBox.Checked = _settings.ReplacementsRespectPunctuation;

        _disableInFullscreenCheckBox.Checked = _settings.DisableInFullscreenApps;
        ReloadExcludedApps();

        _convertLastWordHotkeyTextBox.Text = _settings.Hotkeys.ConvertLastWord;
        _convertSelectionHotkeyTextBox.Text = _settings.Hotkeys.ConvertSelection;
        _toggleEnabledHotkeyTextBox.Text = _settings.Hotkeys.ToggleEnabled;
        _toggleCaseHotkeyTextBox.Text = _settings.Hotkeys.ToggleCase;
        _transliterateHotkeyTextBox.Text = _settings.Hotkeys.Transliterate;
        _invertEachCharacterCaseCheckBox.Checked = _settings.InvertEachCharacterCase;

        _playSoundCheckBox.Checked = _settings.PlaySoundOnConversion;
        _soundComboBox.SelectedIndex = _settings.SoundVariant switch
        {
            "sound2" => 1,
            "custom" => 2,
            _ => 0
        };
        _customSoundTextBox.Text = _settings.CustomSoundPath ?? string.Empty;
        _volumeTrackBar.Value = Math.Clamp(_settings.SoundVolume, 0, 100);

        _clearBufferCheckBox.Checked = _settings.ClearBufferAfterConversion;
        _debugLogCheckBox.Checked = _settings.EnableDebugLog;
        _updatingUi = wasUpdating;
        UpdateDependentControls();
    }

    // Enables/disables options that only matter while another one is on.
    private void UpdateDependentControls()
    {
        _notificationDurationUpDown.Enabled = true;
        _respectPunctuationCheckBox.Enabled = _autoReplaceCheckBox.Checked;
        // Invariant 12: with auto-correction off the last-word hotkey never learns.
        _manualLearnGroup.Enabled = _autoModeCheckBox.Checked;
        _manualLearnOffNote.Visible = !_autoModeCheckBox.Checked;
        SyncSoundControls();
    }

    private void SyncSoundControls()
    {
        _volumeValueLabel.Text = Localizer.Format("SettingsSoundVolumeValue", _volumeTrackBar.Value);
        bool enabled = _playSoundCheckBox.Checked;
        bool isCustom = _soundComboBox.SelectedIndex == 2;
        _soundVariantLabel.Enabled = enabled;
        _soundComboBox.Enabled = enabled;
        _soundPreviewButton.Enabled = enabled;
        _volumeLabel.Enabled = enabled;
        _volumeTrackBar.Enabled = enabled;
        _volumeValueLabel.Enabled = enabled;
        _customSoundLabel.Enabled = enabled && isCustom;
        _customSoundTextBox.Enabled = enabled && isCustom;
        _customSoundBrowseButton.Enabled = enabled && isCustom;
    }

    private void UpdateLearnGroupTitles()
    {
        _manualLearnGroup.Text = Localizer.Format("SettingsManualLearnGroup", _settings.Hotkeys.ConvertLastWord);
        _selectionLearnGroup.Text = Localizer.Format("SettingsSelectionLearnGroup", _settings.Hotkeys.ConvertSelection);
    }

    private void UpdateDataSummary()
    {
        _dictionarySummaryLabel.Text = Localizer.Format(
            "SettingsUserDictionarySummary",
            _userDictionary.Count(ConvertedLanguage.Russian),
            _userDictionary.Count(ConvertedLanguage.English));
        _replacementsSummaryLabel.Text = Localizer.Format("SettingsReplacementsSummary", _userReplacements.Count);
    }

    private void UpdateUpdateStatus()
    {
        string text;
        bool link = false;
        if (_updates.IsChecking)
        {
            text = Localizer.Get("UpdateStatusChecking");
        }
        else if (_updates.AvailableUpdate is { } update)
        {
            text = Localizer.Format("UpdateStatusAvailable", update.Version.ToString(3));
            link = true;
        }
        else if (_lastManualCheckFailed || (_updates.LastCheckFailed && _settings.LastUpdateCheckUtc is null))
        {
            text = Localizer.Get("UpdateStatusFailed");
        }
        else if (_settings.LastUpdateCheckUtc is { } last)
        {
            text = Localizer.Format("UpdateStatusUpToDate", last.ToLocalTime().ToString("g", Localizer.EffectiveCulture));
        }
        else
        {
            text = Localizer.Get("UpdateStatusNever");
        }

        _updateStatusLabel.Text = text;
        _updateStatusLabel.LinkArea = link ? new LinkArea(0, text.Length) : new LinkArea(0, 0);
        _updateStatusLabel.ForeColor = SystemColors.ControlText;
        _checkUpdatesNowButton.Enabled = !_updates.IsChecking;
    }

    private void ApplyLocalization()
    {
        bool wasUpdating = _updatingUi;
        _updatingUi = true;

        Text = Localizer.Get("SettingsWindowTitle");
        foreach ((Control control, string key) in _texts)
        {
            control.Text = Localizer.Get(key);
        }

        foreach ((Control control, string key) in _tips)
        {
            _toolTip.SetToolTip(control, Localizer.Get(key));
        }

        UpdateLearnGroupTitles();
        UpdateDataSummary();
        UpdateUpdateStatus();
        RebuildSoundItems();
        SyncSoundControls();
        RebuildLanguageItems();
        _versionLabel.Text = Localizer.Format("AboutVersionLabel", UpdateChecker.CurrentVersion.ToString(3));
        _nav.Invalidate();
        if (_nav.SelectedIndex >= 0)
        {
            ShowPage((SettingsPage)_nav.SelectedIndex);
        }

        _updatingUi = wasUpdating;
    }

    private void RebuildLanguageItems()
    {
        _languageComboBox.Items.Clear();
        _languageComboBox.Items.Add(new LanguageComboItem(LanguageCodes.System, Localizer.Get("LanguageSystemDefault")));
        _languageComboBox.Items.Add(new LanguageComboItem(LanguageCodes.Russian, Localizer.Get("LanguageRussian")));
        _languageComboBox.Items.Add(new LanguageComboItem(LanguageCodes.English, Localizer.Get("LanguageEnglish")));

        string selectedLanguage = LanguageCodes.Normalize(_settings.Language);
        for (int index = 0; index < _languageComboBox.Items.Count; index++)
        {
            if (_languageComboBox.Items[index] is LanguageComboItem item &&
                StringComparer.Ordinal.Equals(item.Code, selectedLanguage))
            {
                _languageComboBox.SelectedIndex = index;
                return;
            }
        }

        _languageComboBox.SelectedIndex = 0;
    }

    private void RebuildSoundItems()
    {
        int selected = Math.Max(0, _soundComboBox.SelectedIndex);
        _soundComboBox.Items.Clear();
        _soundComboBox.Items.Add(Localizer.Get("SettingsSound1"));
        _soundComboBox.Items.Add(Localizer.Get("SettingsSound2"));
        _soundComboBox.Items.Add(Localizer.Get("SettingsSoundCustom"));
        _soundComboBox.SelectedIndex = selected < _soundComboBox.Items.Count ? selected : 0;
    }

    // ── handlers ─────────────────────────────────────────────────────────────

    private void HandleLanguageSelectionChanged(object? sender, EventArgs args)
    {
        if (_updatingUi || _languageComboBox.SelectedItem is not LanguageComboItem item)
        {
            return;
        }

        string previousLanguage = _settings.Language;
        _settings.Language = item.Code;

        try
        {
            _settingsService.Save(_settings);
            _settingsChanged();
            Localizer.Apply(_settings.Language);
            _statusLabel.Text = Localizer.Get("SettingsLanguageChangedStatus");
        }
        catch
        {
            _settings.Language = previousLanguage;
            bool wasUpdating = _updatingUi;
            _updatingUi = true;
            RebuildLanguageItems();
            _updatingUi = wasUpdating;
            DialogService.ShowError(this, "SettingsSaveFailed");
        }
    }

    private void HandleGeneralSettingChanged(object? sender, EventArgs args)
    {
        if (_updatingUi)
        {
            return;
        }

        // A radio button raises CheckedChanged twice (old off, new on); react
        // only to the one that became checked.
        if (sender is RadioButton { Checked: false })
        {
            return;
        }

        bool updatesJustEnabled = _checkForUpdatesCheckBox.Checked && !_settings.CheckForUpdates;

        _settings.StartWithWindows = _startWithWindowsCheckBox.Checked;
        _settings.ShowNotifications = _showNotificationsCheckBox.Checked;
        _settings.NotificationDurationSec = (int)_notificationDurationUpDown.Value;
        _settings.TrayIconMode = _trayIconLanguageRadio.Checked
            ? TrayIconMode.Language
            : _trayIconFlagRadio.Checked ? TrayIconMode.Flag : TrayIconMode.Program;
        _settings.CheckForUpdates = _checkForUpdatesCheckBox.Checked;

        _settings.AutoMode = _autoModeCheckBox.Checked;
        _settings.AlwaysConvertSingleLetters = _alwaysSingleLettersCheckBox.Checked;
        _settings.TabAsWordSeparator = _tabAsSeparatorCheckBox.Checked;
        _settings.AutoCapitalizeSentences = _autoCapitalizeCheckBox.Checked;
        _settings.FixAccidentalCapsLock = _fixAccidentalCapsLockCheckBox.Checked;
        _settings.FixTwoInitialCapitals = _fixTwoCapitalsCheckBox.Checked;

        _settings.ManualLearnMode = _manualLearnRememberRadio.Checked
            ? RememberMode.Remember
            : _manualLearnNeverRadio.Checked ? RememberMode.Never : RememberMode.Ask;
        _settings.SelectionLearnMode = _selectionLearnNeverRadio.Checked ? RememberMode.Never : RememberMode.Ask;
        _settings.AutoReplace = _autoReplaceCheckBox.Checked;
        _settings.ReplacementsRespectPunctuation = _respectPunctuationCheckBox.Checked;

        _settings.DisableInFullscreenApps = _disableInFullscreenCheckBox.Checked;
        _settings.InvertEachCharacterCase = _invertEachCharacterCaseCheckBox.Checked;
        _settings.ClearBufferAfterConversion = _clearBufferCheckBox.Checked;
        _settings.EnableDebugLog = _debugLogCheckBox.Checked;

        UpdateDependentControls();
        SaveSettings();
        if (updatesJustEnabled)
        {
            _updates.ScheduleSoon();
        }
    }

    private void HandleSoundChanged(object? sender, EventArgs args)
    {
        if (_updatingUi)
        {
            return;
        }

        _settings.PlaySoundOnConversion = _playSoundCheckBox.Checked;
        _settings.SoundVariant = SoundVariantFromUi();
        _settings.SoundVolume = _volumeTrackBar.Value;
        SyncSoundControls();
        SaveSettings();
    }

    private string SoundVariantFromUi()
    {
        return _soundComboBox.SelectedIndex switch
        {
            1 => "sound2",
            2 => "custom",
            _ => "sound1"
        };
    }

    private void BrowseCustomSound()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = Localizer.Get("SettingsSoundFileFilter"),
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings.CustomSoundPath = dialog.FileName;
        _customSoundTextBox.Text = dialog.FileName;
        SaveSettings();
    }

    private void PreviewSound()
    {
        SoundFeedback.Preview(SoundVariantFromUi(), _settings.CustomSoundPath, _volumeTrackBar.Value);
    }

    private void CaptureHotkey(KeyEventArgs args, TextBox textBox, Action<string> updateSetting)
    {
        args.SuppressKeyPress = true;
        if (args.KeyCode == Keys.Tab && args.Modifiers == Keys.None)
        {
            // Let Tab move the focus instead of becoming a hotkey.
            args.SuppressKeyPress = false;
            return;
        }

        if (!HotkeyDefinition.TryFromKeyEvent(args, out HotkeyDefinition hotkey))
        {
            _statusLabel.Text = Localizer.Get("SettingsHotkeyModifierOnlyStatus");
            return;
        }

        string previousText = textBox.Text;
        string newText = hotkey.ToString();
        textBox.Text = newText;
        updateSetting(newText);

        if (HotkeyDefinition.HasDuplicates(
            _settings.Hotkeys.ConvertLastWord,
            _settings.Hotkeys.ConvertSelection,
            _settings.Hotkeys.ToggleEnabled,
            _settings.Hotkeys.ToggleCase,
            _settings.Hotkeys.Transliterate))
        {
            updateSetting(previousText);
            textBox.Text = previousText;
            _statusLabel.Text = Localizer.Get("SettingsHotkeyDuplicateStatus");
            return;
        }

        SaveSettings();
        UpdateLearnGroupTitles();
        if (textBox.Focused)
        {
            _suspendHotkeys();
        }

        textBox.SelectAll();
        _statusLabel.Text = Localizer.Get("SettingsHotkeyCapturedStatus");
    }

    private void RestoreDefaultHotkeys()
    {
        _settings.Hotkeys = HotkeySettings.Defaults();
        ApplySettingsToControls();
        SaveSettings();
        UpdateLearnGroupTitles();
        _statusLabel.Text = Localizer.Get("SettingsHotkeysRestoredStatus");
    }

    private void SaveSettings()
    {
        try
        {
            _settingsService.Save(_settings);
            _autostartService.Apply(_settings.StartWithWindows);
            _settingsChanged();
            _statusLabel.Text = Localizer.Get("SettingsSavedStatus");
        }
        catch
        {
            DialogService.ShowError(this, "SettingsSaveOrAutostartFailed");
        }
    }

    private async void CheckUpdatesNow()
    {
        _lastManualCheckFailed = false;
        UpdateCheckResult result = await _updates.CheckNowAsync();
        if (IsDisposed)
        {
            return;
        }

        _lastManualCheckFailed = result.Status == UpdateCheckStatus.Failed;
        UpdateUpdateStatus();
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable when result.Update is not null:
                _showUpdate(result.Update);
                break;
            case UpdateCheckStatus.UpToDate:
                _statusLabel.Text = Localizer.Get("UpdateUpToDateStatus");
                break;
            default:
                _statusLabel.Text = Localizer.Get("UpdateStatusFailed");
                break;
        }
    }

    private void HandleUpdateStateChanged(object? sender, EventArgs args)
    {
        if (!IsDisposed)
        {
            UpdateUpdateStatus();
        }
    }

    private void HandleDataChanged(object? sender, EventArgs args)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(UpdateDataSummary);
            return;
        }

        UpdateDataSummary();
    }

    private void HandleLanguageChanged(object? sender, EventArgs args)
    {
        ApplyLocalization();
    }

    // ── exclusions ───────────────────────────────────────────────────────────

    private void FillRunningApps()
    {
        string typed = _excludedAppInput.Text;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero && process.Id != Environment.ProcessId)
                {
                    names.Add(process.ProcessName.ToLowerInvariant() + ".exe");
                }
            }
            catch
            {
                // Processes may exit or deny access while being listed.
            }
            finally
            {
                process.Dispose();
            }
        }

        _excludedAppInput.BeginUpdate();
        _excludedAppInput.Items.Clear();
        foreach (string name in names)
        {
            _excludedAppInput.Items.Add(name);
        }

        _excludedAppInput.EndUpdate();
        _excludedAppInput.Text = typed;
    }

    private void AddExcludedApp()
    {
        string candidate = _excludedAppInput.Text;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        var apps = new List<string>(_settings.ExcludedApps) { candidate };
        _settings.ExcludedApps = AppSettingsService.NormalizeAppList(apps);
        _excludedAppInput.Text = string.Empty;
        ReloadExcludedApps();
        SaveSettings();
    }

    private void RemoveExcludedApp()
    {
        var selected = _excludedAppsList.SelectedItems.Cast<string>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        _settings.ExcludedApps = _settings.ExcludedApps
            .Where(app => !selected.Contains(app, StringComparer.OrdinalIgnoreCase))
            .ToList();
        ReloadExcludedApps();
        SaveSettings();
    }

    // ── backup ───────────────────────────────────────────────────────────────

    private const int BackupFormatVersion = 1;

    private static readonly JsonSerializerOptions BackupJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // One-file backup: settings + user dictionary + user replacements. Used to
    // migrate the whole profile to another computer.
    private void ExportAll()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = Localizer.Get("SettingsBackupJsonFilter"),
            FileName = $"LayoutFixer-backup-{DateTime.Now:yyyy-MM-dd}.json",
            DefaultExt = "json"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var root = new JsonObject
            {
                ["version"] = BackupFormatVersion,
                ["app"] = "LayoutFixer",
                ["appVersion"] = UpdateChecker.CurrentVersion.ToString(3),
                ["exportedAt"] = DateTimeOffset.Now.ToString("O")
            };
            root["settings"] = JsonNode.Parse(JsonSerializer.Serialize(_settings, BackupJsonOptions));
            root["userDictionary"] = JsonNode.Parse(_userDictionary.ExportToJson());
            root["userReplacements"] = JsonNode.Parse(_userReplacements.ExportToJson());

            File.WriteAllText(dialog.FileName, root.ToJsonString(BackupJsonOptions), new UTF8Encoding(false));

            int words = _userDictionary.Count(ConvertedLanguage.Russian) + _userDictionary.Count(ConvertedLanguage.English);
            _notify(Localizer.Format("SettingsBackupExportDone", words, _userReplacements.Count));
            _statusLabel.Text = Localizer.Get("SettingsBackupExportStatus");
        }
        catch (Exception ex)
        {
            Logger.Log($"Backup export failed: {ex}");
            DialogService.ShowError(this, "SettingsBackupExportFailed");
        }
    }

    private void ImportAll()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = Localizer.Get("SettingsBackupJsonFilter"),
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                Localizer.Get("SettingsImportAllConfirm"),
                Localizer.Get("SettingsWindowTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            string text = File.ReadAllText(dialog.FileName);
            if (JsonNode.Parse(text) is not JsonObject root ||
                (root["version"]?.GetValue<int>() ?? 0) < 1)
            {
                DialogService.ShowError(this, "SettingsBackupImportFailed");
                return;
            }

            int words = 0;
            int pairs = 0;

            // Settings are replaced (you want the other machine's config).
            if (root["settings"] is JsonNode settingsNode &&
                settingsNode.Deserialize<AppSettings>(BackupJsonOptions) is { } imported)
            {
                AppSettingsService.Normalize(imported);
                _settings.CopyFrom(imported);
                _settingsService.Save(_settings);
            }

            // Data is merged (additive, dedup) so an import never wipes existing words.
            if (root["userDictionary"] is JsonNode dictNode)
            {
                (int ru, int en) = _userDictionary.ImportFromJson(dictNode.ToJsonString());
                words = ru + en;
            }

            if (root["userReplacements"] is JsonNode replNode)
            {
                pairs = _userReplacements.ImportFromJson(replNode.ToJsonString());
            }

            // Reflect imported settings in the UI and the running app.
            ApplySettingsToControls();
            Localizer.Apply(_settings.Language);
            _autostartService.Apply(_settings.StartWithWindows);
            _settingsChanged();
            UpdateLearnGroupTitles();

            _notify(Localizer.Format("SettingsBackupImportDone", words, pairs));
            _statusLabel.Text = Localizer.Get("SettingsSavedStatus");
        }
        catch (Exception ex)
        {
            Logger.Log($"Backup import failed: {ex}");
            DialogService.ShowError(this, "SettingsBackupImportFailed");
        }
    }

    private void OpenDebugLog()
    {
        string path = Logger.FilePath;
        if (!File.Exists(path))
        {
            MessageBox.Show(
                this,
                Localizer.Get("SettingsDebugLogMissing"),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Log($"Open debug log failed: {ex}");
        }
    }

    private sealed class LanguageComboItem(string code, string displayName)
    {
        public string Code { get; } = code;

        public override string ToString()
        {
            return displayName;
        }
    }
}
