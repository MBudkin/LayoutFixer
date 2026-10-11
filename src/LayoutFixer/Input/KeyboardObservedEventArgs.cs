using System;

namespace LayoutFixer.Input;

internal sealed class KeyboardObservedEventArgs(int virtualKey, int scanCode) : EventArgs
{
    public int VirtualKey { get; } = virtualKey;

    public int ScanCode { get; } = scanCode;

    // Set by the observer when the keystroke has been fully handled (e.g. a
    // live layout conversion replaced the typed tail) and must NOT be forwarded
    // to the focused application. Returning the keystroke as-is would let the
    // OS deliver it on top of the injected replacement, producing a duplicate
    // trailing character.
    public bool Suppress { get; set; }
}
