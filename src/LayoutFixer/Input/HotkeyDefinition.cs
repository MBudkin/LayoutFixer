using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;

namespace LayoutFixer.Input;

internal readonly record struct HotkeyDefinition(HotkeyModifiers Modifiers, Keys Key)
{
    public static bool TryParse(string? value, out HotkeyDefinition hotkey)
    {
        hotkey = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        string? keyToken = null;

        foreach (string token in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Ctrl;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "win":
                case "windows":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    keyToken = token;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(keyToken))
        {
            return false;
        }

        try
        {
            var converter = new KeysConverter();
            if (converter.ConvertFromInvariantString(keyToken) is not Keys key || IsModifierKey(key))
            {
                return false;
            }

            hotkey = new HotkeyDefinition(modifiers, key);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryFromKeyEvent(KeyEventArgs args, out HotkeyDefinition hotkey)
    {
        hotkey = default;

        Keys key = args.KeyCode;
        if (IsModifierKey(key))
        {
            return false;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if (args.Control)
        {
            modifiers |= HotkeyModifiers.Ctrl;
        }

        if (args.Shift)
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (args.Alt)
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        hotkey = new HotkeyDefinition(modifiers, key);
        return true;
    }

    public static bool HasDuplicates(params string[] hotkeys)
    {
        var normalized = hotkeys
            .Select(value => TryParse(value, out HotkeyDefinition parsed) ? parsed.ToString() : string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length;
    }

    public override string ToString()
    {
        string keyName = new KeysConverter().ConvertToInvariantString(Key) ?? Key.ToString();
        string prefix = string.Empty;

        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            prefix += "Ctrl+";
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            prefix += "Shift+";
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            prefix += "Alt+";
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            prefix += "Win+";
        }

        return prefix + keyName;
    }

    public bool MatchesCurrentKeyDown(int virtualKey)
    {
        if ((int)(Key & Keys.KeyCode) != virtualKey)
        {
            return false;
        }

        HotkeyModifiers current = HotkeyModifiers.None;
        if (IsKeyDown(NativeMethods.VkControl))
        {
            current |= HotkeyModifiers.Ctrl;
        }

        if (IsKeyDown(NativeMethods.VkShift))
        {
            current |= HotkeyModifiers.Shift;
        }

        if (IsKeyDown(NativeMethods.VkMenu))
        {
            current |= HotkeyModifiers.Alt;
        }

        if (IsKeyDown(0x5B) || IsKeyDown(0x5C))
        {
            current |= HotkeyModifiers.Win;
        }

        return current == Modifiers;
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
    }

    private static bool IsModifierKey(Keys key)
    {
        return key is Keys.ControlKey
            or Keys.LControlKey
            or Keys.RControlKey
            or Keys.ShiftKey
            or Keys.LShiftKey
            or Keys.RShiftKey
            or Keys.Menu
            or Keys.LMenu
            or Keys.RMenu
            or Keys.LWin
            or Keys.RWin;
    }
}
