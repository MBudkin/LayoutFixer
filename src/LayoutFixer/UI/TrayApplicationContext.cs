using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using LayoutFixer.Diagnostics;
using LayoutFixer.Input;
using LayoutFixer.Localization;
using LayoutFixer.Services;
using LayoutFixer.Settings;

namespace LayoutFixer.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppSettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly AutostartService _autostartService = new();
    private readonly KeyboardBuffer _keyboardBuffer = new();
    private readonly KeyboardHook _keyboardHook = new();
    private readonly MouseHook _mouseHook = new();
    private readonly HotkeyManager _hotkeyManager = new();
    private readonly TriggerDictionaries _triggerDictionaries = new();
    private readonly UserDictionary _userDictionary;
    private readonly UserReplacements _userReplacements;
    private readonly AffixModels _affixes = new();
    private readonly ConversionService _conversionService;
    private readonly ContextMenuStrip _trayMenu;
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayPopup _popup = new();
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _autoReplaceItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _userDictionaryItem;
    private readonly ToolStripMenuItem _userReplacementsItem;
    private readonly ToolStripMenuItem _aboutItem;
    private readonly ToolStripMenuItem _reportIssueItem;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _excludeAppItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly Timer _layoutIconTimer = new();
    private readonly Timer _hookWatchdogTimer = new();
    private readonly InputContextGuard _inputGuard = new();
    private readonly UpdateCoordinator _updates;
    private UpdateForm? _updateForm;
    private Font? _boldMenuFont;
    private bool _updatingMenu;
    private volatile bool _converting;
    // A key reached the application while a manual command was running on a
    // background thread; the buffer could not mirror it.
    private volatile bool _keysDuringConversion;
    private long _lastHookRestartTick;
    private string? _lastBlockReason;
    private string _excludeAppCandidate = string.Empty;
    private volatile bool _selectionIntent;
    private readonly Control _marshal = new();
    private string? _lastNotificationKey;
    private DateTime _lastNotificationTimeUtc;
    private static readonly TimeSpan NotificationDedupWindow = TimeSpan.FromSeconds(3);
    private SettingsForm? _settingsForm;
    private AboutForm? _aboutForm;
    private UserDictionaryForm? _userDictionaryForm;
    private UserReplacementsForm? _userReplacementsForm;
    private WordAddedPopup? _wordAddedPopup;
    private string? _lastTrayIconKey;

    public TrayApplicationContext(AppSettingsService settingsService, AppSettings settings)
    {
        _settingsService = settingsService;
        _settings = settings;
        _userDictionary = new UserDictionary(BuildUserDictionaryPath());
        _userReplacements = new UserReplacements(BuildUserReplacementsPath());
        var decisionEngine = new LayoutDecisionEngine(
            _triggerDictionaries,
            _userDictionary,
            _affixes,
            _userReplacements,
            NgramModel.LoadRussian(),
            NgramModel.LoadEnglish(),
            ShortWordRanks.LoadRussian(),
            ShortWordRanks.LoadEnglish())
        {
            AlwaysConvertSingleLetters = () => _settings.AlwaysConvertSingleLetters
        };
        _conversionService = new ConversionService(
            _keyboardBuffer,
            decisionEngine,
            _userReplacements,
            () => _settings.ReplacementsRespectPunctuation);

        _trayMenu = new ContextMenuStrip
        {
            ShowItemToolTips = true
        };

        _enabledItem = new ToolStripMenuItem { CheckOnClick = true };
        _autoReplaceItem = new ToolStripMenuItem { CheckOnClick = true };
        _settingsItem = new ToolStripMenuItem();
        _userDictionaryItem = new ToolStripMenuItem();
        _userReplacementsItem = new ToolStripMenuItem();
        _aboutItem = new ToolStripMenuItem();
        _reportIssueItem = new ToolStripMenuItem();
        _updateItem = new ToolStripMenuItem();
        _excludeAppItem = new ToolStripMenuItem();
        _exitItem = new ToolStripMenuItem();

        _trayMenu.Items.AddRange(
            [
                _enabledItem,
                _autoReplaceItem,
                _excludeAppItem,
                new ToolStripSeparator(),
                _settingsItem,
                _userDictionaryItem,
                _userReplacementsItem,
                new ToolStripSeparator(),
                _updateItem,
                _aboutItem,
                _reportIssueItem,
                new ToolStripSeparator(),
                _exitItem
            ]);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _trayMenu,
            Icon = AppIcons.ForTray(_settings.TrayIconMode, _settings.AutoMode, CurrentTrayLanguage()),
            Visible = true
        };

        _layoutIconTimer.Interval = 500;
        _layoutIconTimer.Tick += (_, _) => ApplyTrayIconVisuals();
        _layoutIconTimer.Start();

        _updates = new UpdateCoordinator(_settings, SaveSettingsQuietly);
        _updates.StateChanged += (_, _) => UpdateUpdateItem();
        _updates.UpdateFound += (_, update) => PromptUpdate(update);

        _hookWatchdogTimer.Interval = 2000;
        _hookWatchdogTimer.Tick += (_, _) => CheckHooksAlive();
        _hookWatchdogTimer.Start();
        SystemEvents.SessionSwitch += HandleSessionSwitch;
        SystemEvents.PowerModeChanged += HandlePowerModeChanged;

        WireEvents();
        StartInputServices();
        ApplySettings();
        TryApplyAutostart();
        ApplyLocalization();
        Localizer.LanguageChanged += HandleLanguageChanged;
        _updates.Start();

        // Force an early handle on the UI thread so BeginInvoke marshaling works
        // even before the tray menu is ever opened.
        _ = _marshal.Handle;
        ClipboardSnapshot.OwnerWindow = _marshal.Handle;

        if (_settingsService.LastLoadHadError)
        {
            ShowNotification("SettingsInvalidJson", ToolTipIcon.Warning);
        }
        else
        {
            ShowNotification("NotificationStarted", ToolTipIcon.Info);
        }
    }

    protected override void ExitThreadCore()
    {
        Localizer.LanguageChanged -= HandleLanguageChanged;
        SystemEvents.SessionSwitch -= HandleSessionSwitch;
        SystemEvents.PowerModeChanged -= HandlePowerModeChanged;
        _hookWatchdogTimer.Stop();
        _hookWatchdogTimer.Dispose();
        _inputGuard.Dispose();
        _updates.Dispose();
        _updateForm?.Close();
        _settingsForm?.Close();
        _aboutForm?.Close();
        _userDictionaryForm?.Close();
        _userReplacementsForm?.Close();
        _wordAddedPopup?.Close();
        _layoutIconTimer.Stop();
        _layoutIconTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _hotkeyManager.Dispose();
        _keyboardHook.Dispose();
        _mouseHook.Dispose();
        _trayMenu.Dispose();
        _boldMenuFont?.Dispose();
        _marshal.Dispose();
        _popup.Dispose();
        Logger.Flush();
        base.ExitThreadCore();
    }

    private void WireEvents()
    {
        _enabledItem.CheckedChanged += (_, _) =>
        {
            if (_updatingMenu)
            {
                return;
            }

            _settings.AutoMode = _enabledItem.Checked;
            ApplyTrayIconVisuals(force: true);
            TrySaveSettings();
            SyncOpenWindows();
        };

        _autoReplaceItem.CheckedChanged += (_, _) =>
        {
            if (_updatingMenu)
            {
                return;
            }

            _settings.AutoReplace = _autoReplaceItem.Checked;
            TrySaveSettings();
            SyncOpenWindows();
        };

        _settingsItem.Click += (_, _) => OpenSettings();
        _userDictionaryItem.Click += (_, _) => OpenUserDictionary();
        _userReplacementsItem.Click += (_, _) => OpenUserReplacements();
        _aboutItem.Click += (_, _) => OpenAbout();
        _reportIssueItem.Click += (_, _) => ProjectLinks.OpenIssues();
        _updateItem.Click += (_, _) => HandleUpdateItemClick();
        _excludeAppItem.Click += (_, _) => ToggleExcludedApp(_excludeAppCandidate);
        _trayMenu.Opening += (_, _) => UpdateExcludeAppItem();
        _inputGuard.PasswordFieldFocused += (_, _) => BeginInvokeOnUi(() =>
        {
            // Never keep what was typed into a password field. While a manual
            // command is running, defer the reset until it finishes.
            if (_converting)
            {
                _keysDuringConversion = true;
            }
            else
            {
                ForgetTypedText();
            }
        });
        _exitItem.Click += (_, _) => ExitThread();
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();

        // The keyboard buffer is always observed: manual conversion needs it,
        // and user replacements trigger on word terminators regardless of the
        // auto-correction toggle.
        _keyboardHook.KeyObserved += (_, args) => HandleKeyObserved(args);

        _mouseHook.Clicked += (_, _) =>
        {
            _selectionIntent = false;
            if (!_converting)
            {
                _keyboardBuffer.OnPointerAction();
                _conversionService.ClearUndo();
            }

            // A click may move focus inside a window without a focus event
            // (browsers): re-check whether a password field is focused.
            _inputGuard.RequestProbe();
        };
        _mouseHook.SelectionGesture += (_, _) =>
        {
            _selectionIntent = true;
            Logger.Log("Selection intent: mouse gesture");
        };

        _hotkeyManager.HotkeyPressed += HandleHotkeyPressed;
    }

    private void HandleKeyObserved(KeyboardObservedEventArgs args)
    {
        int virtualKey = args.VirtualKey;
        if (_converting)
        {
            // A manual command (Break, selection, case, translit) is running
            // on a background thread. The key still reaches the application,
            // so the buffer no longer mirrors the text; drop it afterwards.
            // A repeated hotkey press (Break twice) is not typing.
            if (!IsModifierKeystroke(virtualKey) &&
                !_hotkeyManager.MatchesRegisteredHotkeyKeyDown(virtualKey))
            {
                _keysDuringConversion = true;
            }

            return;
        }

        // RegisterHotKey consumes application commands separately through
        // WM_HOTKEY. Their final key must not be interpreted as a Ctrl/Alt
        // editor command, otherwise the buffer is cleared before commands
        // such as "transform last word" get a chance to read it.
        if (_hotkeyManager.MatchesRegisteredHotkeyKeyDown(virtualKey))
        {
            return;
        }

        if (IsKeyboardSelectionGesture(virtualKey))
        {
            _selectionIntent = true;
        }
        else if (!IsModifierKeystroke(virtualKey) &&
                 !IsSelectionPreservingShortcut(virtualKey))
        {
            _selectionIntent = false;
        }

        // Password fields: never record, log or rewrite what is typed there.
        if (_inputGuard.IsPasswordFieldFocused)
        {
            ForgetTypedText();
            if (virtualKey is NativeMethods.VkTab or NativeMethods.VkReturn)
            {
                _inputGuard.RequestProbe();
            }

            return;
        }

        // Tab is configurable: when "Tab as word separator" is off, Tab
        // must not trigger an action, but it still resets the buffer
        // (Tab usually moves focus, so the buffer is no longer valid).
        if (virtualKey == NativeMethods.VkTab && !_settings.TabAsWordSeparator)
        {
            _keyboardBuffer.Clear();
            _conversionService.ClearUndo();
            _inputGuard.RequestProbe();
            return;
        }

        bool isTerminator = IsWordTerminator(virtualKey);
        bool reset = _keyboardBuffer.Observe(args);
        // An untrackable reset (Ctrl+Backspace, paste, navigation, ...) breaks
        // the relationship with the last conversion: invalidate the undo state
        // so a stale toggle cannot revert (and learn) a freshly retyped word.
        if (reset)
        {
            _conversionService.ClearUndo();
        }
        else if (virtualKey is NativeMethods.VkBack or NativeMethods.VkDelete)
        {
            // The buffer could mirror this one-character edit, but any live
            // decision made for the old spelling is now stale. Re-arm live
            // conversion for the next character the user types.
            _conversionService.NotifyTrackedDeletion();
        }

        if (isTerminator)
        {
            if (!reset)
            {
                HandleTerminatorInHook(args);
            }

            // Enter and Tab often move focus (forms, chats, dialogs).
            if (virtualKey is NativeMethods.VkTab or NativeMethods.VkReturn)
            {
                _inputGuard.RequestProbe();
            }
        }
        else if (_settings.AutoMode && IsLiveEligibleKeystroke(virtualKey) && AutoEditsAllowed())
        {
            // Live (mid-word) conversion. Runs synchronously on the hook
            // thread; TryLiveConvert is a no-op when the buffer does not yet
            // form a known prefix or when the user dictionary holds it off.
            // When it DOES fire, the triggering keystroke is consumed by the
            // replacement text, so the original key must be suppressed —
            // otherwise the OS delivers it on top of the injected text and
            // the user sees a duplicate trailing character.
            //
            // Only real character keystrokes are eligible. Backspace/Delete
            // are NOT: they shrink the current word, and running a live
            // trigger on the resulting tail would fire on a fragment the
            // user never typed as a word (e.g. erasing "рузпрпривет" down to
            // "руз" used to live-switch "руз" -> "hep"). Modifiers are
            // already filtered out by KeyboardBuffer.Observe and never add a
            // character, so they are excluded here too.
            if (_conversionService.TryLiveConvert())
            {
                args.Suppress = true;
                if (_settings.PlaySoundOnConversion)
                {
                    SoundFeedback.PlaySuccess();
                }
            }
        }
    }

    // Automatic edits (live/final layout fixes, auto-replacement, case fixes)
    // are off in password fields, excluded programs and full-screen windows.
    private bool AutoEditsAllowed()
    {
        string? reason = _inputGuard.GetAutoBlockReason(_settings.ExcludedApps, _settings.DisableInFullscreenApps);
        if (!string.Equals(reason, _lastBlockReason, StringComparison.Ordinal))
        {
            Logger.Log(reason is null ? "Auto edits allowed again" : $"Auto edits blocked: {reason}");
            _lastBlockReason = reason;
        }

        return reason is null;
    }

    // SEPARATOR PIPELINE, synchronous inside the keyboard hook. The separator
    // (Space/Enter/Tab/"-") is already in the buffer but not yet in the
    // application. All steps edit the buffer tail through an InputBatch; if
    // anything changed, the original keystroke is suppressed and the batch
    // sends backspaces + corrected word + the separator in ONE SendInput call.
    // Compared with the former background task this removes the race with
    // keys typed right after the separator, and "word + Enter" in a chat now
    // sends the corrected word instead of sending the typo first.
    private void HandleTerminatorInHook(KeyboardObservedEventArgs args)
    {
        char? pending = _keyboardBuffer.LastCharacter;
        if (pending is not (' ' or '\n' or '\t' or '-' or '_'))
        {
            // The buffer did not record this separator (unsupported layout):
            // it cannot be re-sent reliably, so leave the keystroke alone.
            return;
        }

        if (!AutoEditsAllowed())
        {
            return;
        }

        TerminatorResults results;
        bool suppress = false;
        using (InputBatch batch = TextInputSender.BeginBatch(pending.Value.ToString()))
        {
            try
            {
                results = RunTerminatorSteps();
            }
            catch (Exception ex)
            {
                Logger.Log($"Terminator action failed: {ex}");
                ForgetTypedText();
                return;
            }

            if (batch.HasChanges)
            {
                if (batch.Commit())
                {
                    suppress = true;
                }
                else
                {
                    // Nothing (or not everything) reached the application; the
                    // buffer was already edited, so it no longer mirrors the
                    // text. Let the separator through and start afresh.
                    Logger.Log("Terminator batch could not be sent");
                    ForgetTypedText();
                    results = TerminatorResults.Failed;
                }
            }
        }

        args.Suppress = suppress;
        HandleAutoConversionResult(results);
    }

    private TerminatorResults RunTerminatorSteps()
    {
        var results = new TerminatorResults();
        // A second separator after a word ("word" Space Break Space): the word
        // already went through this pipeline, and the user may have fixed it
        // by hand since. Re-deciding it would undo that fix.
        if (_conversionService.IsLastWordAlreadyTerminated())
        {
            Logger.Log("Terminator: last word was already finished by an earlier separator -> skip");
            return results;
        }

        results.Replacement = _settings.AutoReplace
            ? _conversionService.ApplyReplacement()
            : ConversionResult.Skipped;
        results.Primary = results.Replacement;
        if (results.Primary == ConversionResult.Skipped && _settings.AutoMode)
        {
            results.Primary = _conversionService.FinalConvert();
        }

        // Replacements may expand one trigger into a whole phrase; the
        // current token would then be the LAST word of that phrase. Leave
        // phrase casing untouched until replacement-aware casing exists.
        bool caseSteps = results.Replacement != ConversionResult.Success &&
                         results.Primary != ConversionResult.SendFailed;
        if (caseSteps && _settings.FixAccidentalCapsLock)
        {
            results.CapsLock = _conversionService.FixAccidentalCapsLock();
        }

        if (caseSteps && results.CapsLock != ConversionResult.SendFailed && _settings.FixTwoInitialCapitals)
        {
            results.TwoCapitals = _conversionService.FixTwoInitialCapitals();
        }

        if (caseSteps &&
            results.CapsLock != ConversionResult.SendFailed &&
            results.TwoCapitals != ConversionResult.SendFailed &&
            _settings.AutoCapitalizeSentences)
        {
            results.Capitalization = _conversionService.AutoCapitalizeLastWord();
        }

        return results;
    }

    private sealed class TerminatorResults
    {
        public ConversionResult Replacement = ConversionResult.Skipped;
        public ConversionResult Primary = ConversionResult.Skipped;
        public ConversionResult CapsLock = ConversionResult.Skipped;
        public ConversionResult TwoCapitals = ConversionResult.Skipped;
        public ConversionResult Capitalization = ConversionResult.Skipped;

        public static TerminatorResults Failed => new() { Primary = ConversionResult.SendFailed };
    }

    // Drops everything the buffer knows about the current text.
    private void ForgetTypedText()
    {
        _keyboardBuffer.Clear();
        _conversionService.ClearUndo();
    }

    // Called on the UI thread when a background command finished.
    private void FinishBackgroundCommand()
    {
        _converting = false;
        if (_keysDuringConversion)
        {
            _keysDuringConversion = false;
            Logger.Log("Keys were typed during a manual command -> buffer reset");
            ForgetTypedText();
        }
    }

    // ── hook watchdog ────────────────────────────────────────────────────────

    // Windows silently removes a low-level hook whose callback once took longer
    // than LowLevelHooksTimeout (a stalled system, a debugger, heavy swapping).
    // The program then looks alive but no longer reacts to typing. Input the
    // system registered but neither hook saw means exactly that: reinstall.
    private void CheckHooksAlive()
    {
        try
        {
            var info = new NativeMethods.LastInputInfo
            {
                CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LastInputInfo>()
            };
            if (!NativeMethods.GetLastInputInfo(ref info))
            {
                return;
            }

            long now = Environment.TickCount64;
            long inputAge = unchecked((uint)Environment.TickCount - info.DwTime);
            long keyboardAge = now - _keyboardHook.LastEventTick;
            long mouseAge = now - _mouseHook.LastEventTick;
            const long slack = 1500;
            if (inputAge + slack >= Math.Min(keyboardAge, mouseAge) ||
                now - _lastHookRestartTick < 30_000)
            {
                return;
            }

            // Input to elevated windows or the secure desktop is invisible to a
            // non-elevated hook by design; that is not a dead hook.
            if (ElevationProbe.IsForegroundInaccessible())
            {
                return;
            }

            RestartHooks($"input {inputAge} ms ago, keyboard hook idle {keyboardAge} ms, mouse hook idle {mouseAge} ms");
        }
        catch (Exception ex)
        {
            Logger.Log($"Hook watchdog failed: {ex.Message}");
        }
    }

    private void RestartHooks(string reason)
    {
        _lastHookRestartTick = Environment.TickCount64;
        Logger.Log($"Reinstalling input hooks: {reason}");
        ForgetTypedText();
        try
        {
            _keyboardHook.Restart();
        }
        catch (Exception ex)
        {
            Logger.Log($"Keyboard hook restart failed: {ex.Message}");
        }

        try
        {
            _mouseHook.Restart();
        }
        catch (Exception ex)
        {
            Logger.Log($"Mouse hook restart failed: {ex.Message}");
        }
    }

    private void HandleSessionSwitch(object? sender, SessionSwitchEventArgs args)
    {
        if (args.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
        {
            BeginInvokeOnUi(() => RestartHooks("session " + args.Reason));
        }
    }

    private void HandlePowerModeChanged(object? sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume)
        {
            BeginInvokeOnUi(() => RestartHooks("resume from sleep"));
        }
    }

    // ── per-program exclusion from the tray menu ─────────────────────────────

    private void UpdateExcludeAppItem()
    {
        _excludeAppCandidate = _inputGuard.LastExternalProcess;
        if (string.IsNullOrEmpty(_excludeAppCandidate))
        {
            _excludeAppItem.Visible = false;
            return;
        }

        _excludeAppItem.Visible = true;
        _excludeAppItem.Checked = InputContextGuard.IsExcluded(_excludeAppCandidate, _settings.ExcludedApps);
        _excludeAppItem.Text = Localizer.Format("TrayExcludeApp", _excludeAppCandidate);
        _excludeAppItem.ToolTipText = Localizer.Get("TrayExcludeAppTooltip");
    }

    private void ToggleExcludedApp(string processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return;
        }

        var apps = new System.Collections.Generic.List<string>(_settings.ExcludedApps);
        bool removed = apps.RemoveAll(app => string.Equals(app, processName, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
        {
            apps.Add(processName);
        }

        _settings.ExcludedApps = AppSettingsService.NormalizeAppList(apps);
        TrySaveSettings();
        _settingsForm?.ReloadExcludedApps();
        ShowNotificationText(Localizer.Format(removed ? "NotificationAppIncluded" : "NotificationAppExcluded", processName));
    }

    private void HandleLanguageChanged(object? sender, EventArgs args)
    {
        ApplyLocalization();
        ShowNotification("NotificationLanguageChanged");
    }

    private void ApplySettings()
    {
        _updatingMenu = true;
        _enabledItem.Checked = _settings.AutoMode;
        _autoReplaceItem.Checked = _settings.AutoReplace;
        _updatingMenu = false;
        RegisterHotkeys();
        ApplyTrayIconVisuals(force: true);

        // TriggerDictionaries load synchronously in their constructor, so there
        // is no background-load step here. AutoMode simply gates whether the
        // live/final conversion paths are consulted.

        ApplyDebugLogging();
        ApplyPopupDuration();
        SoundFeedback.Configure(
            _settings.PlaySoundOnConversion,
            _settings.SoundVariant,
            _settings.CustomSoundPath,
            _settings.SoundVolume);
    }

    private void ApplyPopupDuration()
    {
        // A single configurable duration drives every transient popup so the
        // setting is honoured without restarting already-open popups (each form
        // reads CurrentDisplayMs when it shows / refreshes).
        int ms = Math.Clamp(_settings.NotificationDurationSec, 1, 300) * 1000;
        TrayPopup.CurrentDisplayMs = ms;
        WordAddedPopup.CurrentDisplayMs = ms;
        TrayPrompt.CurrentDisplayMs = ms;
    }

    private void ApplyDebugLogging()
    {
        // Debug logging is OFF by default for privacy. When it is off, nothing
        // is written and any previous log is removed. When on, a size-capped
        // debug.log is written to %AppData%\LayoutFixer.
        bool wasEnabled = Logger.Enabled;
        Logger.Enabled = _settings.EnableDebugLog;
        if (!_settings.EnableDebugLog)
        {
            Logger.Clear();
        }
        else if (!wasEnabled)
        {
            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
            Logger.Log($"Debug log on: LayoutFixer {version}, data '{AppPaths.DataDirectory}', " +
                       $"keyboard hook={_keyboardHook.IsInstalled} mouse hook={_mouseHook.IsInstalled}, " +
                       $"layout=0x{LayoutProbe.GetCurrentLayoutId():X4}, excluded apps={_settings.ExcludedApps.Count}");
        }
    }

    private void ApplyTrayIconVisuals(bool force = false)
    {
        ConvertedLanguage language = CurrentTrayLanguage();
        string key = $"{_settings.TrayIconMode}:{_settings.AutoMode}:{language}";
        if (!force && string.Equals(_lastTrayIconKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _notifyIcon.Icon = AppIcons.ForTray(_settings.TrayIconMode, _settings.AutoMode, language);
        _lastTrayIconKey = key;
    }

    private static ConvertedLanguage CurrentTrayLanguage()
    {
        return LayoutProbe.GetCurrentLanguage();
    }

    private void StartInputServices()
    {
        try
        {
            _keyboardHook.Start();
        }
        catch
        {
            DialogService.ShowError(null, "KeyboardHookFailed");
        }

        try
        {
            _mouseHook.Start();
        }
        catch
        {
            // Mouse hook is best-effort; if it fails, keyboard navigation reset still works.
        }
    }

    private void ApplyLocalization()
    {
        _notifyIcon.Text = Localizer.Get("TrayTooltip");
        _enabledItem.Text = Localizer.Get("TrayEnabled");
        _enabledItem.ToolTipText = Localizer.Get("TrayEnabledTooltip");
        _autoReplaceItem.Text = Localizer.Get("TrayAutoReplace");
        _autoReplaceItem.ToolTipText = Localizer.Get("TrayAutoReplaceTooltip");
        _settingsItem.Text = Localizer.Get("TraySettings");
        _settingsItem.ToolTipText = Localizer.Get("TraySettingsTooltip");
        _userDictionaryItem.Text = Localizer.Get("TrayUserDictionary");
        _userDictionaryItem.ToolTipText = Localizer.Get("TrayUserDictionaryTooltip");
        _userReplacementsItem.Text = Localizer.Get("TrayUserReplacements");
        _userReplacementsItem.ToolTipText = Localizer.Get("TrayUserReplacementsTooltip");
        _aboutItem.Text = Localizer.Get("TrayAbout");
        _aboutItem.ToolTipText = Localizer.Get("TrayAboutTooltip");
        _reportIssueItem.Text = Localizer.Get("ReportIssueButton");
        UpdateExcludeAppItem();
        _reportIssueItem.ToolTipText = Localizer.Get("ReportIssueTooltip");
        _exitItem.Text = Localizer.Get("TrayExit");
        _exitItem.ToolTipText = Localizer.Get("TrayExitTooltip");
        UpdateUpdateItem();
    }

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Show();
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(
            _settingsService,
            _settings,
            _userDictionary,
            _userReplacements,
            _updates,
            HandleSettingsFormChanged,
            _hotkeyManager.Suspend,
            RegisterHotkeys,
            OpenUserDictionary,
            OpenUserReplacements,
            ShowUpdate,
            ShowFeedbackPopup);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void OpenAbout()
    {
        if (_aboutForm is { IsDisposed: false })
        {
            _aboutForm.WindowState = FormWindowState.Normal;
            _aboutForm.Show();
            _aboutForm.Activate();
            return;
        }

        _aboutForm = new AboutForm();
        _aboutForm.FormClosed += (_, _) => _aboutForm = null;
        _aboutForm.Show();
        _aboutForm.Activate();
    }

    private void OpenUserDictionary()
    {
        if (_userDictionaryForm is { IsDisposed: false })
        {
            _userDictionaryForm.WindowState = FormWindowState.Normal;
            _userDictionaryForm.Show();
            _userDictionaryForm.Activate();
            return;
        }

        _userDictionaryForm = new UserDictionaryForm(_userDictionary);
        _userDictionaryForm.FormClosed += (_, _) => _userDictionaryForm = null;
        _userDictionaryForm.Show();
        _userDictionaryForm.Activate();
    }

    private void OpenUserReplacements()
    {
        if (_userReplacementsForm is { IsDisposed: false })
        {
            _userReplacementsForm.WindowState = FormWindowState.Normal;
            _userReplacementsForm.Show();
            _userReplacementsForm.Activate();
            return;
        }

        _userReplacementsForm = new UserReplacementsForm(
            _userReplacements,
            () => _settings.AutoReplace,
            value =>
            {
                _settings.AutoReplace = value;
                _updatingMenu = true;
                _autoReplaceItem.Checked = value;
                _updatingMenu = false;
                TrySaveSettings();
                _settingsForm?.RefreshFromSettings();
            });
        _userReplacementsForm.FormClosed += (_, _) => _userReplacementsForm = null;
        _userReplacementsForm.Show();
        _userReplacementsForm.Activate();
    }

    private static string BuildUserDictionaryPath()
    {
        return AppPaths.File("user-dictionary.json");
    }

    private static string BuildUserReplacementsPath()
    {
        return AppPaths.File("user-replacements.json");
    }

    private void ShowNotification(string messageKey, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (!_settings.ShowNotifications)
        {
            return;
        }

        // Suppress identical notifications fired in quick succession (e.g. pressing
        // the conversion hotkey several times when there is nothing to convert).
        DateTime nowUtc = DateTime.UtcNow;
        if (messageKey == _lastNotificationKey &&
            nowUtc - _lastNotificationTimeUtc < NotificationDedupWindow)
        {
            return;
        }

        _lastNotificationKey = messageKey;
        _lastNotificationTimeUtc = nowUtc;

        // Use the instant popup instead of a NotifyIcon balloon: balloons are
        // queued by the shell and lag behind rapid hotkey presses.
        _popup.ShowMessage(Localizer.Get(messageKey), icon);
    }

    private void ShowNotificationText(string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        // Show an already-localized message verbatim. Unlike ShowNotification,
        // this does NOT call Localizer.Get again (which would treat the text as a
        // resource key and wrap it in [[...]]).
        if (!_settings.ShowNotifications)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (string.Equals(text, _lastNotificationKey, StringComparison.Ordinal) &&
            nowUtc - _lastNotificationTimeUtc < NotificationDedupWindow)
        {
            return;
        }

        _lastNotificationKey = text;
        _lastNotificationTimeUtc = nowUtc;
        _popup.ShowMessage(text, icon);
    }

    // Feedback for explicit user actions (export/import). Shown unconditionally,
    // regardless of the "show notifications" toggle, because the user initiated it.
    private void ShowFeedbackPopup(string text)
    {
        _popup.ShowMessage(text, ToolTipIcon.Info);
    }

    private void TrySaveSettings()
    {
        try
        {
            _settingsService.Save(_settings);
            TryApplyAutostart();
            RegisterHotkeys();
        }
        catch
        {
            DialogService.ShowError(null, "SettingsSaveFailed");
        }
    }

    // Settings saved from the settings window: apply them to the running app and
    // to the other open windows (the settings window is already up to date).
    private void HandleSettingsFormChanged()
    {
        ApplySettings();
        _userReplacementsForm?.RefreshAutoReplaceState();
    }

    // A setting changed outside the settings window (tray menu, hotkey).
    private void SyncOpenWindows()
    {
        _settingsForm?.RefreshFromSettings();
        _userReplacementsForm?.RefreshAutoReplaceState();
    }

    // Saves bookkeeping (update check time) without the side effects of
    // TrySaveSettings (hotkey re-registration, error dialogs).
    private void SaveSettingsQuietly()
    {
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            Logger.Log($"Settings save failed: {ex.Message}");
        }
    }

    // ── updates ──────────────────────────────────────────────────────────────

    private void UpdateUpdateItem()
    {
        if (_updates.UnskippedUpdate is { } update)
        {
            _updateItem.Text = Localizer.Format("TrayUpdateAvailable", update.Version.ToString(3));
            _updateItem.ToolTipText = Localizer.Get("TrayUpdateAvailableTooltip");
            _boldMenuFont ??= new Font(_trayMenu.Font, FontStyle.Bold);
            _updateItem.Font = _boldMenuFont;
        }
        else
        {
            _updateItem.Text = Localizer.Get(_updates.IsChecking ? "UpdateStatusChecking" : "TrayCheckUpdates");
            _updateItem.ToolTipText = Localizer.Get("TrayCheckUpdatesTooltip");
            _updateItem.Font = _trayMenu.Font;
        }

        _updateItem.Enabled = !_updates.IsChecking;
    }

    private async void HandleUpdateItemClick()
    {
        if (_updates.UnskippedUpdate is { } known)
        {
            ShowUpdate(known);
            return;
        }

        UpdateCheckResult result = await _updates.CheckNowAsync();
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable when result.Update is not null:
                ShowUpdate(result.Update);
                break;
            case UpdateCheckStatus.UpToDate:
                ShowFeedbackPopup(Localizer.Format("UpdateUpToDateNotification", UpdateChecker.CurrentVersion.ToString(3)));
                break;
            default:
                _popup.ShowMessage(Localizer.Get("UpdateStatusFailed"), ToolTipIcon.Warning);
                break;
        }
    }

    // Automatic check found a new release: a non-activating prompt, so the
    // text being typed somewhere else keeps the focus.
    private void PromptUpdate(UpdateInfo update)
    {
        var prompt = new TrayPrompt(
            Localizer.Format("UpdatePromptText", update.Version.ToString(3)),
            Localizer.Get("UpdatePromptDetails"),
            Localizer.Get("UpdateLaterButton"),
            minimumDisplayMs: 20_000);
        prompt.Decision += (_, accepted) =>
        {
            if (accepted)
            {
                ShowUpdate(update);
            }
        };
        prompt.Show();
    }

    private void ShowUpdate(UpdateInfo update)
    {
        if (_updateForm is { IsDisposed: false } && _updateForm.Release.Version == update.Version)
        {
            _updateForm.WindowState = FormWindowState.Normal;
            _updateForm.Activate();
            return;
        }

        _updateForm?.Close();
        _updateForm = new UpdateForm(update, _updates.Skip);
        _updateForm.FormClosed += (_, _) => _updateForm = null;
        _updateForm.Show();
        _updateForm.Activate();
    }

    private void TryApplyAutostart()
    {
        try
        {
            _autostartService.Apply(_settings.StartWithWindows);
        }
        catch
        {
            DialogService.ShowError(null, "AutostartUpdateFailed");
        }
    }

    private void RegisterHotkeys()
    {
        var errors = _hotkeyManager.Apply(_settings);
        foreach (HotkeyRegistrationError error in errors)
        {
            string key = error.InvalidFormat ? "HotkeyInvalidFormat" : "HotkeyRegistrationFailed";
            MessageBox.Show(
                Localizer.Format(key, GetHotkeyActionName(error.Action), error.Hotkey),
                Localizer.Get("ErrorDialogTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void HandleHotkeyPressed(HotkeyAction action)
    {
        Logger.Log($"Hotkey pressed: {action}");
        switch (action)
        {
            case HotkeyAction.ConvertLastWord:
                BeginConvertLastWord();
                break;
            case HotkeyAction.ConvertSelection:
                BeginConvertSelection();
                break;
            case HotkeyAction.ToggleEnabled:
                ToggleEnabled();
                break;
            case HotkeyAction.ToggleCase:
                BeginTextTransformation(TextTransformationKind.ToggleCase);
                break;
            case HotkeyAction.Transliterate:
                BeginTextTransformation(TextTransformationKind.Transliterate);
                break;
        }
    }

    private void BeginConvertLastWord()
    {
        // Manual conversion always works while the app is running — it is not
        // gated by the auto-correction toggle.

        // Ignore presses while a conversion is already in flight. This also keeps
        // the keyboard buffer untouched by the hook callbacks during conversion,
        // so there is no race with the conversion reading/writing it.
        if (_converting)
        {
            return;
        }

        _converting = true;

        Task.Run(() =>
        {
            ConversionOutcome outcome;
            bool fromSelection;
            try
            {
                if (!WaitForHotkeyModifiersReleased())
                {
                    outcome = ConversionOutcome.From(ConversionResult.Skipped);
                    fromSelection = false;
                }
                else
                {
                    // Priority 1: convert an active text selection — but ONLY when UI
                    // Automation confirms a real selection exists. Otherwise editors
                    // that copy the current line on Ctrl+C would make us append the
                    // converted text to the just-typed word instead of replacing it.
                    if (SelectionProbe.TryReadSelectedText(_selectionIntent, out string selection))
                    {
                        _selectionIntent = false;
                        fromSelection = true;
                        outcome = _conversionService.ConvertSelection(selection);
                    }
                    else
                    {
                        // Priority 2: convert last typed word from the buffer.
                        fromSelection = false;
                        outcome = _conversionService.ConvertLastWord();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ConvertLastWord failed: {ex}");
                outcome = ConversionOutcome.From(ConversionResult.SendFailed);
                fromSelection = false;
            }

            BeginInvokeOnUi(() =>
            {
                try
                {
                    HandleConversionResult(outcome, fromSelection, suppressLearning: !_settings.AutoMode);
                }
                finally
                {
                    FinishBackgroundCommand();
                }
            });
        });
    }

    private void BeginConvertSelection()
    {
        // Manual conversion always works while the app is running.
        if (_converting)
        {
            return;
        }

        _converting = true;

        Task.Run(() =>
        {
            ConversionOutcome outcome;
            try
            {
                if (!WaitForHotkeyModifiersReleased())
                {
                    outcome = ConversionOutcome.From(ConversionResult.Skipped);
                }
                else
                {
                    // Prefer reading the selection directly via UI Automation (no
                    // clipboard round-trip); fall back to Ctrl+C, which works wherever
                    // a real selection exists. Only use the clipboard fallback when
                    // UI Automation confirms a text selection; otherwise some apps
                    // copy a link/path/current object on Ctrl+C and Shift+Pause would
                    // insert unrelated clipboard-like text.
                    if (SelectionProbe.TryReadSelectedText(_selectionIntent, out string selection))
                    {
                        _selectionIntent = false;
                        outcome = _conversionService.ConvertSelection(selection);
                    }
                    else
                    {
                        outcome = ConversionOutcome.From(ConversionResult.NoConvertibleWord);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ConvertSelection failed: {ex}");
                outcome = ConversionOutcome.From(ConversionResult.SendFailed);
            }

            BeginInvokeOnUi(() =>
            {
                try
                {
                    HandleConversionResult(outcome, fromSelection: true, suppressLearning: false);
                }
                finally
                {
                    FinishBackgroundCommand();
                }
            });
        });
    }

    private void BeginTextTransformation(TextTransformationKind kind)
    {
        if (_converting)
        {
            return;
        }

        _converting = true;
        Task.Run(() =>
        {
            ConversionResult result;
            try
            {
                if (!WaitForHotkeyModifiersReleased())
                {
                    result = ConversionResult.Skipped;
                }
                else
                {
                    if (SelectionProbe.TryReadSelectedText(_selectionIntent, out string selection))
                    {
                        _selectionIntent = false;
                        result = _conversionService.TransformSelection(
                            selection,
                            kind,
                            _settings.InvertEachCharacterCase);
                    }
                    else
                    {
                        result = _conversionService.TransformLastWord(
                            kind,
                            _settings.InvertEachCharacterCase);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Text transformation failed ({kind}): {ex}");
                result = ConversionResult.SendFailed;
            }

            BeginInvokeOnUi(() =>
            {
                try
                {
                    HandleTextTransformationResult(result);
                }
                finally
                {
                    FinishBackgroundCommand();
                }
            });
        });
    }

    private void HandleAutoConversionResult(TerminatorResults results)
    {
        bool primarySucceeded = results.Primary == ConversionResult.Success;
        bool caseLayer = results.TwoCapitals == ConversionResult.Success ||
                         results.Capitalization == ConversionResult.Success;
        bool anySucceeded = primarySucceeded || caseLayer || results.CapsLock == ConversionResult.Success;
        if (!anySucceeded)
        {
            return;
        }

        // Keep the buffer when a case layer was applied: Break needs that
        // exact tail for its first, case-only undo step.
        if (_settings.ClearBufferAfterConversion && !caseLayer)
        {
            _keyboardBuffer.Clear();
        }

        // The existing sound means layout/replacement conversion. Fixing the
        // case of a word by itself stays quiet.
        if (primarySucceeded && _settings.PlaySoundOnConversion)
        {
            SoundFeedback.PlaySuccess();
        }
    }

    private static void HandleTextTransformationResult(ConversionResult result)
    {
        switch (result)
        {
            case ConversionResult.NoConvertibleWord:
                // Deliberately quiet: the hotkey may be pressed on punctuation,
                // Latin text for transliteration, or with an empty buffer.
                break;
            case ConversionResult.SendFailed:
                DialogService.ShowError(null, "SendInputFailed");
                break;
        }
    }

    private static bool IsWordTerminator(int virtualKey)
    {
        return virtualKey is NativeMethods.VkSpace
            or NativeMethods.VkReturn
            or NativeMethods.VkTab
            or NativeMethods.VkOemMinus; // "-" also finishes a word ("когда-то")
    }

    // A keystroke that ADDS a character to the current word, and therefore is
    // safe/meaningful to evaluate against the live triggers. Editing keys
    // (Backspace/Delete) only shrink the word and must not trigger a live
    // switch on the resulting fragment; modifiers are already absorbed by the
    // buffer and never produce a character — but their LEFT/RIGHT variants
    // (VK 0xA0..0xA5) are distinct from the generic VkShift/VkControl/VkMenu
    // constants, so they must be filtered explicitly, otherwise pressing Ctrl
    // alone would re-run a live check on the stale word in the buffer.
    private static bool IsLiveEligibleKeystroke(int virtualKey)
    {
        if (virtualKey == NativeMethods.VkBack || virtualKey == NativeMethods.VkDelete)
        {
            return false;
        }

        if (virtualKey is NativeMethods.VkShift or NativeMethods.VkControl or NativeMethods.VkMenu or NativeMethods.VkCapital)
        {
            return false;
        }

        // Left/right Shift, Control, Menu (Alt) and the Windows keys.
        if (virtualKey is >= 0xA0 and <= 0xA5 or 0x5B or 0x5C)
        {
            return false;
        }

        return true;
    }

    private static bool IsKeyboardSelectionGesture(int virtualKey)
    {
        bool shiftDown = IsKeyDown(NativeMethods.VkShift);
        bool controlDown = IsKeyDown(NativeMethods.VkControl);
        return (shiftDown && virtualKey is >= 0x21 and <= 0x28) ||
               (controlDown && virtualKey == 0x41);
    }

    private static bool IsSelectionPreservingShortcut(int virtualKey)
    {
        if (!IsKeyDown(NativeMethods.VkControl))
        {
            return false;
        }

        return virtualKey is 0x43 or 0x2D;
    }

    private static bool IsModifierKeystroke(int virtualKey)
    {
        return virtualKey is NativeMethods.VkCapital
            or NativeMethods.VkShift
            or NativeMethods.VkControl
            or NativeMethods.VkMenu
            or 0x5B
            or 0x5C
            or >= 0xA0 and <= 0xA5;
    }

    private static bool WaitForHotkeyModifiersReleased()
    {
        const int timeoutMs = 2000;
        const int pollMs = 10;

        for (int elapsed = 0; elapsed < timeoutMs; elapsed += pollMs)
        {
            if (!IsHotkeyModifierDown())
            {
                return true;
            }

            System.Threading.Thread.Sleep(pollMs);
        }

        Logger.Log("Hotkey action skipped: modifiers were held for more than 2 seconds");
        return false;
    }

    private static bool IsHotkeyModifierDown()
    {
        return IsKeyDown(NativeMethods.VkControl) ||
               IsKeyDown(NativeMethods.VkShift) ||
               IsKeyDown(NativeMethods.VkMenu) ||
               IsKeyDown(0x5B) ||
               IsKeyDown(0x5C);
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
    }

    private void BeginInvokeOnUi(Action action)
    {
        if (_marshal.IsDisposed)
        {
            return;
        }

        _marshal.BeginInvoke(action);
    }

    private void HandleConversionResult(ConversionOutcome outcome, bool fromSelection, bool suppressLearning)
    {
        switch (outcome.Result)
        {
            case ConversionResult.Success:
                if (_settings.ClearBufferAfterConversion && !outcome.WasCapitalizationUndo)
                {
                    _keyboardBuffer.Clear();
                }

                if (_settings.PlaySoundOnConversion)
                {
                    if (outcome.WasRevert || outcome.WasCapitalizationUndo)
                    {
                        SoundFeedback.PlayRevert();
                    }
                    else
                    {
                        SoundFeedback.PlaySuccess();
                    }
                }

                LearnFromOutcome(outcome, fromSelection, suppressLearning);
                break;
            case ConversionResult.NoConvertibleWord:
                ShowNotification("NotificationNoConvertibleWord", ToolTipIcon.Warning);
                break;
            case ConversionResult.SendFailed:
                DialogService.ShowError(null, "SendInputFailed");
                break;
        }
    }

    private void LearnFromOutcome(ConversionOutcome outcome, bool fromSelection, bool suppressLearning)
    {
        if (outcome.Result != ConversionResult.Success)
        {
            return;
        }

        if (outcome.WasCapitalizationUndo)
        {
            return;
        }

        // When automatic layout correction is disabled, the last-word hotkey is
        // an explicit one-off tool. Do not silently learn or prompt for words
        // converted through that hotkey; the user intentionally turned off auto
        // learning context for layout decisions.
        if (suppressLearning)
        {
            return;
        }

        if (outcome.WasRevert)
        {
            // Reverting undoes a conversion. Only learn when it undoes an AUTO
            // conversion: the user typed the original correctly and auto guessed
            // wrong, so the restored original is the word to remember. Reverting
            // a manual convert is an intentional undo of gibberish — don't learn.
            if (!outcome.FromAuto || _settings.ManualLearnMode == RememberMode.Never)
            {
                return;
            }

            if (outcome.Language is not (ConvertedLanguage.Russian or ConvertedLanguage.English))
            {
                return;
            }

            // Strip layout punctuation that rode along with the word (e.g. the
            // restored text "хошь?" must be learned as "хошь").
            string word = TextNormalizer.TrimToWord(outcome.ConvertedText);
            if (word.Length == 0)
            {
                return;
            }

            // Apply the same ManualLearnMode as a normal conversion: Remember ->
            // silent add; Ask -> prompt. Without this, an Ask-mode user would see
            // the prompt only on the second Break (the first Break reverted the
            // auto-conversion silently), which felt like the prompt "didn't fire".
            if (_settings.ManualLearnMode == RememberMode.Ask)
            {
                PromptAddToUserDictionary(word, outcome.Language);
            }
            else if (_userDictionary.Add(outcome.Language, word))
            {
                ShowWordAddedPopup(word, outcome.Language);
            }

            return;
        }

        if (outcome.Language is not (ConvertedLanguage.Russian or ConvertedLanguage.English))
        {
            return;
        }

        // Never learn punctuation/symbol-only tokens — they are not words.
        if (!ConversionService.ContainsLetter(outcome.OriginalText))
        {
            return;
        }

        // Strip surrounding layout punctuation so the learned word is bare
        // (e.g. converted "хошь," is learned as "хошь").
        string learnedWord = TextNormalizer.TrimToWord(outcome.ConvertedText);
        if (learnedWord.Length == 0)
        {
            return;
        }

        // Shift+Pause (selection): the mode is either Ask or Never; selection
        // learning is never silent. Only single words are eligible.
        if (fromSelection)
        {
            if (!outcome.IsSingleWord || _settings.SelectionLearnMode != RememberMode.Ask)
            {
                return;
            }

            PromptAddToUserDictionary(learnedWord, outcome.Language);
            return;
        }

        // The automatic logic already converts this word by itself; it was only
        // held back this time (Caps Lock, sentence context, an exclusion...).
        // Remembering it adds nothing, and asking about a common word is noise.
        if (outcome.AlreadyRecognized)
        {
            return;
        }

        // Pause (last word): Remember -> silent add; Ask -> prompt; Never -> nothing.
        switch (_settings.ManualLearnMode)
        {
            case RememberMode.Remember:
                if (_userDictionary.Add(outcome.Language, learnedWord))
                {
                    ShowWordAddedPopup(learnedWord, outcome.Language);
                }
                break;
            case RememberMode.Ask:
                PromptAddToUserDictionary(learnedWord, outcome.Language);
                break;
            case RememberMode.Never:
                break;
        }
    }

    private void PromptAddToUserDictionary(string word, ConvertedLanguage language)
    {
        var prompt = TrayPrompt.AddToDictionary(word, language);
        prompt.Decision += (_, accepted) =>
        {
            if (accepted && _userDictionary.Add(language, word))
            {
                ShowNotificationText(Localizer.Format("NotificationWordAddedToUserDictionary", word));
            }
        };
        prompt.Show();
    }

    private void ShowWordAddedPopup(string word, ConvertedLanguage language)
    {
        if (!_settings.ShowNotifications)
        {
            return;
        }

        if (_wordAddedPopup is { IsDisposed: false })
        {
            _wordAddedPopup.Close();
        }

        var popup = new WordAddedPopup(word, language);
        _wordAddedPopup = popup;
        popup.Decision += (_, undone) =>
        {
            if (undone && _userDictionary.Remove(language, word))
            {
                ShowNotificationText(Localizer.Format("NotificationWordRemovedFromUserDictionary", word));
            }
        };
        popup.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_wordAddedPopup, popup))
            {
                _wordAddedPopup = null;
            }
        };
        popup.Show();
    }

    private void ToggleEnabled()
    {
        // The hotkey now toggles auto-correction (layout auto-switching).
        // Manual conversion always stays available.
        _settings.AutoMode = !_settings.AutoMode;
        ApplySettings();
        TrySaveSettings();
        SyncOpenWindows();
        ShowNotification(_settings.AutoMode ? "NotificationAutoModeEnabled" : "NotificationAutoModeDisabled");
    }

    private static string GetHotkeyActionName(HotkeyAction action)
    {
        return action switch
        {
            HotkeyAction.ConvertLastWord => Localizer.Get("SettingsHotkeyConvertLastWord"),
            HotkeyAction.ConvertSelection => Localizer.Get("SettingsHotkeyConvertSelection"),
            HotkeyAction.ToggleEnabled => Localizer.Get("SettingsHotkeyToggleEnabled"),
            HotkeyAction.ToggleCase => Localizer.Get("SettingsHotkeyToggleCase"),
            HotkeyAction.Transliterate => Localizer.Get("SettingsHotkeyTransliterate"),
            _ => action.ToString()
        };
    }
}
