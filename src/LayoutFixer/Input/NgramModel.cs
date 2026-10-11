using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Input;

/// <summary>
/// Character trigram language model for one language (Russian or English).
/// The embedded tables are generated offline from the word lists by
/// <c>tools/LayoutFixer.Tools ngrams</c>: every word is padded as
/// <c>^word$</c> and all 1-, 2- and 3-grams are counted. At runtime the model
/// only answers "how plausible is this letter sequence as a word of my
/// language"; it never stores or needs the word lists themselves.
/// </summary>
/// <remarks>
/// This is the generalising layer behind the exact trigger tables: a word
/// that is in no table (an inflected form, a rare word, slang) is still
/// recognised when its wrong-layout form is implausible in the current
/// language while its converted form reads like a normal word of the other
/// language — the classic "impossible letter combination" idea of layout
/// switchers, but scored instead of hand-listed.
/// Probabilities use interpolated Witten-Bell smoothing, so an unseen trigram
/// is unlikely but never impossible, which keeps the scores comparable.
/// </remarks>
internal sealed class NgramModel
{
    private const char Start = '^';
    private const char End = '$';

    // Symbol index 0 is the start marker, 1 the end marker, then the alphabet.
    private readonly Dictionary<char, int> _index = new();
    private readonly int _symbols;
    private readonly bool _russian;

    private readonly int[] _unigram;
    private int _unigramTotal;
    private readonly Dictionary<int, int> _bigram = new();
    private readonly Dictionary<int, int> _trigram = new();

    // Witten-Bell history statistics: total count of continuations and the
    // number of distinct continuation symbols.
    private readonly int[] _bigramHistoryCount;
    private readonly int[] _bigramHistoryTypes;
    private readonly Dictionary<int, long> _trigramHistory = new();

    // Typical average score of real words by length (index = length, capped),
    // for complete words and for prefixes. Languages differ in alphabet size
    // and spelling regularity, so raw scores are not comparable across
    // models; the engine compares scores relative to these baselines.
    private const int BaselineMaxLength = 9;
    private readonly double[] _completeBaseline = new double[BaselineMaxLength + 1];
    private readonly double[] _prefixBaseline = new double[BaselineMaxLength + 1];

    private NgramModel(string alphabet, bool russian)
    {
        _russian = russian;
        _index[Start] = 0;
        _index[End] = 1;
        foreach (char character in alphabet)
        {
            _index.TryAdd(character, _index.Count);
        }

        _symbols = _index.Count;
        _unigram = new int[_symbols];
        _bigramHistoryCount = new int[_symbols];
        _bigramHistoryTypes = new int[_symbols];
    }

    public const string RussianAlphabet = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
    public const string EnglishAlphabet = "abcdefghijklmnopqrstuvwxyz'";

    public bool IsLoaded => _unigramTotal > 0;

    public static NgramModel LoadRussian() =>
        LoadEmbedded("LayoutFixer.Dictionaries.ngrams-ru.txt", RussianAlphabet, russian: true);

    public static NgramModel LoadEnglish() =>
        LoadEmbedded("LayoutFixer.Dictionaries.ngrams-en.txt", EnglishAlphabet, russian: false);

    /// <summary>Builds a model from raw "ngram&lt;TAB&gt;count" lines (used by the tools/tests).</summary>
    public static NgramModel FromLines(IEnumerable<string> lines, string alphabet, bool russian)
    {
        var model = new NgramModel(alphabet, russian);
        model.LoadLines(lines);
        return model;
    }

    private static NgramModel LoadEmbedded(string resourceName, string alphabet, bool russian)
    {
        var model = new NgramModel(alphabet, russian);
        try
        {
            using Stream? stream = typeof(NgramModel).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                Logger.Log($"NgramModel: embedded resource '{resourceName}' not found");
                return model;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            model.LoadLines(ReadLines(reader));
        }
        catch (Exception ex)
        {
            Logger.Log($"NgramModel: failed to load '{resourceName}': {ex.Message}");
        }

        return model;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    private void LoadLines(IEnumerable<string> lines)
    {
        int unigramTotal = 0;
        foreach (string line in lines)
        {
            if (line.StartsWith("#baseline", StringComparison.Ordinal))
            {
                LoadBaseline(line);
                continue;
            }

            int tab = line.IndexOf('\t');
            if (tab <= 0 || tab > 3 ||
                !int.TryParse(line.AsSpan(tab + 1), out int count) || count <= 0)
            {
                continue;
            }

            switch (tab)
            {
                case 1 when TryIndex(line[0], out int a):
                    _unigram[a] += count;
                    unigramTotal += count;
                    break;
                case 2 when TryIndex(line[0], out int a) && TryIndex(line[1], out int b):
                    _bigram[Key(a, b)] = count;
                    _bigramHistoryCount[a] += count;
                    _bigramHistoryTypes[a]++;
                    break;
                case 3 when TryIndex(line[0], out int a) && TryIndex(line[1], out int b) && TryIndex(line[2], out int c):
                    _trigram[Key(a, b, c)] = count;
                    int history = Key(a, b);
                    _trigramHistory.TryGetValue(history, out long packed);
                    // High 32 bits: continuation count, low 32 bits: distinct types.
                    long total = (packed >> 32) + count;
                    long types = (packed & 0xFFFFFFFF) + 1;
                    _trigramHistory[history] = (total << 32) | types;
                    break;
            }
        }

        _unigramTotal = unigramTotal;
    }

    // "#baseline<TAB>complete|prefix<TAB>length<TAB>value"
    private void LoadBaseline(string line)
    {
        string[] parts = line.Split('\t');
        if (parts.Length != 4 ||
            !int.TryParse(parts[2], out int length) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            length < 1 || length > BaselineMaxLength)
        {
            return;
        }

        double[] target = parts[1] == "prefix" ? _prefixBaseline : _completeBaseline;
        target[length] = value;
    }

    /// <summary>Typical score of a real word (or prefix) of this length.</summary>
    public double Baseline(int length, bool complete)
    {
        double[] table = complete ? _completeBaseline : _prefixBaseline;
        int index = Math.Clamp(length, 1, BaselineMaxLength);
        // Fall back to the nearest known length, then to a neutral value.
        for (int distance = 0; distance <= BaselineMaxLength; distance++)
        {
            foreach (int candidate in new[] { index - distance, index + distance })
            {
                if (candidate >= 1 && candidate <= BaselineMaxLength && table[candidate] != 0)
                {
                    return table[candidate];
                }
            }
        }

        return -2.5;
    }

    /// <summary>
    /// Normalises a word core for scoring: lower case, Russian "ё" folded to
    /// "е", typographic apostrophes folded to "'". Returns null when the text
    /// contains a character outside this model's alphabet.
    /// </summary>
    public string? Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var builder = new StringBuilder(text.Length);
        foreach (char raw in text)
        {
            char character = char.ToLowerInvariant(raw);
            if (_russian && character == 'ё')
            {
                character = 'е';
            }
            else if (!_russian && character is '’' or 'ʼ')
            {
                character = '\'';
            }

            if (character is Start or End || !_index.ContainsKey(character))
            {
                return null;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Average natural-log probability per transition of <paramref name="word"/>
    /// (already normalised). With <paramref name="complete"/> the end-of-word
    /// transition is included; live (prefix) checks pass false. Also reports
    /// the number of trigrams never observed in the training words and the
    /// lowest observed count among the scored trigrams.
    /// </summary>
    public NgramScore Score(string word, bool complete)
    {
        if (!IsLoaded || string.IsNullOrEmpty(word))
        {
            return NgramScore.Invalid;
        }

        double total = 0;
        int transitions = 0;
        int unseen = 0;
        int previous2 = -1;
        int previous1 = 0; // start marker

        int length = word.Length + (complete ? 1 : 0);
        for (int position = 0; position < length; position++)
        {
            int current;
            if (position < word.Length)
            {
                if (!TryIndex(word[position], out current) || current < 2)
                {
                    return NgramScore.Invalid;
                }
            }
            else
            {
                current = 1; // end marker
            }

            double probability = previous2 < 0
                ? BigramProbability(previous1, current)
                : TrigramProbability(previous2, previous1, current);
            if (previous2 >= 0 && !_trigram.ContainsKey(Key(previous2, previous1, current)))
            {
                unseen++;
            }

            total += Math.Log(probability);
            transitions++;
            previous2 = previous1;
            previous1 = current;
        }

        double average = total / transitions;
        return new NgramScore(true, average, unseen, transitions, average - Baseline(word.Length, complete));
    }

    private double UnigramProbability(int symbol)
    {
        // Add-one over every predictable symbol (letters and the end marker).
        return (_unigram[symbol] + 1.0) / (_unigramTotal + (_symbols - 1));
    }

    private double BigramProbability(int history, int symbol)
    {
        double lower = UnigramProbability(symbol);
        int historyCount = _bigramHistoryCount[history];
        if (historyCount == 0)
        {
            return lower;
        }

        int types = _bigramHistoryTypes[history];
        _bigram.TryGetValue(Key(history, symbol), out int count);
        return (count + types * lower) / (historyCount + types);
    }

    private double TrigramProbability(int history2, int history1, int symbol)
    {
        double lower = BigramProbability(history1, symbol);
        if (!_trigramHistory.TryGetValue(Key(history2, history1), out long packed))
        {
            return lower;
        }

        long historyCount = packed >> 32;
        long types = packed & 0xFFFFFFFF;
        _trigram.TryGetValue(Key(history2, history1, symbol), out int count);
        return (count + types * lower) / (historyCount + types);
    }

    private bool TryIndex(char character, out int index) => _index.TryGetValue(character, out index);

    private int Key(int a, int b) => a * _symbols + b;

    private int Key(int a, int b, int c) => (a * _symbols + b) * _symbols + c;
}

// Relative = AverageLogProbability minus the language's typical score for a
// word of this length: 0 is "a normal word", negative is "unusual".
internal readonly record struct NgramScore(bool IsValid, double AverageLogProbability, int UnseenTrigrams, int Transitions, double Relative)
{
    public static NgramScore Invalid => new(false, double.NegativeInfinity, 0, 0, double.NegativeInfinity);
}
