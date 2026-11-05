using LayoutFixer.Input;
using LayoutFixer.Settings;
using Xunit;

namespace LayoutFixer.Tests;

public sealed class SingleLetterTests
{
    public static TheoryData<string, string> RussianLetters => new()
    {
        { "d", "в" },
        { "f", "а" },
        { "z", "я" },
        { "b", "и" },
        { "c", "с" },
        { "e", "у" },
        { "j", "о" },
        { "r", "к" }
    };

    [Theory]
    [MemberData(nameof(RussianLetters))]
    public void EveryRussianSingleLetterIsFixedAtSentenceStart(string typed, string expected)
    {
        using var engine = new TestEngine();
        foreach (bool alwaysConvert in new[] { false, true })
        {
            engine.Engine.AlwaysConvertSingleLetters = () => alwaysConvert;
            Assert.Equal(expected, engine.TypeVisible(typed, ConvertedLanguage.English));
            Assert.NotEqual(LayoutDecisionEngine.TermLetterSource, engine.Engine.LastFinalSource);
            Assert.Equal(expected, engine.TypeVisible(expected, ConvertedLanguage.Russian));
        }
    }

    [Theory]
    [MemberData(nameof(RussianLetters))]
    public void DefaultSettingFixesEverySingleLetterAfterEnglishWord(string typed, string expected)
    {
        using var engine = new TestEngine();
        var settings = new AppSettings();
        engine.Engine.AlwaysConvertSingleLetters = () => settings.AlwaysConvertSingleLetters;

        Assert.Equal(expected, engine.TypeVisible(typed, ConvertedLanguage.English, "hello"));
        // ConversionService uses this decision to keep English for the next term.
        Assert.Equal(LayoutDecisionEngine.TermLetterSource, engine.Engine.LastFinalSource);

        // Changes to the setting take effect on the next word.
        settings.AlwaysConvertSingleLetters = false;
        Assert.Equal(typed, engine.TypeVisible(typed, ConvertedLanguage.English, "hello"));
    }

    [Theory]
    [MemberData(nameof(RussianLetters))]
    public void EverySingleLetterBetweenEnglishTermsIsFixedInBothModes(string typed, string expected)
    {
        using var engine = new TestEngine();
        foreach (bool alwaysConvert in new[] { false, true })
        {
            engine.Engine.AlwaysConvertSingleLetters = () => alwaysConvert;
            foreach (string previous in new[] { "Claude", "API", "ChatGPT" })
            {
                Assert.Equal(expected, engine.TypeVisible(typed, ConvertedLanguage.English, previous));
                Assert.Equal(LayoutDecisionEngine.TermLetterSource, engine.Engine.LastFinalSource);
            }

            Assert.True(engine.Engine.TryFinalDecision(typed, "docker", "Сравни docker ", out ConvertedLanguage target));
            Assert.Equal(ConvertedLanguage.Russian, target);
            Assert.Equal(expected, engine.Converter.Convert(typed, ConvertedLanguage.English));
            Assert.Equal(LayoutDecisionEngine.TermLetterSource, engine.Engine.LastFinalSource);
        }
    }

    [Theory]
    [MemberData(nameof(RussianLetters))]
    public void EverySingleLetterAfterRussianWordSwitchesToRussian(string typed, string expected)
    {
        using var engine = new TestEngine();
        foreach (bool alwaysConvert in new[] { false, true })
        {
            engine.Engine.AlwaysConvertSingleLetters = () => alwaysConvert;
            Assert.Equal(expected, engine.TypeVisible(typed, ConvertedLanguage.English, "привет"));
            Assert.NotEqual(LayoutDecisionEngine.TermLetterSource, engine.Engine.LastFinalSource);
        }
    }

    [Theory]
    [MemberData(nameof(RussianLetters))]
    public void SingleLetterLabelsAndUserDictionaryRemainAuthoritative(string typed, string expected)
    {
        using var engine = new TestEngine();
        engine.Engine.AlwaysConvertSingleLetters = () => true;
        string label = typed.ToUpperInvariant();
        Assert.Equal(label, engine.TypeVisible(label, ConvertedLanguage.English, "Plan"));

        engine.UserDictionary.Add(ConvertedLanguage.English, typed);
        Assert.Equal(typed, engine.TypeVisible(typed, ConvertedLanguage.English, "Claude"));
        Assert.Equal(typed, engine.TypeVisible(typed, ConvertedLanguage.English));
        Assert.False(engine.Engine.TryConvertPrecedingLetter(typed, ConvertedLanguage.Russian, out _));
        Assert.Equal(expected, engine.Converter.Convert(typed, ConvertedLanguage.English));
    }
}
