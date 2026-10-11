using LayoutFixer.Input;
using Xunit;

namespace LayoutFixer.Tests;

public sealed class TablesTests
{
    [Fact]
    public void RussianTriggerLookupDoesNotFoldEntriesWithYo()
    {
        var triggers = new TriggerDictionaries();
        // "шё" (EN keys "i`") is a live trigger; "ше" (EN "it") must not match it.
        Assert.True(triggers.ContainsLiveRu("шё"));
        Assert.False(triggers.ContainsLiveRu("ше"));
    }

    [Fact]
    public void RussianTriggerLookupAcceptsYoQueryForYeEntry()
    {
        var triggers = new TriggerDictionaries();
        // A query typed with "ё" may still match the plain "е" entry.
        Assert.True(triggers.ContainsFinalRu("мед"));
        Assert.True(triggers.ContainsFinalRu("мёд"));
    }

    [Fact]
    public void SystemAbbreviationsAreDropped()
    {
        var triggers = new TriggerDictionaries();
        Assert.False(triggers.ContainsLiveEn("ctrl"));
        Assert.False(triggers.ContainsFinalEn("ctrl"));
    }

    [Fact]
    public void NgramModelsSeparateWordsFromLayoutGibberish()
    {
        NgramModel russian = NgramModel.LoadRussian();
        NgramModel english = NgramModel.LoadEnglish();
        Assert.True(russian.IsLoaded);
        Assert.True(english.IsLoaded);

        double word = russian.Score("привет", complete: true).Relative;
        double gibberish = english.Score("ghbdtn", complete: true).Relative;
        Assert.True(word > gibberish + 1.5, $"привет={word:F2} ghbdtn={gibberish:F2}");

        Assert.False(english.Score("ghb,", complete: true).IsValid);
        Assert.Null(english.Normalize("j,]z"));
        Assert.Equal("ее", russian.Normalize("Её"));
    }

    [Fact]
    public void ShortWordRanksKnowFrequentForms()
    {
        ShortWordRanks russian = ShortWordRanks.LoadRussian();
        ShortWordRanks english = ShortWordRanks.LoadEnglish();
        Assert.True(russian.RankOf("не") < 10);
        Assert.True(russian.RankOf("ему") < 500);
        Assert.True(english.RankOf("the") < 10);
        Assert.Equal(ShortWordRanks.Unknown, english.RankOf("ghb"));
    }

    [Fact]
    public void AffixFallbackIgnoresLoneThreeLetterSuffix()
    {
        // Invariant 5: "ueger" must not score as English from "ger" alone.
        var affixes = new AffixModels();
        Assert.Equal(0, affixes.EnglishScore("ueger"));
    }
}
