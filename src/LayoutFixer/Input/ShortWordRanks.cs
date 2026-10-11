using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Input;

/// <summary>
/// Frequency ranks of short words (1-4 letters) for one language, generated
/// offline by <c>tools/LayoutFixer.Tools ngrams</c>. Short words are where
/// layout mistakes are both most frequent ("yt" for "не", "ns" for "ты") and
/// most ambiguous ("vs"/"мы", "herb"/"руки"), and where n-grams carry little
/// information. Comparing how common each spelling is in its own language
/// resolves most of them.
/// </summary>
internal sealed class ShortWordRanks
{
    public const int MaxLength = 4;
    public const int Unknown = 1_000_000;

    private readonly Dictionary<string, int> _ranks = new(StringComparer.Ordinal);
    private readonly bool _russian;

    private ShortWordRanks(bool russian)
    {
        _russian = russian;
    }

    public bool IsLoaded => _ranks.Count > 0;

    public static ShortWordRanks LoadRussian() =>
        LoadEmbedded("LayoutFixer.Dictionaries.short-words-ru.txt", russian: true);

    public static ShortWordRanks LoadEnglish() =>
        LoadEmbedded("LayoutFixer.Dictionaries.short-words-en.txt", russian: false);

    public static ShortWordRanks FromLines(IEnumerable<string> lines, bool russian)
    {
        var table = new ShortWordRanks(russian);
        table.LoadLines(lines);
        return table;
    }

    /// <summary>Rank of the word (1 = most frequent) or <see cref="Unknown"/>.</summary>
    public int RankOf(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length > MaxLength)
        {
            return Unknown;
        }

        string key = _russian ? TextNormalizer.NormalizeRussian(word) : word.ToLowerInvariant();
        return _ranks.TryGetValue(key, out int rank) ? rank : Unknown;
    }

    private static ShortWordRanks LoadEmbedded(string resourceName, bool russian)
    {
        var table = new ShortWordRanks(russian);
        try
        {
            using Stream? stream = typeof(ShortWordRanks).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                Logger.Log($"ShortWordRanks: embedded resource '{resourceName}' not found");
                return table;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lines.Add(line);
            }

            table.LoadLines(lines);
        }
        catch (Exception ex)
        {
            Logger.Log($"ShortWordRanks: failed to load '{resourceName}': {ex.Message}");
        }

        return table;
    }

    private void LoadLines(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0 || !int.TryParse(line.AsSpan(tab + 1), out int rank) || rank <= 0)
            {
                continue;
            }

            string word = line.Substring(0, tab);
            string key = _russian ? TextNormalizer.NormalizeRussian(word) : word.ToLowerInvariant();
            if (!_ranks.TryGetValue(key, out int existing) || rank < existing)
            {
                _ranks[key] = rank;
            }
        }
    }
}
