namespace LayoutFixer.Input;

/// <summary>
/// Text normalization helpers shared across the conversion pipeline. Extracted
/// from the legacy <c>WordDictionaries</c> so they survive after the full word
/// lists were replaced by the trigger tables.
/// </summary>
internal static class TextNormalizer
{
    // Russian "ё" and "е" are the same letter for practical typing: the source
    // data may contain "мёд" but the user types "мед". Normalize both the stored
    // forms and the lookup form by collapsing ё -> е (after lowercasing).
    public static string NormalizeRussian(string word)
    {
        return word.ToLowerInvariant().Replace('ё', 'е');
    }

    // Strips leading/trailing characters that are not letters, keeping the
    // interior untouched. Used to turn a typed token that carries layout
    // punctuation (e.g. "хошь?", "break,", ".well") into the bare word
    // ("хошь", "break", "well") before it is stored in the user dictionary,
    // so a learned word never includes surrounding punctuation. Internal
    // punctuation (apostrophe in "don't") is preserved.
    public static string TrimToWord(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int start = 0;
        int end = text.Length;
        while (start < end && !char.IsLetter(text[start]))
        {
            start++;
        }

        while (end > start && !char.IsLetter(text[end - 1]))
        {
            end--;
        }

        return start == 0 && end == text.Length ? text : text.Substring(start, end - start);
    }
}
