using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Input;

internal static class TextInputSender
{
    // The batch collecting replacements while a separator keystroke is being
    // handled inside the keyboard hook (see InputBatch). Thread-static: only
    // the hook (UI) thread ever opens one; background hotkey tasks always send
    // immediately.
    [ThreadStatic]
    private static InputBatch? _batch;

    public static bool IsBatching => _batch is not null;

    /// <summary>
    /// Starts collecting replacements instead of sending them. Used for the
    /// separator keystroke that the hook is still holding: <paramref name="pendingTail"/>
    /// is the separator character the keyboard buffer already contains but the
    /// application has not received yet.
    /// </summary>
    public static InputBatch BeginBatch(string pendingTail)
    {
        if (_batch is not null)
        {
            throw new InvalidOperationException("An input batch is already open.");
        }

        _batch = new InputBatch(pendingTail);
        return _batch;
    }

    internal static void EndBatch(InputBatch batch)
    {
        if (ReferenceEquals(_batch, batch))
        {
            _batch = null;
        }
    }

    public static bool ReplaceTail(int backspaceCount, string replacement)
    {
        Logger.Log($"ReplaceTail: backspaces={backspaceCount} replacementLen={replacement.Length} batched={IsBatching}");
        Logger.Log(Logger.Format("replacement", replacement));

        if (_batch is { } batch)
        {
            batch.ReplaceTail(backspaceCount, replacement);
            return true;
        }

        var inputs = new List<NativeMethods.Input>(backspaceCount * 2 + replacement.Length * 2);
        AppendKeyPresses(inputs, NativeMethods.VkBack, backspaceCount);
        AppendText(inputs, replacement);
        bool sent = Send(inputs);
        Logger.Log(sent ? "ReplaceTail send complete" : "ReplaceTail send FAILED");
        return sent;
    }

    /// <summary>
    /// Sends text without preceding backspaces. Used to replace an active selection:
    /// because there is a selection, the typed text automatically replaces it.
    /// </summary>
    public static bool SendString(string text)
    {
        Logger.Log($"SendString: len={text.Length}");
        Logger.Log(Logger.Format("text", text));

        var inputs = new List<NativeMethods.Input>(text.Length * 2);
        AppendText(inputs, text);
        bool sent = Send(inputs);
        Logger.Log(sent ? "SendString send complete" : "SendString send FAILED");
        return sent;
    }

    public static bool TurnOffCapsLock()
    {
        if ((NativeMethods.GetKeyState(NativeMethods.VkCapital) & 1) == 0)
        {
            return true;
        }

        if (_batch is { } batch)
        {
            batch.AppendKeyAfterText(NativeMethods.VkCapital);
            return true;
        }

        var inputs = new List<NativeMethods.Input>(2);
        AppendKeyPresses(inputs, NativeMethods.VkCapital, 1);
        bool sent = Send(inputs);
        Logger.Log($"TurnOffCapsLock: sent={sent}");
        return sent;
    }

    // All events of one replacement go through a SINGLE SendInput call.
    // SendInput guarantees that the events of one call are not interleaved
    // with other keyboard input, so a key the user presses while a
    // replacement is in flight can no longer land between our backspaces and
    // the new text (which used to delete or garble neighbouring characters).
    internal static bool Send(List<NativeMethods.Input> inputs)
    {
        if (inputs.Count == 0)
        {
            return true;
        }

        NativeMethods.Input[] array = inputs.ToArray();
        uint sent = NativeMethods.SendInput((uint)array.Length, array, Marshal.SizeOf<NativeMethods.Input>());
        if (sent != array.Length)
        {
            Logger.Log($"SendInput sent {sent} of {array.Length} events (error {Marshal.GetLastWin32Error()})");
        }

        return sent == array.Length;
    }

    internal static void AppendKeyPresses(List<NativeMethods.Input> inputs, int virtualKey, int count)
    {
        ushort scanCode = (ushort)NativeMethods.MapVirtualKey((uint)virtualKey, NativeMethods.MapvkVkToVsc);
        for (int index = 0; index < count; index++)
        {
            inputs.Add(KeyboardInput((ushort)virtualKey, scanCode, 0));
            inputs.Add(KeyboardInput((ushort)virtualKey, scanCode, NativeMethods.KeyeventfKeyup));
        }
    }

    /// <summary>
    /// Appends a string, emitting real Return/Tab keypresses for line breaks
    /// and tabs (\r, \n, \r\n, \t) instead of WM_CHAR control characters. Most
    /// editors ignore WM_CHAR newlines, and a real Tab keeps indentation and
    /// focus navigation working the way the user's own key would.
    /// </summary>
    internal static void AppendText(List<NativeMethods.Input> inputs, string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];

            if (character == '\r' || character == '\n')
            {
                AppendKeyPresses(inputs, NativeMethods.VkReturn, 1);

                // Treat CR+LF as a single line break.
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                continue;
            }

            if (character == '\t')
            {
                AppendKeyPresses(inputs, NativeMethods.VkTab, 1);
                continue;
            }

            inputs.Add(KeyboardInput(0, character, NativeMethods.KeyeventfUnicode));
            inputs.Add(KeyboardInput(0, character, NativeMethods.KeyeventfUnicode | NativeMethods.KeyeventfKeyup));
        }
    }

    private static NativeMethods.Input KeyboardInput(ushort virtualKey, ushort scanCode, uint flags)
    {
        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                Ki = new NativeMethods.KeyboardInput
                {
                    WVk = virtualKey,
                    WScan = scanCode,
                    DwFlags = flags
                }
            }
        };
    }
}

/// <summary>
/// Collects the replacements made while the keyboard hook still holds a
/// separator keystroke (Space/Enter/Tab/"-") and turns them into one
/// replacement relative to what is actually on screen.
/// </summary>
/// <remarks>
/// The keyboard buffer already contains the separator, the application does
/// not (the low-level hook runs before delivery). Each step of the separator
/// pipeline (user replacement, layout fix, Caps Lock fix, capitalisation)
/// edits the buffer tail; the batch composes those edits starting from
/// "the separator still has to be typed". When nothing changed, the original
/// keystroke is simply let through. Otherwise the hook suppresses it and
/// <see cref="Commit"/> sends the backspaces, the new text and the separator
/// itself in one atomic SendInput call, BEFORE the application processes the
/// separator. This is what makes "word + Enter" in a messenger send the
/// corrected word instead of sending the typo and typing the fix afterwards.
/// </remarks>
internal sealed class InputBatch : IDisposable
{
    private readonly string _pendingTail;
    private readonly List<int> _keysAfterText = new();
    private int _backspaces;
    private string _text;

    public InputBatch(string pendingTail)
    {
        _pendingTail = pendingTail;
        _text = pendingTail;
    }

    public bool HasChanges =>
        _backspaces != 0 ||
        !string.Equals(_text, _pendingTail, StringComparison.Ordinal) ||
        _keysAfterText.Count > 0;

    public int Backspaces => _backspaces;

    public string Text => _text;

    public void ReplaceTail(int count, string replacement)
    {
        if (count <= _text.Length)
        {
            _text = _text.Substring(0, _text.Length - count) + replacement;
            return;
        }

        _backspaces += count - _text.Length;
        _text = replacement;
    }

    public void AppendKeyAfterText(int virtualKey)
    {
        _keysAfterText.Add(virtualKey);
    }

    public bool Commit()
    {
        var inputs = new List<NativeMethods.Input>(_backspaces * 2 + _text.Length * 2 + _keysAfterText.Count * 2);
        TextInputSender.AppendKeyPresses(inputs, NativeMethods.VkBack, _backspaces);
        TextInputSender.AppendText(inputs, _text);
        foreach (int key in _keysAfterText)
        {
            TextInputSender.AppendKeyPresses(inputs, key, 1);
        }

        Logger.Log($"InputBatch.Commit: backspaces={_backspaces} textLen={_text.Length} extraKeys={_keysAfterText.Count}");
        return TextInputSender.Send(inputs);
    }

    public void Dispose()
    {
        TextInputSender.EndBatch(this);
    }
}
