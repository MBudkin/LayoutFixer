namespace LayoutFixer.Input;

internal readonly record struct ConversionOutcome
{
    public ConversionResult Result { get; init; }

    public string? OriginalText { get; init; }

    public string? ConvertedText { get; init; }

    public ConvertedLanguage Language { get; init; }

    public bool IsSingleWord { get; init; }

    public bool WasRevert { get; init; }

    // True when Break only removes capitalization that LayoutFixer applied at a
    // sentence boundary. This is a separate undo layer and must not be learned
    // as a layout correction.
    public bool WasCapitalizationUndo { get; init; }

    // True when a revert undoes an automatic conversion (set only on the revert
    // outcome). Lets learning treat the restored original as the correct word.
    public bool FromAuto { get; init; }

    // True when the automatic logic itself picks the same layout for this word
    // (it was only held back by context, Caps Lock, an exclusion...). Such a
    // word is already known: learning it would only clutter the dictionary.
    public bool AlreadyRecognized { get; init; }

    public static ConversionOutcome From(ConversionResult result) => new() { Result = result };
}
