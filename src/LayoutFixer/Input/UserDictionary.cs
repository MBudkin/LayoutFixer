using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LayoutFixer.Diagnostics;
using LayoutFixer.Services;

namespace LayoutFixer.Input;

internal sealed class UserDictionary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<string> _russianOrder = new();
    private readonly List<string> _englishOrder = new();
    private readonly HashSet<string> _russianSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _englishSet = new(StringComparer.OrdinalIgnoreCase);

    public UserDictionary(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>
    /// Raised after any change (add, remove, clear, import). May be raised on
    /// any thread; UI subscribers marshal to their own thread.
    /// </summary>
    public event EventHandler? Changed;

    public bool ContainsRussian(string? word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return false;
        }

        lock (_gate)
        {
            return _russianSet.Contains(KeyFor(ConvertedLanguage.Russian, word));
        }
    }

    public bool ContainsEnglish(string? word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return false;
        }

        lock (_gate)
        {
            return _englishSet.Contains(word);
        }
    }

    // Live-conversion support: returns true when any user word in the given
    // language is exactly the prefix or has it as a real prefix. This lets the
    // live trigger hold off when the typed tail could still grow into a valid
    // user word. Example: with "ueger" stored, typing "ueg" must NOT trigger an
    // EN->RU switch, because the user may well be typing "ueger" on purpose.
    // Russian keys are matched in their normalized (ё->е, lowercased) form so
    // the comparison mirrors how the words are stored.
    public bool HasEnglishPrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return false;
        }

        string key = prefix.ToLowerInvariant();
        lock (_gate)
        {
            foreach (string word in _englishOrder)
            {
                if (word.Length >= key.Length &&
                    string.Compare(word, 0, key, 0, key.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool HasRussianPrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return false;
        }

        string key = TextNormalizer.NormalizeRussian(prefix);
        lock (_gate)
        {
            foreach (string word in _russianOrder)
            {
                string normalized = TextNormalizer.NormalizeRussian(word);
                if (normalized.Length >= key.Length &&
                    normalized.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The set stores a normalized lookup key (ё->е for Russian) so that "мёд" and
    // "мед" are treated as the same entry; the order list keeps the original form
    // for display.
    private static string KeyFor(ConvertedLanguage language, string word)
    {
        return language == ConvertedLanguage.Russian
            ? TextNormalizer.NormalizeRussian(word)
            : word;
    }

    public bool Add(ConvertedLanguage language, string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return false;
        }

        word = TextNormalizer.TrimToWord(word);
        if (word.Length == 0)
        {
            return false;
        }

        bool added = AddNoSave(language, word);
        if (added)
        {
            Save();
            Logger.Log($"UserDictionary: added '{word}' to {language}");
        }

        return added;
    }

    public int Import(ConvertedLanguage language, IEnumerable<string> words)
    {
        int added = 0;
        foreach (string raw in words)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string word = TextNormalizer.TrimToWord(raw);
            if (word.Length == 0)
            {
                continue;
            }

            if (AddNoSave(language, word))
            {
                added++;
            }
        }

        if (added > 0)
        {
            Save();
            Logger.Log($"UserDictionary: imported {added} word(s) to {language}");
        }

        return added;
    }

    private bool AddNoSave(ConvertedLanguage language, string word)
    {
        lock (_gate)
        {
            HashSet<string> set = SetFor(language);
            if (!set.Add(KeyFor(language, word)))
            {
                return false;
            }

            ListFor(language).Add(word);
        }

        return true;
    }

    public bool Remove(ConvertedLanguage language, string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return false;
        }

        string key = KeyFor(language, word);
        bool removed;
        lock (_gate)
        {
            HashSet<string> set = SetFor(language);
            removed = set.Remove(key);
            if (removed)
            {
                ListFor(language).RemoveAll(w => string.Equals(KeyFor(language, w), key, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (removed)
        {
            Save();
        }

        return removed;
    }

    /// <summary>
    /// Replaces <paramref name="oldWord"/> in <paramref name="oldLanguage"/> with
    /// <paramref name="newWord"/> in <paramref name="newLanguage"/> (edit or move
    /// to the other list) with a single save. Returns false when the new word is
    /// empty or already present in the target list (the old word is kept then).
    /// </summary>
    public bool Replace(ConvertedLanguage oldLanguage, string oldWord, ConvertedLanguage newLanguage, string newWord)
    {
        newWord = TextNormalizer.TrimToWord(newWord ?? string.Empty);
        if (newWord.Length == 0 || string.IsNullOrEmpty(oldWord))
        {
            return false;
        }

        string oldKey = KeyFor(oldLanguage, oldWord);
        lock (_gate)
        {
            if (!SetFor(oldLanguage).Contains(oldKey))
            {
                return false;
            }

            string newKey = KeyFor(newLanguage, newWord);
            bool sameEntry = oldLanguage == newLanguage &&
                             SetFor(oldLanguage).Comparer.Equals(oldKey, newKey);
            if (!sameEntry && SetFor(newLanguage).Contains(newKey))
            {
                return false;
            }

            List<string> oldList = ListFor(oldLanguage);
            int index = oldList.FindIndex(w => string.Equals(KeyFor(oldLanguage, w), oldKey, StringComparison.OrdinalIgnoreCase));
            SetFor(oldLanguage).Remove(oldKey);
            if (index >= 0)
            {
                oldList.RemoveAt(index);
            }

            SetFor(newLanguage).Add(newKey);
            List<string> newList = ListFor(newLanguage);
            if (oldLanguage == newLanguage && index >= 0)
            {
                newList.Insert(Math.Min(index, newList.Count), newWord);
            }
            else
            {
                newList.Add(newWord);
            }
        }

        Save();
        Logger.Log($"UserDictionary: '{oldWord}' ({oldLanguage}) -> '{newWord}' ({newLanguage})");
        return true;
    }

    /// <summary>Removes several words with one save.</summary>
    public int RemoveRange(ConvertedLanguage language, IEnumerable<string> words)
    {
        int removed = 0;
        lock (_gate)
        {
            HashSet<string> set = SetFor(language);
            List<string> list = ListFor(language);
            foreach (string word in words)
            {
                string key = KeyFor(language, word);
                if (set.Remove(key))
                {
                    list.RemoveAll(w => string.Equals(KeyFor(language, w), key, StringComparison.OrdinalIgnoreCase));
                    removed++;
                }
            }
        }

        if (removed > 0)
        {
            Save();
        }

        return removed;
    }

    public int Clear(ConvertedLanguage language)
    {
        int count;
        lock (_gate)
        {
            count = ListFor(language).Count;
            ListFor(language).Clear();
            SetFor(language).Clear();
        }

        Save();
        return count;
    }

    public IReadOnlyList<string> GetWords(ConvertedLanguage language)
    {
        lock (_gate)
        {
            return ListFor(language).ToArray();
        }
    }

    public int Count(ConvertedLanguage language)
    {
        lock (_gate)
        {
            return ListFor(language).Count;
        }
    }

    public string ExportToJson()
    {
        List<string> russian;
        List<string> english;
        lock (_gate)
        {
            russian = new List<string>(_russianOrder);
            english = new List<string>(_englishOrder);
        }

        var data = new UserDictionaryData { Russian = russian, English = english };
        return JsonSerializer.Serialize(data, JsonOptions);
    }

    public (int russian, int english) ImportFromJson(string json)
    {
        var data = JsonSerializer.Deserialize<UserDictionaryData>(json, JsonOptions);
        if (data is null)
        {
            return (0, 0);
        }

        int ru = data.Russian is null ? 0 : Import(ConvertedLanguage.Russian, data.Russian);
        int en = data.English is null ? 0 : Import(ConvertedLanguage.English, data.English);
        return (ru, en);
    }

    private HashSet<string> SetFor(ConvertedLanguage language)
    {
        return language == ConvertedLanguage.Russian ? _russianSet : _englishSet;
    }

    private List<string> ListFor(ConvertedLanguage language)
    {
        return language == ConvertedLanguage.Russian ? _russianOrder : _englishOrder;
    }

    private void Load()
    {
        try
        {
            UserDictionaryData? data = AtomicFile.Load(
                _path,
                json => JsonSerializer.Deserialize<UserDictionaryData>(json, JsonOptions),
                out _);
            if (data is null)
            {
                return;
            }

            ImportList(_russianOrder, _russianSet, data.Russian, w => KeyFor(ConvertedLanguage.Russian, w));
            ImportList(_englishOrder, _englishSet, data.English, w => w);
            Logger.Log($"UserDictionary loaded: russian={_russianOrder.Count} english={_englishOrder.Count}");
        }
        catch (Exception ex)
        {
            Logger.Log($"UserDictionary load failed: {ex}");
        }
    }

    private static void ImportList(List<string> order, HashSet<string> set, List<string>? words, Func<string, string> keySelector)
    {
        if (words is null)
        {
            return;
        }

        foreach (string raw in words)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string word = TextNormalizer.TrimToWord(raw);
            if (word.Length == 0)
            {
                continue;
            }

            if (set.Add(keySelector(word)))
            {
                order.Add(word);
            }
        }
    }

    private void Save()
    {
        try
        {
            List<string> russian;
            List<string> english;
            lock (_gate)
            {
                russian = new List<string>(_russianOrder);
                english = new List<string>(_englishOrder);
            }

            var data = new UserDictionaryData { Russian = russian, English = english };
            string json = JsonSerializer.Serialize(data, JsonOptions);
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Logger.Log($"UserDictionary save failed: {ex}");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class UserDictionaryData
    {
        public List<string>? Russian { get; set; }

        public List<string>? English { get; set; }
    }
}
