using System;

namespace LayoutFixer.Input;

internal readonly record struct BufferToken(string Text, string Trailing)
{
    public string OriginalSegment => Text + Trailing;

    public int OriginalLength => OriginalSegment.Length;

    // A word separator was typed after this word BEFORE the last trailing
    // character ("word  ", "word -", "word\n\n"): the separator pipeline has
    // already had its turn on this word. "word! " does not count, "!" alone
    // never runs the pipeline.
    public bool HasEarlierTerminator =>
        Trailing.Length > 1 &&
        Trailing.AsSpan(0, Trailing.Length - 1).IndexOfAny(" \t\r\n-") >= 0;
}
