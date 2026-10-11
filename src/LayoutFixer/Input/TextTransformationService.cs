using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LayoutFixer.Input;

internal enum TextTransformationKind
{
    ToggleCase,
    Transliterate
}

internal static class TextTransformationService
{
    private static readonly IReadOnlyDictionary<char, string> RussianTransliteration =
        new Dictionary<char, string>
        {
            ['а'] = "a",
            ['б'] = "b",
            ['в'] = "v",
            ['г'] = "g",
            ['д'] = "d",
            ['е'] = "e",
            ['ё'] = "yo",
            ['ж'] = "zh",
            ['з'] = "z",
            ['и'] = "i",
            ['й'] = "y",
            ['к'] = "k",
            ['л'] = "l",
            ['м'] = "m",
            ['н'] = "n",
            ['о'] = "o",
            ['п'] = "p",
            ['р'] = "r",
            ['с'] = "s",
            ['т'] = "t",
            ['у'] = "u",
            ['ф'] = "f",
            ['х'] = "kh",
            ['ц'] = "ts",
            ['ч'] = "ch",
            ['ш'] = "sh",
            ['щ'] = "shch",
            ['ъ'] = string.Empty,
            ['ы'] = "y",
            ['ь'] = string.Empty,
            ['э'] = "e",
            ['ю'] = "yu",
            ['я'] = "ya"
        };

    private static readonly (string Latin, string Russian)[] LatinSequences =
    [
        ("shch", "щ"),
        ("yo", "ё"),
        ("zh", "ж"),
        ("kh", "х"),
        ("ts", "ц"),
        ("ch", "ч"),
        ("sh", "ш"),
        ("yu", "ю"),
        ("ya", "я")
    ];

    private static readonly IReadOnlyDictionary<char, string> LatinTransliteration =
        new Dictionary<char, string>
        {
            ['a'] = "а",
            ['b'] = "б",
            ['v'] = "в",
            ['g'] = "г",
            ['d'] = "д",
            ['e'] = "е",
            ['z'] = "з",
            ['i'] = "и",
            ['k'] = "к",
            ['l'] = "л",
            ['m'] = "м",
            ['n'] = "н",
            ['o'] = "о",
            ['p'] = "п",
            ['r'] = "р",
            ['s'] = "с",
            ['t'] = "т",
            ['u'] = "у",
            ['f'] = "ф",
            // Common alternatives that do not occur as standalone characters
            // in LayoutFixer's RU -> Latin output, but are useful for manually
            // typed transliteration.
            ['h'] = "х",
            ['j'] = "й",
            ['q'] = "к",
            ['w'] = "в",
            ['x'] = "кс"
        };

    public static string Transform(
        string text,
        TextTransformationKind kind,
        bool invertEachCharacterCase = false)
    {
        return kind switch
        {
            TextTransformationKind.ToggleCase => ToggleCase(text, invertEachCharacterCase),
            TextTransformationKind.Transliterate => Transliterate(text),
            _ => text
        };
    }

    public static string CapitalizeFirstLetter(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        for (int index = 0; index < text.Length; index++)
        {
            if (!char.IsLetter(text[index]))
            {
                continue;
            }

            char upper = char.ToUpperInvariant(text[index]);
            if (upper == text[index])
            {
                return text;
            }

            var result = text.ToCharArray();
            result[index] = upper;
            return new string(result);
        }

        return text;
    }

    public static string ToggleCase(string text, bool invertEachCharacter = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (invertEachCharacter)
        {
            var inverted = new char[text.Length];
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                inverted[index] = char.IsUpper(character)
                    ? char.ToLowerInvariant(character)
                    : (char.IsLower(character)
                        ? char.ToUpperInvariant(character)
                        : character);
            }

            return new string(inverted);
        }

        bool hasCasedLetter = false;
        bool allUpper = true;
        foreach (char character in text)
        {
            if (!char.IsLetter(character))
            {
                continue;
            }

            hasCasedLetter = true;
            if (char.IsLower(character))
            {
                allUpper = false;
            }
        }

        if (!hasCasedLetter)
        {
            return text;
        }

        var result = new char[text.Length];
        for (int index = 0; index < text.Length; index++)
        {
            result[index] = allUpper
                ? char.ToLowerInvariant(text[index])
                : char.ToUpperInvariant(text[index]);
        }

        return new string(result);
    }

    // "ПРивет" -> "Привет", "THis" -> "This": the Shift key released one
    // letter too late. Only plain words of at least four letters in a single
    // alphabet qualify: two capitals followed by lowercase letters only, so
    // "IDs", "GHz", "PhD", "iPhone", "MacOS" and abbreviations stay intact.
    public static string FixTwoInitialCapitals(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
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

        if (end - start < 4 ||
            !char.IsUpper(text[start]) ||
            !char.IsUpper(text[start + 1]))
        {
            return text;
        }

        bool cyrillic = IsCyrillic(text[start]);
        for (int index = start; index < end; index++)
        {
            char character = text[index];
            if (!char.IsLetter(character) || IsCyrillic(character) != cyrillic)
            {
                return text;
            }

            if (index >= start + 2 && !char.IsLower(character))
            {
                return text;
            }
        }

        var result = text.ToCharArray();
        result[start + 1] = char.ToLowerInvariant(result[start + 1]);
        return new string(result);
    }

    private static bool IsCyrillic(char character) => character is >= 'Ѐ' and <= 'ӿ';

    public static string NormalizeAccidentalCapsLock(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        int firstLetter = -1;
        int letterCount = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (!char.IsLetter(text[index]))
            {
                continue;
            }

            if (firstLetter < 0)
            {
                firstLetter = index;
                if (!char.IsLower(text[index]))
                {
                    return text;
                }
            }
            else if (!char.IsUpper(text[index]))
            {
                return text;
            }

            letterCount++;
        }

        if (firstLetter < 0 || letterCount < 2)
        {
            return text;
        }

        var result = text.ToCharArray();
        result[firstLetter] = char.ToUpperInvariant(result[firstLetter]);
        for (int index = firstLetter + 1; index < result.Length; index++)
        {
            if (char.IsLetter(result[index]))
            {
                result[index] = char.ToLowerInvariant(result[index]);
            }
        }

        return new string(result);
    }

    public static string Transliterate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        int russianLetters = 0;
        int latinLetters = 0;
        foreach (char character in text)
        {
            if (IsRussianLetter(character))
            {
                russianLetters++;
            }
            else if (IsLatinLetter(character))
            {
                latinLetters++;
            }
        }

        if (russianLetters == 0 && latinLetters == 0)
        {
            return text;
        }

        if (russianLetters > 0 && latinLetters > 0)
        {
            return text;
        }

        return russianLetters > 0
            ? TransliterateRussianToLatin(text)
            : TransliterateLatinToRussian(text);
    }

    private static string TransliterateRussianToLatin(string text)
    {
        bool changed = false;
        var result = new StringBuilder(text.Length * 2);
        int index = 0;
        while (index < text.Length)
        {
            if (!IsRussianLetter(text[index]))
            {
                result.Append(text[index]);
                index++;
                continue;
            }

            int wordEnd = index;
            bool allUpper = true;
            while (wordEnd < text.Length && IsRussianLetter(text[wordEnd]))
            {
                if (char.IsLower(text[wordEnd]))
                {
                    allUpper = false;
                }

                wordEnd++;
            }

            for (; index < wordEnd; index++)
            {
                char source = text[index];
                string mapped = RussianTransliteration[char.ToLowerInvariant(source)];
                if (char.IsUpper(source))
                {
                    mapped = allUpper ? mapped.ToUpperInvariant() : CapitalizeAscii(mapped);
                }

                result.Append(mapped);
                changed = true;
            }
        }

        return changed ? result.ToString() : text;
    }

    private static string TransliterateLatinToRussian(string text)
    {
        bool changed = false;
        var result = new StringBuilder(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            if (!IsLatinLetter(text[index]))
            {
                result.Append(text[index]);
                index++;
                continue;
            }

            int wordEnd = index;
            while (wordEnd < text.Length && IsLatinLetter(text[wordEnd]))
            {
                wordEnd++;
            }

            string sourceWord = text.Substring(index, wordEnd - index);
            string russianWord = TransliterateLatinWord(sourceWord);
            result.Append(ApplyWordCase(sourceWord, russianWord));
            changed |= !string.Equals(sourceWord, russianWord, StringComparison.Ordinal);
            index = wordEnd;
        }

        return changed ? result.ToString() : text;
    }

    private static string TransliterateLatinWord(string sourceWord)
    {
        string word = sourceWord.ToLowerInvariant();
        var result = new StringBuilder(word.Length);
        int index = 0;
        while (index < word.Length)
        {
            bool matched = false;
            foreach ((string latin, string russian) in LatinSequences)
            {
                if (!word.AsSpan(index).StartsWith(latin, StringComparison.Ordinal))
                {
                    continue;
                }

                result.Append(russian);
                index += latin.Length;
                matched = true;
                break;
            }

            if (matched)
            {
                continue;
            }

            char character = word[index];
            if (character == 'y')
            {
                result.Append(ResolveSingleY(result));
            }
            else if (LatinTransliteration.TryGetValue(character, out string? russian))
            {
                result.Append(russian);
            }
            else
            {
                result.Append(character);
            }

            index++;
        }

        return result.ToString();
    }

    private static char ResolveSingleY(StringBuilder russianPrefix)
    {
        if (russianPrefix.Length == 0)
        {
            return 'й';
        }

        char previous = char.ToLowerInvariant(russianPrefix[^1]);
        return IsRussianVowel(previous) ? 'й' : 'ы';
    }

    private static string ApplyWordCase(string sourceWord, string russianWord)
    {
        bool allUpper = true;
        foreach (char character in sourceWord)
        {
            allUpper &= char.IsUpper(character);
        }

        if (allUpper)
        {
            return russianWord.ToUpperInvariant();
        }

        bool titleCase = char.IsUpper(sourceWord[0]) &&
                         sourceWord.AsSpan(1).ToString().All(char.IsLower);
        if (titleCase)
        {
            return CapitalizeFirstLetter(russianWord);
        }

        return russianWord;
    }

    private static bool IsRussianLetter(char character)
    {
        return RussianTransliteration.ContainsKey(char.ToLowerInvariant(character));
    }

    private static bool IsLatinLetter(char character)
    {
        char lower = char.ToLowerInvariant(character);
        return lower is >= 'a' and <= 'z';
    }

    private static bool IsRussianVowel(char character)
    {
        return character is 'а' or 'е' or 'ё' or 'и' or 'о'
            or 'у' or 'ы' or 'э' or 'ю' or 'я';
    }

    private static string CapitalizeAscii(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
