using System;
using System.IO;
using System.Text;
using LayoutFixer.Input;

namespace LayoutFixer.Tests;

/// <summary>
/// Builds a <see cref="LayoutDecisionEngine"/> over the embedded tables (the
/// same ones the app ships) with an empty user dictionary/replacement list in
/// a private temp folder, and simulates typing key by key.
/// </summary>
internal sealed class TestEngine : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "LayoutFixer.Tests." + Guid.NewGuid().ToString("N"));

    public TestEngine()
    {
        Directory.CreateDirectory(_folder);
        UserDictionary = new UserDictionary(Path.Combine(_folder, "user-dictionary.json"));
        Replacements = new UserReplacements(Path.Combine(_folder, "user-replacements.json"));
        Engine = new LayoutDecisionEngine(
            Shared.Triggers,
            UserDictionary,
            Shared.Affixes,
            Replacements,
            Shared.RussianModel,
            Shared.EnglishModel,
            Shared.RussianShort,
            Shared.EnglishShort);
    }

    public LayoutDecisionEngine Engine { get; }

    public UserDictionary UserDictionary { get; }

    public UserReplacements Replacements { get; }

    public LayoutConverter Converter => Engine.Converter;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Wrong-layout spelling of a word (what the user sees when the other layout is active).</summary>
    public string WrongLayout(string word) => Converter.Convert(word, Converter.DetectLanguage(word));

    /// <summary>
    /// Types <paramref name="word"/> (written as intended) key by key with
    /// <paramref name="startLayout"/> active: a live decision after every key,
    /// the final decision at the separator. Returns the resulting screen text.
    /// </summary>
    public string Type(string word, ConvertedLanguage startLayout, string? previousWord = null)
    {
        ConvertedLanguage language = Converter.DetectLanguage(word);
        ConvertedLanguage layout = startLayout;
        var screen = new StringBuilder();
        bool liveFired = false;

        foreach (char intended in word)
        {
            string key = intended.ToString();
            bool isLetterKey = char.IsLetter(intended) || Converter.CanConvert(key, language);
            screen.Append(layout == language || !isLetterKey ? key : Converter.Convert(key, language));

            if (liveFired)
            {
                continue;
            }

            string token = screen.ToString();
            if (!ConversionService.ContainsLetter(token) || ConversionService.ContainsDigit(token))
            {
                continue;
            }

            LiveDecision decision = Engine.DecideLive(token, layout);
            if (decision.Hit)
            {
                screen.Clear().Append(decision.Converted);
                layout = decision.Target;
                liveFired = true;
            }
        }

        string text = screen.ToString();
        if (!liveFired && Engine.TryFinalDecision(text, previousWord, out ConvertedLanguage target))
        {
            text = Converter.Convert(text, LayoutDecisionEngine.Oppose(target));
        }

        return text;
    }

    /// <summary>Types the exact visible keys <paramref name="visible"/> with <paramref name="layout"/> active.</summary>
    public string TypeVisible(string visible, ConvertedLanguage layout, string? previousWord = null)
    {
        var screen = new StringBuilder();
        bool liveFired = false;
        ConvertedLanguage current = layout;
        foreach (char key in visible)
        {
            screen.Append(liveFired && current != layout ? Converter.Convert(key.ToString(), layout) : key.ToString());
            if (liveFired)
            {
                continue;
            }

            string token = screen.ToString();
            if (!ConversionService.ContainsLetter(token) || ConversionService.ContainsDigit(token))
            {
                continue;
            }

            LiveDecision decision = Engine.DecideLive(token, current);
            if (decision.Hit)
            {
                screen.Clear().Append(decision.Converted);
                current = decision.Target;
                liveFired = true;
            }
        }

        string text = screen.ToString();
        if (!liveFired && Engine.TryFinalDecision(text, previousWord, out ConvertedLanguage target))
        {
            text = Converter.Convert(text, LayoutDecisionEngine.Oppose(target));
        }

        return text;
    }

    // Embedded tables are immutable after load; share them across tests.
    private static class Shared
    {
        public static readonly TriggerDictionaries Triggers = new();
        public static readonly AffixModels Affixes = new();
        public static readonly NgramModel RussianModel = NgramModel.LoadRussian();
        public static readonly NgramModel EnglishModel = NgramModel.LoadEnglish();
        public static readonly ShortWordRanks RussianShort = ShortWordRanks.LoadRussian();
        public static readonly ShortWordRanks EnglishShort = ShortWordRanks.LoadEnglish();
    }
}
