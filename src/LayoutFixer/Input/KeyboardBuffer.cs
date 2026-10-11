using System;
using System.Text;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Input;

internal sealed class KeyboardBuffer
{
    private const int DefaultMaxLength = 128;
    // Word boundaries. Whitespace, layout-independent punctuation (!, (, )) and
    // the hyphen (-) act as separators. The hyphen is a separator so compound
    // words typed in the wrong layout are checked part by part (e.g. "rjulf-nj"
    // -> "когда" + "то"), since it maps to itself on both layouts. Keys that DO
    // map to a Russian letter (',', '.', '/', '?', '"', "'", '[', ']', etc.)
    // are NOT separators — they convert together with the word.
    // NOTE: the double-quote (") maps to the Russian letter "Э" (on the same
    // physical key as the apostrophe, which maps to "э"), so it is treated as
    // part of a word and converts with it. This lets '"nj' convert as one token
    // into 'Это', and a Russian 'ЭрудзЭ' into '"help"'. Which physical key
    // produced a '"' is resolved by DetectLanguage from the surrounding letters
    // (Latin around -> the Э key -> "Э"; Cyrillic around -> the 2 key -> "@").
    // Phrase breaks between quoted words are still correct because real typing
    // separates them with a space, which remains a separator.
    private static readonly string Separators = " \t\r\n!()-";
    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();
    private readonly int _maxLength;

    public KeyboardBuffer(int maxLength = DefaultMaxLength)
    {
        _maxLength = maxLength;
    }

    // Returns true when the keystroke caused an "untrackable" buffer reset
    // (Ctrl+Backspace/Ctrl+Delete, any Ctrl/Alt combo, navigation). The caller
    // uses that signal to drop the pending undo state in ConversionService: a
    // reset invalidates any last conversion, otherwise a stale toggle could
    // revert (and learn) a freshly retyped word that merely ends with the old
    // converted text.
    public bool Observe(KeyboardObservedEventArgs args)
    {
        lock (_gate)
        {
            int vk = args.VirtualKey;

            // Deletion keys.
            if (vk is NativeMethods.VkBack or NativeMethods.VkDelete)
            {
                if (IsKeyDown(NativeMethods.VkControl))
                {
                    // Ctrl+Backspace / Ctrl+Delete remove a whole word, but every
                    // editor applies its OWN word boundaries (one editor drops a
                    // single trailing period, another drops the whole word). The
                    // buffer cannot mirror that, so a precise RemoveLast() would
                    // leave the model out of sync with the real text and the next
                    // ReplaceTail() would delete the wrong number of characters
                    // (eating neighbouring text). Reset the model entirely.
                    Logger.Log($"Observe: VK=0x{vk:X2} + Ctrl -> Clear (untrackable word delete)");
                    Clear();
                    return true;
                }

                Logger.Log($"Observe: VK=0x{vk:X2} -> RemoveLast");
                RemoveLast();
                return false;
            }

            if (IsNavigationKey(vk))
            {
                Logger.Log($"Observe: VK=0x{vk:X2} -> Clear (navigation)");
                Clear();
                return true;
            }

            // Modifier-only keystrokes (Shift/Ctrl/Alt/CapsLock pressed or
            // released on their own) never affect the buffer.
            if (IsModifierKey(vk))
            {
                return false;
            }

            // Any other key pressed together with Ctrl or Alt (paste, cut, undo,
            // redo, select-all, Ctrl+Space layout switch, ...) changes the editor
            // text in a way the buffer cannot reverse-model. Reset so the next
            // conversion never operates on a stale tail.
            if (IsControlOrAltDown())
            {
                Logger.Log($"Observe: VK=0x{vk:X2} + Ctrl/Alt -> Clear (untrackable edit)");
                Clear();
                return true;
            }

            if (!IsTypableCharacter(vk))
            {
                return false;
            }

            char? character = TranslateKey(vk, (uint)args.ScanCode);
            if (character is null)
            {
                Logger.Log($"Observe: VK=0x{vk:X2} scan=0x{args.ScanCode:X2} -> null");
                return false;
            }

            Logger.Log($"Observe: VK=0x{vk:X2} scan=0x{args.ScanCode:X2} -> U+{((int)character.Value):X4} '{character.Value}'");
            Append(character.Value);
            return false;
        }
    }

    private static bool IsNavigationKey(int virtualKey)
    {
        return virtualKey is >= 0x21 and <= 0x28 or 0x2D;
    }

    private static bool IsTypableCharacter(int virtualKey)
    {
        if (virtualKey == NativeMethods.VkSpace ||
            virtualKey == NativeMethods.VkTab ||
            virtualKey == NativeMethods.VkReturn)
        {
            return true;
        }

        if (IsModifierKey(virtualKey))
        {
            return false;
        }

        // Digits, A-Z, numpad, and OEM symbol keys. The OEM range matters because
        // , . ; ' [ ] ` \ / and their shifted variants (< > : " { } ~ | ?) carry
        // Russian letters on the RU layout (б ю ж э х ъ ё / | Х Ъ ...). Capturing
        // them lets a whole wrong-layout word convert (e.g. ",.hj" -> "бюро").
        return (virtualKey >= 0x30 && virtualKey <= 0x39) ||  // 0-9
               (virtualKey >= 0x41 && virtualKey <= 0x5A) ||  // A-Z
               (virtualKey >= 0x60 && virtualKey <= 0x6F) ||  // numpad
               (virtualKey >= 0xBA && virtualKey <= 0xE2);    // OEM symbol keys
    }

    public bool TryGetLastToken(out BufferToken token)
    {
        lock (_gate)
        {
            token = default;

            if (_buffer.Length == 0)
            {
                return false;
            }

            int end = _buffer.Length - 1;
            while (end >= 0 && IsSeparator(_buffer[end]))
            {
                end--;
            }

            if (end < 0)
            {
                return false;
            }

            int start = end;
            while (start >= 0 && !IsSeparator(_buffer[start]))
            {
                start--;
            }

            string text = _buffer.ToString(start + 1, end - start);
            string trailing = _buffer.ToString(end + 1, _buffer.Length - end - 1);
            token = new BufferToken(text, trailing);
            return true;
        }
    }

    // The word typed before the last token, without its separators, or null
    // when the buffer holds no earlier word. Used as sentence context for very
    // short tokens ("plan b" stays English when "plan" stayed English).
    public string? GetWordBeforeLastToken()
    {
        lock (_gate)
        {
            int end = _buffer.Length - 1;
            while (end >= 0 && IsSeparator(_buffer[end]))
            {
                end--;
            }

            while (end >= 0 && !IsSeparator(_buffer[end]))
            {
                end--;
            }

            // Only a real whitespace gap counts as "previous word in the same
            // sentence"; "(" or "!" glued tokens are left alone.
            // "!" ends the sentence and a line break starts a new message or
            // paragraph: "Hello! d" and "thanks \nz" have no sentence context.
            bool sawSpace = false;
            bool sawBoundary = false;
            while (end >= 0 && IsSeparator(_buffer[end]))
            {
                sawSpace |= _buffer[end] == ' ';
                sawBoundary |= _buffer[end] is '!' or '\n' or '\r';
                end--;
            }

            if (end < 0 || !sawSpace || sawBoundary)
            {
                return null;
            }

            int start = end;
            while (start >= 0 && !IsSeparator(_buffer[start]))
            {
                start--;
            }

            return _buffer.ToString(start + 1, end - start);
        }
    }

    // The text of the current sentence before the last token: everything
    // after the last ".", "!", "?", "…" or line break. Tells whether English
    // words around a short token are terms inside a Russian sentence.
    public string GetSentenceBeforeLastToken()
    {
        lock (_gate)
        {
            int end = _buffer.Length - 1;
            while (end >= 0 && IsSeparator(_buffer[end]))
            {
                end--;
            }

            while (end >= 0 && !IsSeparator(_buffer[end]))
            {
                end--;
            }

            int start = end;
            while (start >= 0 && _buffer[start] is not ('.' or '!' or '?' or '…' or '\n' or '\r'))
            {
                start--;
            }

            return end > start ? _buffer.ToString(start + 1, end - start) : string.Empty;
        }
    }

    // The token right before the last one and the exact separators between
    // them: "z" and " " for "hello z levf". Lets a single-letter word be fixed
    // together with the wrong-layout word that follows it.
    public bool TryGetTokenBeforeLastToken(out string word, out string gap)
    {
        lock (_gate)
        {
            word = string.Empty;
            gap = string.Empty;

            int end = _buffer.Length - 1;
            while (end >= 0 && IsSeparator(_buffer[end]))
            {
                end--;
            }

            while (end >= 0 && !IsSeparator(_buffer[end]))
            {
                end--;
            }

            int gapEnd = end;
            while (end >= 0 && IsSeparator(_buffer[end]))
            {
                end--;
            }

            if (end < 0 || end == gapEnd)
            {
                return false;
            }

            int start = end;
            while (start >= 0 && !IsSeparator(_buffer[start]))
            {
                start--;
            }

            word = _buffer.ToString(start + 1, end - start);
            gap = _buffer.ToString(end + 1, gapEnd - end);
            return true;
        }
    }

    public bool IsLastTokenAfterSentenceTerminator()
    {
        lock (_gate)
        {
            if (_buffer.Length == 0)
            {
                return false;
            }

            int end = _buffer.Length - 1;
            while (end >= 0 && IsSeparator(_buffer[end]))
            {
                end--;
            }

            if (end < 0)
            {
                return false;
            }

            int start = end;
            while (start >= 0 && !IsSeparator(_buffer[start]))
            {
                start--;
            }

            // The requested rule requires a real whitespace gap: ". word",
            // not a punctuation mark glued directly to the word.
            int cursor = start;
            bool hasWhitespace = false;
            while (cursor >= 0 && char.IsWhiteSpace(_buffer[cursor]))
            {
                hasWhitespace = true;
                cursor--;
            }

            if (!hasWhitespace)
            {
                return false;
            }

            // Allow a sentence-ending mark immediately inside a closing quote
            // or bracket: `"Hello!" next` is still a new sentence boundary.
            while (cursor >= 0 && IsClosingWrapper(_buffer[cursor]))
            {
                cursor--;
            }

            return cursor >= 0 && _buffer[cursor] is '.' or '!' or '?';
        }
    }

    // The last recorded character, or null for an empty buffer.
    public char? LastCharacter
    {
        get
        {
            lock (_gate)
            {
                return _buffer.Length == 0 ? null : _buffer[_buffer.Length - 1];
            }
        }
    }

    public bool TailEquals(string expected)
    {
        lock (_gate)
        {
            if (expected is null || _buffer.Length < expected.Length)
            {
                return false;
            }

            int offset = _buffer.Length - expected.Length;
            for (int index = 0; index < expected.Length; index++)
            {
                if (_buffer[offset + index] != expected[index])
                {
                    return false;
                }
            }

            return true;
        }
    }

    public void ReplaceTail(int count, string replacement)
    {
        lock (_gate)
        {
            int safeCount = Math.Min(count, _buffer.Length);
            _buffer.Remove(_buffer.Length - safeCount, safeCount);
            Append(replacement);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _buffer.Clear();
        }
    }

    public void OnPointerAction()
    {
        lock (_gate)
        {
            if (_buffer.Length > 0)
            {
                Logger.Log("OnPointerAction: clearing buffer");
                _buffer.Clear();
            }
        }
    }

    public string GetSnapshot()
    {
        lock (_gate)
        {
            return _buffer.ToString();
        }
    }

    private void Append(char character)
    {
        _buffer.Append(character);
        Trim();
    }

    private void Append(string text)
    {
        _buffer.Append(text);
        Trim();
    }

    private void RemoveLast()
    {
        if (_buffer.Length > 0)
        {
            _buffer.Remove(_buffer.Length - 1, 1);
        }
    }

    private void Trim()
    {
        if (_buffer.Length > _maxLength)
        {
            _buffer.Remove(0, _buffer.Length - _maxLength);
        }
    }

    private static bool IsSeparator(char character)
    {
        return Separators.IndexOf(character) >= 0;
    }

    private static bool IsModifierKey(int virtualKey)
    {
        return virtualKey is NativeMethods.VkCapital
            or NativeMethods.VkShift
            or NativeMethods.VkControl
            or NativeMethods.VkMenu
            or 0x5B
            or 0x5C
            or >= 0xA0 and <= 0xA5;
    }

    private static bool IsClosingWrapper(char character)
    {
        return character is '"' or '\'' or ')' or ']' or '}' or '>' or '»';
    }

    private static char? TranslateKey(int virtualKey, uint scanCodeFromHook)
    {
        if (IsControlOrAltDown())
        {
            Logger.Log($"  TranslateKey: VK=0x{virtualKey:X2} -> null (ctrl/alt down)");
            return null;
        }

        if (virtualKey == NativeMethods.VkSpace)
        {
            return ' ';
        }

        if (virtualKey == NativeMethods.VkTab)
        {
            return '\t';
        }

        if (virtualKey == NativeMethods.VkReturn)
        {
            return '\n';
        }

        bool shift = IsKeyDown(NativeMethods.VkShift);
        bool capsLock = (NativeMethods.GetKeyState(NativeMethods.VkCapital) & 1) != 0;

        IntPtr focusedWindow = GetFocusedOrForegroundWindow();
        uint threadId = focusedWindow == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(focusedWindow, IntPtr.Zero);
        IntPtr keyboardLayout = NativeMethods.GetKeyboardLayout(threadId);
        ushort langId = (ushort)((long)keyboardLayout & 0xFFFF);

        uint scanCode = scanCodeFromHook;
        if (scanCode == 0)
        {
            scanCode = NativeMethods.MapVirtualKeyEx((uint)virtualKey, NativeMethods.MapvkVkToVsc, keyboardLayout);
        }

        // Reliable path: use our own scan-code table for the standard English/Russian
        // layouts. ToUnicodeEx is unreliable when the focused window is in another
        // process and can return completely wrong characters.
        if (ScanCodeMap.IsSupported(langId) &&
            ScanCodeMap.TryLookup(scanCode, ApplyShift(shift, capsLock, virtualKey), langId, out char mapped))
        {
            Logger.Log($"  TranslateKey(scan-map): VK=0x{virtualKey:X2} scan=0x{scanCode:X2} hkl=0x{(long)keyboardLayout:X} lang=0x{langId:X4} shift={shift} caps={capsLock} -> U+{((int)mapped):X4} '{mapped}'");
            return mapped;
        }

        // Fallback for unsupported layouts.
        var keyState = new byte[256];
        if (shift)
        {
            keyState[NativeMethods.VkShift] = 0x80;
        }

        if (capsLock)
        {
            keyState[NativeMethods.VkCapital] = 1;
        }

        var buffer = new StringBuilder(8);
        int result = NativeMethods.ToUnicodeEx((uint)virtualKey, scanCode, keyState, buffer, buffer.Capacity, 0, keyboardLayout);

        Logger.Log($"  TranslateKey(tounicode): VK=0x{virtualKey:X2} scan=0x{scanCode:X2} hkl=0x{(long)keyboardLayout:X} lang=0x{langId:X4} result={result} char={(result > 0 ? $"U+{((int)buffer[0]):X4}" : "none")}");

        if (result < 0)
        {
            // Dead key — flush translator state so subsequent keys are not composed.
            buffer.Length = 0;
            NativeMethods.ToUnicodeEx((uint)virtualKey, scanCode, keyState, buffer, buffer.Capacity, 0, keyboardLayout);
            return null;
        }

        return result > 0 ? buffer[0] : null;
    }

    private static bool ApplyShift(bool shift, bool capsLock, int virtualKey)
    {
        // For letters, CapsLock toggles shift. For other keys, CapsLock has no effect.
        if ((virtualKey >= 0x41 && virtualKey <= 0x5A))
        {
            return shift ^ capsLock;
        }

        return shift;
    }

    private static IntPtr GetFocusedOrForegroundWindow()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        uint threadId = NativeMethods.GetWindowThreadProcessId(foreground, IntPtr.Zero);
        if (threadId == 0)
        {
            return foreground;
        }

        var info = new NativeMethods.GuiThreadInfo
        {
            CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
        };

        if (NativeMethods.GetGUIThreadInfo(threadId, ref info) && info.HwndFocus != IntPtr.Zero)
        {
            return info.HwndFocus;
        }

        return foreground;
    }

    private static bool IsControlOrAltDown()
    {
        return IsKeyDown(NativeMethods.VkControl) || IsKeyDown(NativeMethods.VkMenu);
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
    }
}
