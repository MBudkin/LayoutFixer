using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LayoutFixer.Diagnostics;
using LayoutFixer.Services;

namespace LayoutFixer.Input;

internal sealed class UserReplacements
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
    private readonly List<UserReplacementEntry> _entries = new();

    public UserReplacements(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>Raised after the list changed and was saved. May be raised on any thread.</summary>
    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool HasAny
    {
        get
        {
            lock (_gate)
            {
                foreach (UserReplacementEntry entry in _entries)
                {
                    if (entry.Enabled && !string.IsNullOrEmpty(entry.Trigger))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Returns the replacement for the given token, or null if no enabled entry
    /// matches. Whole-word match; case sensitivity follows each entry.
    /// </summary>
    public string? Find(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        lock (_gate)
        {
            foreach (UserReplacementEntry entry in _entries)
            {
                if (!entry.Enabled || string.IsNullOrEmpty(entry.Trigger))
                {
                    continue;
                }

                bool match = entry.CaseSensitive
                    ? string.Equals(token, entry.Trigger, StringComparison.Ordinal)
                    : string.Equals(token, entry.Trigger, StringComparison.OrdinalIgnoreCase);

                if (match)
                {
                    return entry.Replacement;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Tries to find a replacement, optionally treating surrounding punctuation
    /// as a separator. When <paramref name="respectPunctuation"/> is true, any
    /// leading/trailing non-letter characters are stripped from the token before
    /// the lookup and returned via <paramref name="prefix"/>/<paramref name="suffix"/>,
    /// so the caller can wrap the expansion with them ("спс," -> "спасибо,").
    /// Returns the expansion (without the wrapping punctuation) or null.
    /// </summary>
    public string? Find(string token, bool respectPunctuation, out string prefix, out string suffix)
    {
        prefix = string.Empty;
        suffix = string.Empty;

        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        string core = token;
        if (respectPunctuation)
        {
            // Mirror TextNormalizer.TrimToWord, but keep the trimmed edges so
            // they can be re-applied to the expansion. Internal punctuation
            // (apostrophe in "don't") is preserved.
            int start = 0;
            int end = token.Length;
            while (start < end && !char.IsLetter(token[start]))
            {
                start++;
            }

            while (end > start && !char.IsLetter(token[end - 1]))
            {
                end--;
            }

            if (start >= end)
            {
                // Token is punctuation-only: no word to match against.
                return null;
            }

            prefix = token.Substring(0, start);
            suffix = token.Substring(end);
            core = token.Substring(start, end - start);
        }

        return Find(core);
    }

    public IReadOnlyList<UserReplacementEntry> GetAll()
    {
        lock (_gate)
        {
            // Return copies so the UI can't mutate the live list directly.
            var copies = new List<UserReplacementEntry>(_entries.Count);
            foreach (UserReplacementEntry entry in _entries)
            {
                copies.Add(entry.Clone());
            }

            return copies;
        }
    }

    public void SetAll(IEnumerable<UserReplacementEntry> entries)
    {
        lock (_gate)
        {
            _entries.Clear();
            foreach (UserReplacementEntry entry in entries)
            {
                if (IsComplete(entry))
                {
                    _entries.Add(entry.Clone());
                }
            }
        }

        Save();
    }

    public string ExportToJson()
    {
        List<UserReplacementEntry> snapshot;
        lock (_gate)
        {
            snapshot = new List<UserReplacementEntry>(_entries.Count);
            foreach (UserReplacementEntry entry in _entries)
            {
                snapshot.Add(entry.Clone());
            }
        }

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static List<UserReplacementEntry> Deserialize(string json)
    {
        return JsonSerializer.Deserialize<List<UserReplacementEntry>>(json, JsonOptions) ?? new List<UserReplacementEntry>();
    }

    // Live-conversion hold-off: returns true when any enabled trigger is exactly
    // the prefix or has it as a real prefix. This stops a live layout switch
    // from firing mid-word while the user may still be typing a replacement
    // trigger (e.g. with "пнтр" -> "продолжение..." stored, typing "пнт" must
    // NOT be live-switched to "gyn"). Comparison follows each entry's
    // CaseSensitive flag.
    public bool HasTriggerPrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return false;
        }

        lock (_gate)
        {
            foreach (UserReplacementEntry entry in _entries)
            {
                if (!entry.Enabled || string.IsNullOrEmpty(entry.Trigger))
                {
                    continue;
                }

                if (entry.Trigger.Length < prefix.Length)
                {
                    continue;
                }

                bool prefixMatch = entry.CaseSensitive
                    ? string.Compare(prefix, 0, entry.Trigger, 0, prefix.Length, StringComparison.Ordinal) == 0
                    : string.Compare(prefix, 0, entry.Trigger, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;

                if (prefixMatch)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Merges entries from a previously exported JSON. Existing pairs are kept;
    // duplicates (same trigger + case-sensitivity) are skipped. Returns the number
    // of newly added pairs.
    public int ImportFromJson(string json)
    {
        List<UserReplacementEntry> data = Deserialize(json);
        if (data.Count == 0)
        {
            return 0;
        }

        int added = 0;
        lock (_gate)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (UserReplacementEntry entry in _entries)
            {
                keys.Add(KeyOf(entry));
            }

            foreach (UserReplacementEntry entry in data)
            {
                if (!IsComplete(entry))
                {
                    continue;
                }

                if (keys.Add(KeyOf(entry)))
                {
                    _entries.Add(entry);
                    added++;
                }
            }
        }

        if (added > 0)
        {
            Save();
            Logger.Log($"UserReplacements: imported {added} entries");
        }

        return added;
    }

    private static string KeyOf(UserReplacementEntry entry)
    {
        return $"{(entry.CaseSensitive ? "1" : "0")}|{entry.Trigger}";
    }

    private static bool IsComplete(UserReplacementEntry? entry)
    {
        return entry is not null &&
               !string.IsNullOrWhiteSpace(entry.Trigger) &&
               !string.IsNullOrWhiteSpace(entry.Replacement);
    }

    private void Load()
    {
        try
        {
            List<UserReplacementEntry>? data = AtomicFile.Load(
                _path,
                json => JsonSerializer.Deserialize<List<UserReplacementEntry>>(json, JsonOptions),
                out _);
            if (data is null)
            {
                return;
            }

            lock (_gate)
            {
                _entries.Clear();
                foreach (UserReplacementEntry entry in data)
                {
                    if (IsComplete(entry))
                    {
                        _entries.Add(entry);
                    }
                }
            }

            Logger.Log($"UserReplacements loaded: {_entries.Count} entries");
        }
        catch (Exception ex)
        {
            Logger.Log($"UserReplacements load failed: {ex}");
        }
    }

    private void Save()
    {
        try
        {
            List<UserReplacementEntry> snapshot;
            lock (_gate)
            {
                snapshot = new List<UserReplacementEntry>(_entries);
            }

            string json = JsonSerializer.Serialize(snapshot, JsonOptions);
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Logger.Log($"UserReplacements save failed: {ex}");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class UserReplacementEntry
{
    public string Trigger { get; set; } = string.Empty;

    public string Replacement { get; set; } = string.Empty;

    public bool CaseSensitive { get; set; }

    public bool Enabled { get; set; } = true;

    public UserReplacementEntry Clone() => new()
    {
        Trigger = Trigger,
        Replacement = Replacement,
        CaseSensitive = CaseSensitive,
        Enabled = Enabled
    };
}
