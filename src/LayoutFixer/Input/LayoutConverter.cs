using System.Collections.Generic;

namespace LayoutFixer.Input;

internal enum ConvertedLanguage
{
    Unknown,
    English,
    Russian
}

internal sealed class LayoutConverter
{
    private static readonly IReadOnlyDictionary<char, char> EnglishToRussian = BuildEnglishToRussian();
    private static readonly IReadOnlyDictionary<char, char> RussianToEnglish = ReverseMap(EnglishToRussian);

    public string Convert(string text)
    {
        return Convert(text, ConvertedLanguage.Unknown);
    }

    // Converts with an explicit source language. This matters for text without
    // letters (single punctuation/sign tokens), where DetectLanguage cannot
    // decide a direction and would otherwise fall back to EN->RU. Callers that
    // know the real input layout (e.g. from the current system layout) pass it
    // here; ConvertedLanguage.Unknown keeps the historical DetectLanguage path.
    public string Convert(string text, ConvertedLanguage sourceLanguage)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        ConvertedLanguage language = sourceLanguage != ConvertedLanguage.Unknown
            ? sourceLanguage
            : DetectLanguage(text);
        IReadOnlyDictionary<char, char> map = language == ConvertedLanguage.Russian
            ? RussianToEnglish
            : EnglishToRussian;

        var result = new char[text.Length];
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            result[index] = map.TryGetValue(current, out char mapped) ? mapped : current;
        }

        return new string(result);
    }

    public ConvertedLanguage DetectLanguage(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return ConvertedLanguage.Unknown;
        }

        int englishHits = 0;
        int russianHits = 0;

        foreach (char character in text)
        {
            // Only letters decide the language; punctuation/digits are ignored.
            // Otherwise dotted abbreviations like "т.е." (two dots map to 'ю')
            // would be mis-detected as English and learned into the wrong
            // dictionary.
            if (!char.IsLetter(character))
            {
                continue;
            }

            if (EnglishToRussian.TryGetValue(character, out char russian) && russian != character)
            {
                englishHits++;
            }
            else if (RussianToEnglish.TryGetValue(character, out char english) && english != character)
            {
                russianHits++;
            }
        }

        if (englishHits == 0 && russianHits == 0)
        {
            return ConvertedLanguage.Unknown;
        }

        return russianHits > englishHits ? ConvertedLanguage.Russian : ConvertedLanguage.English;
    }

    public bool CanConvert(string text)
    {
        return CanConvert(text, ConvertedLanguage.Unknown);
    }

    public bool CanConvert(string text, ConvertedLanguage sourceLanguage)
    {
        return !string.IsNullOrWhiteSpace(text) &&
               !string.Equals(text, Convert(text, sourceLanguage), System.StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<char, char> BuildEnglishToRussian()
    {
        var map = new Dictionary<char, char>();

        // Backtick / ё row
        map['`'] = 'ё';
        map['~'] = 'Ё';

        // Shifted digit row (unshifted digits are shared between layouts and stay as-is)
        map['@'] = '"';
        map['#'] = '№';
        map['$'] = ';';
        map['^'] = ':';
        map['&'] = '?';

        // Letter row 1 (Q П)
        AddLetterPair(map, 'q', 'й');
        AddLetterPair(map, 'w', 'ц');
        AddLetterPair(map, 'e', 'у');
        AddLetterPair(map, 'r', 'к');
        AddLetterPair(map, 't', 'е');
        AddLetterPair(map, 'y', 'н');
        AddLetterPair(map, 'u', 'г');
        AddLetterPair(map, 'i', 'ш');
        AddLetterPair(map, 'o', 'щ');
        AddLetterPair(map, 'p', 'з');
        AddLetterPair(map, '[', 'х');
        AddLetterPair(map, ']', 'ъ');

        // Letter row 2 (A Ф)
        AddLetterPair(map, 'a', 'ф');
        AddLetterPair(map, 's', 'ы');
        AddLetterPair(map, 'd', 'в');
        AddLetterPair(map, 'f', 'а');
        AddLetterPair(map, 'g', 'п');
        AddLetterPair(map, 'h', 'р');
        AddLetterPair(map, 'j', 'о');
        AddLetterPair(map, 'k', 'л');
        AddLetterPair(map, 'l', 'д');
        AddLetterPair(map, ';', 'ж');
        AddLetterPair(map, '\'', 'э');

        // Letter row 3 (Z Я)
        AddLetterPair(map, 'z', 'я');
        AddLetterPair(map, 'x', 'ч');
        AddLetterPair(map, 'c', 'с');
        AddLetterPair(map, 'v', 'м');
        AddLetterPair(map, 'b', 'и');
        AddLetterPair(map, 'n', 'т');
        AddLetterPair(map, 'm', 'ь');
        AddLetterPair(map, ',', 'б');
        AddLetterPair(map, '.', 'ю');
        AddLetterPair(map, '/', '.');

        // Shifted letter-row symbols (braces, colons, quotes etc.)
        map['{'] = 'Х';
        map['}'] = 'Ъ';
        map[':'] = 'Ж';
        map['"'] = 'Э';
        map['<'] = 'Б';
        map['>'] = 'Ю';
        map['?'] = ',';

        // Backslash / pipe (extra ISO key behavior)
        map['|'] = '/';

        return map;
    }

    private static void AddLetterPair(Dictionary<char, char> map, char english, char russian)
    {
        map[english] = russian;

        // Only add the uppercase form for actual letters. For symbol keys
        // ([ ] ; ' , . / etc.) ToUpperInvariant returns the same character, so the
        // second assignment would otherwise overwrite the lowercase mapping with the
        // capital Russian letter (e.g. '[' -> 'Х' instead of 'х'). Their shifted
        // variants ({ } : " < > ?) are added separately in BuildEnglishToRussian.
        char englishUpper = char.ToUpperInvariant(english);
        if (englishUpper != english)
        {
            map[englishUpper] = char.ToUpperInvariant(russian);
        }
    }

    private static IReadOnlyDictionary<char, char> ReverseMap(IReadOnlyDictionary<char, char> source)
    {
        var reversed = new Dictionary<char, char>();
        foreach (var pair in source)
        {
            if (pair.Key == pair.Value)
            {
                continue;
            }

            // First write wins so ambiguous characters such as digits/letters keep the most
            // meaningful mapping; BuildEnglishToRussian orders unambiguous entries first.
            reversed.TryAdd(pair.Value, pair.Key);
        }

        return reversed;
    }
}
