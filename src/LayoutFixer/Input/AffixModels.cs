using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Input;

/// <summary>
/// Affix-based language affinity model used as a conservative fallback when the
/// exact dictionaries cannot decide a word. Built once from four embedded
/// frequency tables (Russian/English prefixes and suffixes) produced by
/// <c>tools/analyze-affixes.ps1</c>. Each affix is stored with the number of
/// source-dictionary words that contain it, so common stems/endings weigh more.
/// </summary>
internal sealed class AffixModels
{
    private const string RuPrefixResource = "LayoutFixer.Dictionaries.ru-prefixes.txt";
    private const string RuSuffixResource = "LayoutFixer.Dictionaries.ru-suffixes.txt";
    private const string EnPrefixResource = "LayoutFixer.Dictionaries.en-prefixes.txt";
    private const string EnSuffixResource = "LayoutFixer.Dictionaries.en-suffixes.txt";

    // Longest affix wins: a longer matching stem/ending is a stronger signal.
    // Suffixes are at least 3 chars on purpose — 2-char endings ("ie", "sk",
    // "re") are weak and produced false positives on short Russian words whose
    // English-layout form happened to end in them.
    private static readonly int[] PrefixLengths = { 7, 6, 5, 4, 3 };
    private static readonly int[] SuffixLengths = { 5, 4, 3 };

    private readonly HashSet<string> _ruPrefix;
    private readonly HashSet<string> _ruSuffix;
    private readonly HashSet<string> _enPrefix;
    private readonly HashSet<string> _enSuffix;

    public AffixModels()
    {
        Assembly assembly = typeof(AffixModels).Assembly;
        // Russian keys are normalized (ё -> е) so "мёд"/"мед" behave the same.
        _ruPrefix = Load(assembly, RuPrefixResource, normalizeRussian: true);
        _ruSuffix = Load(assembly, RuSuffixResource, normalizeRussian: true);
        _enPrefix = Load(assembly, EnPrefixResource, normalizeRussian: false);
        _enSuffix = Load(assembly, EnSuffixResource, normalizeRussian: false);
        Logger.Log($"AffixModels loaded: ruPre={_ruPrefix.Count} ruSuf={_ruSuffix.Count} enPre={_enPrefix.Count} enSuf={_enSuffix.Count}");
    }

    public int RussianScore(string word)
    {
        return Score(word, _ruPrefix, _ruSuffix, normalizeRussian: true);
    }

    public int EnglishScore(string word)
    {
        return Score(word, _enPrefix, _enSuffix, normalizeRussian: false);
    }

    private static int Score(string word, HashSet<string> prefixes, HashSet<string> suffixes, bool normalizeRussian)
    {
        if (string.IsNullOrEmpty(word))
        {
            return 0;
        }

        string key = normalizeRussian ? TextNormalizer.NormalizeRussian(word) : word.ToLowerInvariant();

        int prefixScore = 0;
        int suffixScore = 0;

        // Each match contributes its length, so a 5-char stem outweighs a 3-char
        // one. Raw frequencies were not used: a single very common ending (e.g.
        // "re") dominated and mis-classified valid Russian words.
        foreach (int length in PrefixLengths)
        {
            if (key.Length > length && prefixes.Contains(key.Substring(0, length)))
            {
                prefixScore = length;
                break;
            }
        }

        foreach (int length in SuffixLengths)
        {
            if (key.Length > length && suffixes.Contains(key.Substring(key.Length - length)))
            {
                suffixScore = length;
                break;
            }
        }

        // A 3-character suffix ALONE is too weak a signal: common endings like
        // "ger", "ion", "dly" light up on meaningless gibberish (e.g. "ueger"
        // scored 3 from "ger" and got misclassified as English). Require either
        // a prefix of any length, a longer (>=4) suffix, or both — but not a
        // lone 3-char suffix.
        if (prefixScore == 0 && suffixScore == 3)
        {
            return 0;
        }

        return prefixScore + suffixScore;
    }

    private static HashSet<string> Load(Assembly assembly, string resourceName, bool normalizeRussian)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            Logger.Log($"AffixModels: embedded resource '{resourceName}' not found");
            return set;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Files are "affix<TAB>frequency"; only membership matters now.
            int tab = line.IndexOf('\t');
            string affix = (tab >= 0 ? line.Substring(0, tab) : line).Trim();

            string key = normalizeRussian ? TextNormalizer.NormalizeRussian(affix) : affix.ToLowerInvariant();
            if (key.Length > 0)
            {
                set.Add(key);
            }
        }

        return set;
    }
}
