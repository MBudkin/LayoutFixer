using LayoutFixer.Input;
using Xunit;

namespace LayoutFixer.Tests;

public sealed class DecisionEngineTests
{
    private const ConvertedLanguage Ru = ConvertedLanguage.Russian;
    private const ConvertedLanguage En = ConvertedLanguage.English;

    // ── wrong layout is fixed ────────────────────────────────────────────────

    [Theory]
    [InlineData("привет")]
    [InlineData("сделать")]
    [InlineData("хочешь")]
    [InlineData("будут")]
    [InlineData("этого")]
    [InlineData("объявление")]
    [InlineData("думаешь")]
    [InlineData("не")]
    [InlineData("ты")]
    [InlineData("он")]
    [InlineData("мы")]
    [InlineData("вы")]
    [InlineData("да")]
    [InlineData("бы")]
    [InlineData("же")]
    [InlineData("её")]
    [InlineData("ещё")]
    [InlineData("туда")]
    public void RussianTypedInEnglishLayoutIsRestored(string word)
    {
        using var engine = new TestEngine();
        Assert.Equal(word, engine.Type(word, En));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("their")]
    [InlineData("check")]
    [InlineData("future")]
    [InlineData("because")]
    [InlineData("the")]
    [InlineData("what")]
    [InlineData("hey")]
    [InlineData("yeah")]
    public void EnglishTypedInRussianLayoutIsRestored(string word)
    {
        using var engine = new TestEngine();
        Assert.Equal(word, engine.Type(word, Ru));
    }

    // ── correct words are never touched ──────────────────────────────────────

    [Theory]
    // 1.8.1 regression: "ё" folding made "шё" ("i`") match "ше" ("it").
    [InlineData("it")]
    [InlineData("at")]
    [InlineData("its")]
    [InlineData("other")]
    [InlineData("others")]
    [InlineData("attack")]
    [InlineData("able")]
    [InlineData("afraid")]
    [InlineData("ahead")]
    [InlineData("neither")]
    [InlineData("kept")]
    [InlineData("enjoy")]
    [InlineData("herself")]
    [InlineData("hello")]
    [InlineData("the")]
    [InlineData("github")]
    public void CorrectEnglishStaysEnglish(string word)
    {
        using var engine = new TestEngine();
        Assert.Equal(word, engine.Type(word, En));
    }

    [Theory]
    [InlineData("думаешь")]
    [InlineData("думал")]
    [InlineData("туда")]
    [InlineData("руки")]
    [InlineData("руку")]
    [InlineData("иду")]
    [InlineData("идут")]
    [InlineData("едва")]
    [InlineData("ищу")] // 1.6.11: "ищу" <-> "boe"
    [InlineData("ищем")]
    [InlineData("суть")] // 1.6.9: "суть" <-> "cenm"
    [InlineData("буду")] // invariant 11: "буду" -> ",ele"
    [InlineData("будь")] // invariant 6: oscillation "будь" <-> ",elm"
    [InlineData("сыграть")]
    [InlineData("ерунда")]
    [InlineData("привет")]
    public void CorrectRussianStaysRussian(string word)
    {
        using var engine = new TestEngine();
        Assert.Equal(word, engine.Type(word, Ru));
    }

    // ── invariants with punctuation (AGENTS.md #6, #11) ──────────────────────

    [Theory]
    [InlineData("были,")]
    [InlineData("\"было\"")]
    [InlineData("\"сек\"")]
    public void RussianWithPunctuationIsNotHijacked(string visible)
    {
        using var engine = new TestEngine();
        Assert.Equal(visible, engine.TypeVisible(visible, Ru));
    }

    [Fact]
    public void WrappedWrongLayoutWordIsConvertedWithItsWrapper()
    {
        using var engine = new TestEngine();
        // "ЭруддщЭ" = "hello" in quotes typed in the Russian layout.
        Assert.Equal("\"hello\"", engine.TypeVisible("ЭруддщЭ", Ru));
    }

    [Fact]
    public void SystemAbbreviationIsNotATarget()
    {
        using var engine = new TestEngine();
        // Invariant 4: "Секд" (RU) must not become "Ctrl".
        Assert.Equal("Секд", engine.TypeVisible("Секд", Ru));
    }

    // ── user dictionary (invariant 7, 11) ────────────────────────────────────

    [Fact]
    public void UserDictionaryWordWinsWithTrailingPunctuation()
    {
        using var engine = new TestEngine();
        engine.UserDictionary.Add(En, "github");
        Assert.True(engine.Engine.TryFinalDecision("пшерги,", null, out ConvertedLanguage target));
        Assert.Equal(En, target);
    }

    [Fact]
    public void UserDictionaryKeepsVisibleWordOfCurrentLayout()
    {
        using var engine = new TestEngine();
        engine.UserDictionary.Add(En, "ghbdtn");
        Assert.False(engine.Engine.TryFinalDecision("ghbdtn", null, out _));
    }

    [Fact]
    public void UserDictionaryDoesNotTrustGeneratedLeadingJunk()
    {
        using var engine = new TestEngine();
        engine.UserDictionary.Add(En, "skj");
        Assert.False(engine.Engine.TryFinalDecision("\"было\"", null, out _));
    }

    // ── sentence context for very short tokens ───────────────────────────────

    [Fact]
    public void SingleLetterAfterEnglishWordStaysEnglish()
    {
        using var engine = new TestEngine();
        Assert.False(engine.Engine.TryFinalDecision("b", "plan", out _));
        Assert.False(engine.Engine.TryFinalDecision("vs", "Apple", out _));
    }

    [Fact]
    public void ShortWordAfterRussianWordIsConverted()
    {
        using var engine = new TestEngine();
        Assert.True(engine.Engine.TryFinalDecision("yt", "привет", out ConvertedLanguage target));
        Assert.Equal(Ru, target);
    }

    // 1.10.1: Russian text is full of English names and terms.
    [Theory]
    [InlineData("b", "Claude")] // Claude и ChatGPT
    [InlineData("c", "Claude")] // Claude с Opus
    [InlineData("b", "API")]
    [InlineData("b", "ChatGPT")]
    [InlineData("z", "iPhone")]
    public void SingleLetterAfterEnglishNameIsRussian(string letter, string previous)
    {
        using var engine = new TestEngine();
        Assert.True(engine.Engine.TryFinalDecision(letter, previous, out ConvertedLanguage target));
        Assert.Equal(Ru, target);
        // The layout stays English for the next term ("и ChatGPT").
        Assert.Equal(LayoutDecisionEngine.TermLetterSource, engine.Engine.LastFinalSource);
    }

    // 1.11.0: "Always fix в, а, я, и, с, у, о, к" (on by default in the app).
    [Theory]
    [InlineData("b", "plan")]
    [InlineData("z", "ok")]
    [InlineData("c", "docker")]
    [InlineData("b", "Claude")]
    public void AlwaysConvertSingleLettersIgnoresEnglishContext(string letter, string previous)
    {
        using var engine = new TestEngine();
        engine.Engine.AlwaysConvertSingleLetters = () => true;
        Assert.True(engine.Engine.TryFinalDecision(letter, previous, "", out ConvertedLanguage target));
        Assert.Equal(Ru, target);
        Assert.Equal(LayoutDecisionEngine.TermLetterSource, engine.Engine.LastFinalSource);
    }

    [Theory]
    [InlineData("B", "Plan")] // capitals are labels
    [InlineData("a", "Claude")] // English "a"
    [InlineData("k", "ok")] // "л" is no word
    public void AlwaysConvertSingleLettersKeepsNonWords(string letter, string previous)
    {
        using var engine = new TestEngine();
        engine.Engine.AlwaysConvertSingleLetters = () => true;
        Assert.False(engine.Engine.TryFinalDecision(letter, previous, "", out _));
    }

    [Fact]
    public void SingleLetterAtSentenceStartSwitchesLayout()
    {
        using var engine = new TestEngine();
        Assert.True(engine.Engine.TryFinalDecision("z", null, out _));
        Assert.NotEqual(LayoutDecisionEngine.TermLetterSource, engine.Engine.LastFinalSource);
    }

    // 1.10.1: mixed case is no identifier in Cyrillic.
    [Theory]
    [InlineData("ChatGPT")]
    [InlineData("OpenAI")]
    [InlineData("iPhone")]
    [InlineData("JavaScript")]
    [InlineData("YouTube")]
    public void MixedCaseNameTypedInRussianLayoutIsFixed(string intended)
    {
        using var engine = new TestEngine();
        string typed = engine.WrongLayout(intended);
        Assert.True(engine.Engine.TryFinalDecision(typed, null, out ConvertedLanguage target));
        Assert.Equal(En, target);
        Assert.Equal(intended, engine.Converter.Convert(typed, Ru));
    }

    [Theory]
    [InlineData("МегаФон")]
    [InlineData("СберБанк")]
    public void MixedCaseRussianNameStays(string visible)
    {
        using var engine = new TestEngine();
        Assert.False(engine.Engine.TryFinalDecision(visible, null, out _));
    }

    [Fact]
    public void SingleLetterAfterEnglishTermInRussianSentenceIsRussian()
    {
        using var engine = new TestEngine();
        Assert.True(engine.Engine.TryFinalDecision("b", "docker", "Сравни docker ", out ConvertedLanguage target));
        Assert.Equal(Ru, target);
        Assert.False(engine.Engine.TryFinalDecision("b", "docker", "compare docker ", out _));
    }

    [Theory]
    [InlineData("b", "plan")]
    [InlineData("B", "Plan")]
    [InlineData("C", "Vitamin")]
    [InlineData("a", "Claude")] // English "a"
    public void LetterInEnglishTextStaysEnglish(string letter, string previous)
    {
        using var engine = new TestEngine();
        Assert.False(engine.Engine.TryFinalDecision(letter, previous, "", out _));
    }

    // 1.10.1: a word that ends a sentence is no context for the next one.
    [Theory]
    [InlineData("hello.")]
    [InlineData("really?")]
    [InlineData("(done.)")]
    public void SingleLetterAfterSentenceEndIsConverted(string previous)
    {
        using var engine = new TestEngine();
        Assert.True(engine.Engine.TryFinalDecision("d", previous, out ConvertedLanguage target));
        Assert.Equal(Ru, target);
    }

    [Theory]
    [InlineData("d", "в")]
    [InlineData("f", "а")]
    [InlineData("z", "я")]
    [InlineData("b", "и")]
    [InlineData("c", "с")]
    [InlineData("e", "у")]
    [InlineData("j", "о")]
    [InlineData("r", "к")]
    public void SingleLetterBeforeConvertedRussianWordFollowsIt(string letter, string expected)
    {
        // "hello z levf" -> "hello я думаю".
        using var engine = new TestEngine();
        Assert.True(engine.Engine.TryConvertPrecedingLetter(letter, Ru, out string converted));
        Assert.Equal(expected, converted);
    }

    [Theory]
    [InlineData("ш", "i")]
    [InlineData("Ш", "I")]
    [InlineData("ф", "a")]
    public void SingleLetterBeforeConvertedEnglishWordFollowsIt(string letter, string expected)
    {
        using var engine = new TestEngine();
        Assert.True(engine.Engine.TryConvertPrecedingLetter(letter, En, out string converted));
        Assert.Equal(expected, converted);
    }

    [Theory]
    [InlineData("a", true)] // English word "a"
    [InlineData("i", true)] // English word "i"
    [InlineData("B", true)] // "plan B": capitals are labels
    [InlineData("k", true)] // "л" is not a word
    [InlineData("z", false)] // already English
    [InlineData("я", false)] // Russian word, "z" is no English word
    [InlineData("ab", true)]
    public void PrecedingLetterIsKeptWhenItIsNoWrongLayoutWord(string letter, bool toRussian)
    {
        using var engine = new TestEngine();
        Assert.False(engine.Engine.TryConvertPrecedingLetter(letter, toRussian ? Ru : En, out _));
    }

    [Fact]
    public void PrecedingLetterInUserDictionaryIsKept()
    {
        using var engine = new TestEngine();
        engine.UserDictionary.Add(En, "b");
        Assert.False(engine.Engine.TryConvertPrecedingLetter("b", Ru, out _));
    }

    // 1.10.1: Caps Lock typed "dSGBIB" for "Выпиши". ConversionService judges
    // the word in the case the user meant, which is a plain wrong-layout word.
    [Fact]
    public void CapsLockWordIsJudgedInTheIntendedCase()
    {
        using var engine = new TestEngine();
        string intended = TextTransformationService.NormalizeAccidentalCapsLock("dSGBIB");
        Assert.Equal("Dsgbib", intended);
        Assert.True(engine.Engine.TryFinalDecision(intended, null, out ConvertedLanguage target));
        Assert.Equal(Ru, target);
        Assert.Equal("вЫПИШИ", engine.Converter.Convert("dSGBIB", En));
        Assert.Equal("Выпиши", TextTransformationService.NormalizeAccidentalCapsLock("вЫПИШИ"));
    }

    // ── addresses ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ivan@mail")]
    [InlineData("https://ghbdtn")]
    [InlineData("www.ghbdtn")]
    public void AddressesAreLeftAlone(string visible)
    {
        using var engine = new TestEngine();
        Assert.False(engine.Engine.TryFinalDecision(visible, null, out _));
    }

    [Theory]
    [InlineData("iPhone")]
    [InlineData("camelCase")]
    [InlineData("VkShift")]
    [InlineData("UtcNow")]
    [InlineData("KeyFor")]
    [InlineData("SendInput")]
    [InlineData("DIB")]
    [InlineData("API")]
    [InlineData("USB")]
    [InlineData("PDF")]
    [InlineData("HTML")]
    public void IdentifiersAndAbbreviationsStayAsTyped(string visible)
    {
        using var engine = new TestEngine();
        Assert.Equal(visible, engine.TypeVisible(visible, En));
    }

    [Fact]
    public void FrequentRussianWordInCapsIsStillFixed()
    {
        using var engine = new TestEngine();
        Assert.Equal("НЕ", engine.TypeVisible("YT", En));
    }
}
