using System;
using System.Collections.Generic;
using System.Windows.Forms;
using LayoutFixer.Settings;

namespace LayoutFixer.Input;

internal sealed class HotkeyManager : IDisposable
{
    private readonly MessageWindow _window;
    private readonly List<int> _registeredIds = new();
    private readonly List<HotkeyDefinition> _registeredHotkeys = new();

    public HotkeyManager()
    {
        _window = new MessageWindow(action => HotkeyPressed?.Invoke(action));
    }

    public event Action<HotkeyAction>? HotkeyPressed;

    public bool MatchesRegisteredHotkeyKeyDown(int virtualKey)
    {
        foreach (HotkeyDefinition hotkey in _registeredHotkeys)
        {
            if (hotkey.MatchesCurrentKeyDown(virtualKey))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<HotkeyRegistrationError> Apply(AppSettings settings)
    {
        UnregisterAll();

        var errors = new List<HotkeyRegistrationError>();
        Register(settings.Hotkeys.ConvertLastWord, HotkeyAction.ConvertLastWord, errors);
        Register(settings.Hotkeys.ConvertSelection, HotkeyAction.ConvertSelection, errors);
        Register(settings.Hotkeys.ToggleEnabled, HotkeyAction.ToggleEnabled, errors);
        Register(settings.Hotkeys.ToggleCase, HotkeyAction.ToggleCase, errors);
        Register(settings.Hotkeys.Transliterate, HotkeyAction.Transliterate, errors);
        return errors;
    }

    public void Suspend()
    {
        UnregisterAll();
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.DestroyHandle();
    }

    private void Register(string hotkeyText, HotkeyAction action, List<HotkeyRegistrationError> errors)
    {
        if (!HotkeyDefinition.TryParse(hotkeyText, out HotkeyDefinition hotkey))
        {
            errors.Add(new HotkeyRegistrationError(action, hotkeyText, InvalidFormat: true));
            return;
        }

        bool registered = NativeMethods.RegisterHotKey(
            _window.Handle,
            (int)action,
            (uint)hotkey.Modifiers,
            (uint)hotkey.Key);

        if (!registered)
        {
            errors.Add(new HotkeyRegistrationError(action, hotkey.ToString(), InvalidFormat: false));
            return;
        }

        _registeredIds.Add((int)action);
        _registeredHotkeys.Add(hotkey);
    }

    private void UnregisterAll()
    {
        foreach (int id in _registeredIds)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, id);
        }

        _registeredIds.Clear();
        _registeredHotkeys.Clear();
    }

    private sealed class MessageWindow : NativeWindow
    {
        private readonly Action<HotkeyAction> _onHotkey;

        public MessageWindow(Action<HotkeyAction> onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WmHotkey)
            {
                _onHotkey((HotkeyAction)message.WParam.ToInt32());
                return;
            }

            base.WndProc(ref message);
        }
    }
}
