using System;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Input;

/// <summary>
/// Pure layout-decision logic shared by the live (mid-word) and final
/// (after a separator) auto-conversion paths. It has no side effects: it does
/// not touch the keyboard buffer, does not send input and does not switch the
/// system layout. <see cref="ConversionService"/> owns the per-word state
/// (revert lock, live flag) and performs the actual replacement.
/// </summary>
/// <remarks>
/// Keeping the decision pure makes it testable (see tests/LayoutFixer.Tests)
/// and lets tools/LayoutFixer.Tools measure precision/recall on word lists.
/// Read AGENTS.md "Критические инварианты" before changing anything here.
/// </remarks>
internal sealed class LayoutDecisionEngine
{
    private readonly LayoutConverter _converter = new();
    private readonly TriggerDictionaries _triggers;
    private readonly UserDictionary _userDictionary;
    private readonly AffixModels _affixes;
    private readonly UserReplacements _replacements;
    private readonly NgramModel? _russianModel;
    private readonly NgramModel? _englishModel;
    private readonly ShortWordRanks? _russianShort;
    private readonly ShortWordRanks? _englishShort;

    public LayoutDecisionEngine(
        TriggerDictionaries triggers,
        UserDictionary userDictionary,
        AffixModels affixes,
        UserReplacements replacements,
        NgramModel? russianModel,
        NgramModel? englishModel,
        ShortWordRanks? russianShort,
        ShortWordRanks? englishShort)
    {
        _triggers = triggers;
        _userDictionary = userDictionary;
        _affixes = affixes;
        _replacements = replacements;
        _russianModel = russianModel;
        _englishModel = englishModel;
        _russianShort = russianShort;
        _englishShort = englishShort;
    }

    public NgramDecisionOptions Ngram { get; set; } = new();

    // Settings → Correction: "Always fix в, а, я, и, с, у, о, к". On: such a
    // lowercase letter typed in the English layout becomes Russian even right
    // after an English word ("ok z" -> "ok я"). Off: only after an English
    // name/term or inside a Russian sentence ("plan b" stays).
    public Func<bool> AlwaysConvertSingleLetters { get; set; } = () => false;

    public LayoutConverter Converter => _converter;

    // Which layer produced the last positive final decision (diagnostics and
    // tools only; the hook path does not depend on it).
    public string LastFinalSource { get; private set; } = string.Empty;

    // LastFinalSource of a single Russian letter among English names
    // ("Claude b ChatGPT" -> "Claude и ChatGPT"). The caller fixes the letter
    // but keeps the English layout: the next word is most likely another
    // English term, and an abbreviation or a mixed-case name typed in the
    // Russian layout ("ФЗШ", "СрфеПЗЕ") is much harder to recover.
    public const string TermLetterSource = "term-letter";

    // ── LIVE ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decides whether the word being typed should be converted right now.
    /// The caller has already applied the per-word state checks (live flag,
    /// revert lock) and guarantees the token has letters and no digits.
    /// </summary>
    public LiveDecision DecideLive(string tokenText, ConvertedLanguage systemLanguage)
    {
        if (systemLanguage is not (ConvertedLanguage.Russian or ConvertedLanguage.English))
        {
            return LiveDecision.Miss;
        }

        // The "other side" form: what the typed text looks like after a layout
        // conversion. We evaluate both spellings against the user dictionary and
        // the trigger tables.
        string convertedText = _converter.Convert(tokenText, systemLanguage);
        ConvertedLanguage target = Oppose(systemLanguage);

        // ── USER GUARDS (must run BEFORE any LiveTrigger) ───────────────────
        // Per spec, user entries primarily GUARD here — they hold the LiveTrigger
        // off while the typed tail could still grow into a user entry. The final
        // (after-separator) decision is where the user dictionary authoritatively
        // picks a layout.
        //
        // 2) UserDictionary guard — checks BOTH spellings (visible + converted):
        //    if either could still become a user word, the guard is active.
        // 3) UserReplacement guard — checks ONLY the visible spelling. A
        //    replacement must NEVER be triggered via its converted form
        //    ("gynh -> пнтр -> replacement" is explicitly forbidden by spec).
        //
        // Compute the live-hit FIRST, then decide whether the guard overrides
        // it. The guard blocks the hit ONLY when the user-dict word it points
        // at lives in the CURRENT layout — i.e. the user might be typing that
        // exact word right now. When the guard word lives in the TARGET layout
        // it AGREES with the hit (both want the same layout), so the hit fires.
        // Example: dict has "хошь" (RU), typing "[j" on EN -> converted "хо" is
        // both a LiveRu trigger and a prefix of the RU word "хошь": fire now
        // instead of waiting for the full word.
        string lookup = TrimNonLetters(convertedText);
        // OSCILLATION GUARD. If the converted form gets edge punctuation from
        // layout mapping (e.g. RU 'б' -> EN ',' or RU quote -> EN '@'), trimming
        // it can expose a phantom trigger such as "sk" inside "\"было\"". Live
        // has no full closing context yet, so leave such words to final.
        if (ShouldSuppressConvertedEdgeLookup(tokenText, systemLanguage, target, convertedText, lookup))
        {
            Logger.Log($"TryLiveConvert: edge guard -> typed '{tokenText}' converted '{convertedText}' lookup '{lookup}'");
            return LiveDecision.Miss;
        }

        string visibleLookup = TrimNonLetters(tokenText);

        // "VkShift", "UtcNow", "iPhone": an uppercase letter right after a
        // lowercase one marks an identifier or a brand name, not a word typed
        // in the wrong layout. Both spellings must show it: in "ЭруддщЭ" the
        // capital "Э" is just a quote key of the other layout.
        if (HasInnerUppercase(visibleLookup) && HasInnerUppercase(lookup))
        {
            Logger.Log($"TryLiveConvert: mixed-case identifier '{tokenText}' -> keep");
            return LiveDecision.Miss;
        }

        // The visible keys behind the converted letters (",e" for "бу", "[j"
        // for "хо"): what the user actually typed for that part of the word.
        string alignedVisible = lookup.Length == 0 ? string.Empty : AlignedSlice(tokenText, convertedText);
        bool triggerHit = ContainsLiveTrigger(target, lookup);
        string source = "trigger";

        if (!triggerHit)
        {
            // No table hit: ask the language models whether the typed prefix is
            // clearly impossible in the current layout while its converted form
            // reads like the start of a normal word of the other language.
            if (!IsNgramLiveHit(tokenText, alignedVisible, systemLanguage, lookup, target))
            {
                return LiveDecision.Miss;
            }

            triggerHit = true;
            source = "ngram";
        }

        // The trigger tables can contain a valid token on BOTH sides of the
        // keyboard map. Example: visible RU "ищу" is a LiveRu token, while its
        // EN-layout form "boe" is also a LiveEn token. In that situation there
        // is no reliable evidence that the current text is wrong, so keep the
        // visible layout. Otherwise each side would auto-convert into the other.
        if (ContainsLiveTrigger(systemLanguage, visibleLookup))
        {
            Logger.Log($"TryLiveConvert: ambiguous live triggers -> keep visible '{tokenText}' (converted '{convertedText}')");
            return LiveDecision.Miss;
        }

        // The typed prefix is already a common complete word of the current
        // language ("иду", "руки", "able"): the user may well be typing exactly
        // that. Leave the decision to the final stage, which sees the whole word.
        int visibleRank = IsAllLetters(alignedVisible) ? RankOf(systemLanguage, alignedVisible) : ShortWordRanks.Unknown;
        if (visibleRank <= Ngram.LiveHoldRank)
        {
            Logger.Log($"TryLiveConvert: '{alignedVisible}' is a common {systemLanguage} word (rank {visibleRank}) -> wait for final");
            return LiveDecision.Miss;
        }

        // A trigger hit on a prefix that reads perfectly normally in the
        // current language ("nel" for "туда", "abl" for "фид") is unreliable:
        // the final stage still converts the finished word when it is wrong.
        if (source == "trigger" && IsPlausiblePrefix(systemLanguage, alignedVisible, target, lookup))
        {
            Logger.Log($"TryLiveConvert: '{alignedVisible}' is a plausible {systemLanguage} prefix -> wait for final");
            return LiveDecision.Miss;
        }

        // Evaluate the user guards only now that we have a hit to override.
        bool guardCurrent = UserDictionaryGuardInCurrentLayout(tokenText, systemLanguage);
        bool guardTarget = UserDictionaryGuardInTargetLayout(convertedText, systemLanguage);
        bool replacementGuard = _replacements.HasTriggerPrefix(tokenText);

        if (guardCurrent)
        {
            // The user-dict word is in the CURRENT layout: the user may be
            // typing it on purpose. Hold the hit off and let the final stage
            // decide.
            Logger.Log($"TryLiveConvert: user guard (current layout) holds off LiveTrigger -> '{tokenText}'");
            return LiveDecision.Miss;
        }

        if (replacementGuard)
        {
            Logger.Log($"TryLiveConvert: replacement guard holds off LiveTrigger -> '{tokenText}'");
            return LiveDecision.Miss;
        }

        // guardTarget (without guardCurrent) AGREES with the hit: the dict word
        // is in the layout we are switching TO, so firing early is consistent
        // with the eventual user-dict decision. Fall through and fire.
        if (guardTarget)
        {
            Logger.Log($"TryLiveConvert: user guard agrees with target layout -> firing early for '{tokenText}'");
        }

        Logger.Log($"TryLiveConvert: {source} hit '{tokenText}' -> '{convertedText}'");
        return new LiveDecision(true, convertedText, target, source);
    }

    // True when the typed prefix reads at least about as naturally in the
    // current language as its converted spelling reads in the target one.
    private bool IsPlausiblePrefix(
        ConvertedLanguage language,
        string visibleLookup,
        ConvertedLanguage targetLanguage,
        string convertedLookup)
    {
        NgramDecisionOptions options = Ngram;
        if (!options.LiveVetoEnabled ||
            visibleLookup.Length < options.LiveVetoMinLength ||
            visibleLookup.Length != convertedLookup.Length ||
            !IsAllLetters(visibleLookup))
        {
            return false;
        }

        NgramScore visible = Score(language, visibleLookup, complete: false);
        NgramScore converted = Score(targetLanguage, convertedLookup, complete: false);
        return visible.IsValid && converted.IsValid &&
               visible.UnseenTrigrams == 0 &&
               visible.Relative >= converted.Relative - options.LiveVetoMargin;
    }

    public int RankOf(ConvertedLanguage language, string core)
    {
        ShortWordRanks? table = language switch
        {
            ConvertedLanguage.Russian => _russianShort,
            ConvertedLanguage.English => _englishShort,
            _ => null
        };

        return table is null || core.Length == 0 ? ShortWordRanks.Unknown : table.RankOf(core);
    }

    private bool IsNgramLiveHit(
        string tokenText,
        string visibleLookup,
        ConvertedLanguage visibleLanguage,
        string convertedLookup,
        ConvertedLanguage targetLanguage)
    {
        NgramDecisionOptions options = Ngram;
        if (!options.LiveEnabled)
        {
            return false;
        }

        // Only plain words: no leading/trailing symbols in the typed text (an
        // opening quote or bracket is fine, it stays outside the core), and no
        // mixed case inside the word such as "iPhone" or "camelCase".
        if (visibleLookup.Length < options.LiveMinLength || HasInnerUppercase(visibleLookup))
        {
            return false;
        }

        // Every visible character inside the core must be a real letter of the
        // current layout: a symbol inside ("j,]") is left to the final stage,
        // where the whole word is known.
        if (!IsAllLetters(visibleLookup) || convertedLookup.Length != visibleLookup.Length)
        {
            return false;
        }

        // A visible form that is a known word or prefix in the current
        // language is never "impossible".
        if (ContainsAnyTrigger(visibleLanguage, visibleLookup))
        {
            return false;
        }

        NgramScore visible = Score(visibleLanguage, visibleLookup, complete: false);
        NgramScore converted = Score(targetLanguage, convertedLookup, complete: false);
        if (!visible.IsValid || !converted.IsValid)
        {
            return false;
        }

        bool hit = visible.UnseenTrigrams >= options.LiveMinVisibleUnseen &&
                   converted.UnseenTrigrams <= options.LiveMaxTargetUnseen &&
                   converted.Relative >= options.LiveMinTarget &&
                   converted.Relative - visible.Relative >= options.LiveMargin;

        if (hit)
        {
            Logger.Log($"TryLiveConvert: ngram visible={visible.Relative:F2}/{visible.UnseenTrigrams} " +
                       $"converted={converted.Relative:F2}/{converted.UnseenTrigrams} '{tokenText}'");
        }

        return hit;
    }

    // ── FINAL ────────────────────────────────────────────────────────────────

    // Picks the target language for a finished word. Per spec order:
    //   1. UserDictionary — PRIORITY layout dictionary. It checks BOTH the
    //      visible word and its converted spelling:
    //        * visible word is in the CURRENT layout's user dict  -> keep as is;
    //        * converted word is in the OTHER layout's user dict   -> convert + switch;
    //        * both forms present                                    -> ambiguous, skip.
    //   2. FinalTriggers (+ LiveTriggers as exact words).
    //   3. Character n-gram language models.
    //   4. Affix heuristic fallback.
    // (UserReplacement is handled by the caller BEFORE FinalConvert, because a
    // replacement expands the visible word and is independent of layout.)
    // `previousWord` is the word typed before this one (or null). It only
    // vetoes very short ambiguous conversions: "plan b" must stay English when
    // the sentence is English. `sentenceBefore` is the text of the current
    // sentence before this word (or null): a Russian word there shows that
    // English words around are just terms ("Сравни docker b kubernetes").
    public bool TryFinalDecision(string text, string? previousWord, out ConvertedLanguage target)
    {
        return TryFinalDecision(text, previousWord, null, out target);
    }

    public bool TryFinalDecision(string text, string? previousWord, string? sentenceBefore, out ConvertedLanguage target)
    {
        target = ConvertedLanguage.Unknown;
        LastFinalSource = string.Empty;

        ConvertedLanguage detected = _converter.DetectLanguage(text);
        if (detected == ConvertedLanguage.Unknown)
        {
            return false;
        }

        string russianForm = detected == ConvertedLanguage.Russian ? text : _converter.Convert(text);
        string englishForm = detected == ConvertedLanguage.English ? text : _converter.Convert(text);

        // Strip layout-punctuation edges before lookup so e.g. "break," matches
        // the stored "break"; the typed replacement still keeps the punctuation.
        string russianLookup = TrimNonLetters(russianForm);
        string englishLookup = TrimNonLetters(englishForm);

        // If the visible word core is pure letters and the converted form gains
        // or changes punctuation at the edges, that punctuation is part of the
        // keyboard mapping itself (for example RU U+0431 -> EN "," or RU quotes
        // -> EN "@"). Do not let TrimNonLetters turn that mapped form into a
        // phantom word such as ",ele" -> "ele", "были," -> ",skb?" -> "skb",
        // or "\"сек\"" -> "@ctr@" -> "ctr".
        // Balanced wrappers are allowed through: "ЭруддщЭ" -> "\"hello\"" and
        // "хруддщъ" -> "[hello]" should still be able to hit the normal English
        // word triggers.
        bool suppressRussianLookup = ShouldSuppressConvertedEdgeLookup(
            text,
            detected,
            ConvertedLanguage.Russian,
            russianForm,
            russianLookup);
        bool suppressEnglishLookup = ShouldSuppressConvertedEdgeLookup(
            text,
            detected,
            ConvertedLanguage.English,
            englishForm,
            englishLookup);
        bool suppressRussianUserLookup = ShouldSuppressConvertedUserDictionaryLookup(
            text,
            detected,
            ConvertedLanguage.Russian,
            russianForm,
            russianLookup);
        bool suppressEnglishUserLookup = ShouldSuppressConvertedUserDictionaryLookup(
            text,
            detected,
            ConvertedLanguage.English,
            englishForm,
            englishLookup);

        // UserDictionary is an explicit user preference, so it may use trimmed
        // converted forms in more cases than the generic trigger/affix path.
        // Still reject converted forms with generated leading edge junk such as
        // "\"было\"" -> "@,skj@"; even if "skj" somehow got into the user
        // dictionary, the visible Russian word should not be hijacked.
        bool userRu = _userDictionary.ContainsRussian(russianForm) ||
                      (!suppressRussianUserLookup && russianLookup.Length > 0 && _userDictionary.ContainsRussian(russianLookup));
        bool userEn = _userDictionary.ContainsEnglish(englishForm) ||
                      (!suppressEnglishUserLookup && englishLookup.Length > 0 && _userDictionary.ContainsEnglish(englishLookup));

        Logger.Log($"TryFinalDecision: detected={detected} userRu={userRu} userEn={userEn}");
        Logger.Log(Logger.Format("russianForm", russianForm));
        Logger.Log(Logger.Format("englishForm", englishForm));
        if (suppressRussianLookup || suppressEnglishLookup)
        {
            Logger.Log($"TryFinalDecision: suppress converted-edge lookup ru={suppressRussianLookup} en={suppressEnglishLookup}");
        }
        if (suppressRussianUserLookup || suppressEnglishUserLookup)
        {
            Logger.Log($"TryFinalDecision: suppress converted user-dict lookup ru={suppressRussianUserLookup} en={suppressEnglishUserLookup}");
        }

        // ── 1) USER DICTIONARY (priority) ──────────────────────────────────
        // Both forms present -> ambiguous, do not guess.
        if (userRu && userEn)
        {
            Logger.Log("TryFinalDecision: both forms in user dictionary -> keep as typed");
            return false;
        }

        // The visible word is valid in the CURRENT layout -> keep as typed.
        // (detected == Russian and userRu, or detected == English and userEn.)
        if ((detected == ConvertedLanguage.Russian && userRu) ||
            (detected == ConvertedLanguage.English && userEn))
        {
            Logger.Log("TryFinalDecision: visible word is a user word in the current layout -> keep as typed");
            return false;
        }

        // The CONVERTED word is valid in the OTHER layout -> switch to it.
        // detected tells us the visible layout; the converted form lives in
        // the opposite one, and a user hit there is a directive to convert.
        if (detected == ConvertedLanguage.Russian && userEn)
        {
            target = ConvertedLanguage.English;
            LastFinalSource = "user-dictionary";
            return true;
        }
        if (detected == ConvertedLanguage.English && userRu)
        {
            target = ConvertedLanguage.Russian;
            LastFinalSource = "user-dictionary";
            return true;
        }

        // Addresses, e-mails and paths are never wrong-layout words, even when
        // their pieces happen to look like one.
        if (LooksLikeAddress(text))
        {
            Logger.Log("TryFinalDecision: token looks like an address/e-mail/path -> keep as typed");
            return false;
        }

        int coreLength = TrimNonLetters(text).Length;
        ConvertedLanguage other = Oppose(detected);

        // The converted spelling that the generic layers may look at (empty
        // when the edge rules above forbid trusting it), and the part of the
        // visible text typed with the same keys. Conversion is per character,
        // so positions line up: for ",s" -> "бы" the visible counterpart of
        // "бы" is ",s", not the trimmed "s".
        string convertedForm = detected == ConvertedLanguage.Russian ? englishForm : russianForm;
        bool suppressConverted = detected == ConvertedLanguage.Russian ? suppressEnglishLookup : suppressRussianLookup;
        string convertedLookup = suppressConverted ? string.Empty : TrimNonLetters(convertedForm);
        string alignedVisible = convertedLookup.Length == 0 ? string.Empty : AlignedSlice(text, convertedForm);

        if (HasInnerUppercase(TrimNonLetters(text)) && HasInnerUppercase(convertedLookup))
        {
            // Mixed case in Cyrillic is not an identifier: "СрфеПЗЕ" is
            // "ChatGPT" typed in the Russian layout. Judge the word in lower
            // case; real Russian names ("МегаФон", "СберБанк") read normally
            // that way and stay. Latin "iPhone"/"VkShift" stay as typed.
            if (detected == ConvertedLanguage.Russian)
            {
                Logger.Log("TryFinalDecision: mixed-case Cyrillic word -> decide in lower case");
                return TryFinalDecision(text.ToLowerInvariant(), previousWord, sentenceBefore, out target);
            }

            Logger.Log("TryFinalDecision: mixed-case identifier -> keep as typed");
            return false;
        }

        if (detected == ConvertedLanguage.Russian && TriggerDictionaries.IsSystemAbbreviation(convertedLookup))
        {
            Logger.Log("TryFinalDecision: converted form is a key name -> keep as typed");
            return false;
        }

        // ── 2) SHORT WORDS BY FREQUENCY ────────────────────────────────────
        // "yt" -> "не", "ns" -> "ты", ",s" -> "бы", but "it", "at", "able"
        // stay English.
        Verdict shortVerdict = DecideByShortWords(alignedVisible, convertedLookup, detected, previousWord);
        if (shortVerdict == Verdict.Keep)
        {
            Logger.Log("TryFinalDecision: short-word frequencies favour the visible word -> keep as typed");
            return false;
        }

        if (shortVerdict == Verdict.Convert)
        {
            target = other;
            LastFinalSource = "short-word";
            return true;
        }

        // ALL-CAPS tokens of 2-4 letters are abbreviations ("DIB", "API",
        // "USB"). Only the frequency rule above may convert them ("YT" ->
        // "НЕ"); the trigger tables would turn "DIB" into "ВШИ".
        if (convertedLookup.Length is >= 2 and <= ShortWordRanks.MaxLength && IsAllUpper(convertedLookup))
        {
            Logger.Log("TryFinalDecision: short ALL-CAPS token without frequency evidence -> keep as typed");
            return false;
        }

        // ── 3) EXACT TRIGGERS ──────────────────────────────────────────────
        // Live triggers are safe prefixes that can decide before a separator;
        // at final time an exact hit is at least as strong as a FinalTrigger.
        // Include both tables so a known current-layout form cannot fall through
        // to the weaker affix model. For example, "суть" is a LiveRu trigger;
        // without this check its EN-layout form "cenm" scored 3 from the "cen"
        // prefix and the correct Russian word was changed to English.
        bool finalTableRu = !suppressRussianLookup && russianLookup.Length > 0 && _triggers.ContainsFinalRu(russianLookup);
        bool finalTableEn = !suppressEnglishLookup && englishLookup.Length > 0 && _triggers.ContainsFinalEn(englishLookup);
        bool liveTableRu = !suppressRussianLookup && russianLookup.Length > 0 && _triggers.ContainsLiveRu(russianLookup);
        bool liveTableEn = !suppressEnglishLookup && englishLookup.Length > 0 && _triggers.ContainsLiveEn(englishLookup);
        bool finalRu = finalTableRu || liveTableRu;
        bool finalEn = finalTableEn || liveTableEn;

        Logger.Log($"TryFinalDecision: finalRu={finalRu} finalEn={finalEn} " +
                   $"(finalTableRu={finalTableRu} finalTableEn={finalTableEn} " +
                   $"liveTableRu={liveTableRu} liveTableEn={liveTableEn})");

        ConvertedLanguage winning;
        string source = "trigger";
        if (finalRu && !finalEn)
        {
            winning = ConvertedLanguage.Russian;
        }
        else if (finalEn && !finalRu)
        {
            winning = ConvertedLanguage.English;
        }
        else if (finalRu && finalEn)
        {
            // Ambiguous final hit with no user signal: prefer Russian for short
            // words (RU is the more common target in a RU/EN typing context),
            // otherwise give up rather than guess wrong.
            if (coreLength > 0 && coreLength <= 3)
            {
                winning = ConvertedLanguage.Russian;
            }
            else
            {
                return false;
            }
        }
        else
        {
            // ── 4) LANGUAGE MODELS ──────────────────────────────────────────
            Verdict verdict = DecideByNgrams(alignedVisible, convertedLookup, detected, out double advantage);
            if (verdict == Verdict.Convert)
            {
                winning = other;
                source = "ngram";
            }
            else if (verdict == Verdict.Keep)
            {
                Logger.Log("TryFinalDecision: language model says the visible word is plausible -> keep as typed");
                return false;
            }
            else
            {
                // ── 5) AFFIX FALLBACK ───────────────────────────────────────
                // Affixes alone are a weak signal ("думаешь" -> "levftim" from
                // an English "lev" stem), so they may only tip a word the
                // language models do not object to.
                string affixRussianLookup = suppressRussianLookup ? string.Empty : russianLookup;
                string affixEnglishLookup = suppressEnglishLookup ? string.Empty : englishLookup;
                if (coreLength < 4 || !TryAffixDecision(affixRussianLookup, affixEnglishLookup, out winning))
                {
                    return false;
                }

                if (winning != detected && advantage < Ngram.AffixMinAdvantage)
                {
                    Logger.Log($"TryFinalDecision: affix vote vetoed by language models (advantage {advantage:F2})");
                    return false;
                }

                source = "affix";
            }
        }

        if (winning == detected)
        {
            // The winning language is the one the text is already in: nothing to
            // convert. (The text is valid as typed.)
            return false;
        }

        // Context veto for very short tokens: a one- or two-letter word right
        // after a word that stayed in the current layout is far more likely a
        // letter/abbreviation of that sentence ("plan b", "vitamin c") than a
        // wrong-layout word.
        if (coreLength <= 2 && ContextLanguage(previousWord) == detected)
        {
            if (!IsRussianLetterAmongEnglishTerms(text, detected, previousWord, sentenceBefore))
            {
                Logger.Log($"TryFinalDecision: short token after a {detected} word -> keep as typed");
                return false;
            }

            source = TermLetterSource;
        }

        target = winning;
        LastFinalSource = source;
        return true;
    }

    private enum Verdict
    {
        Unknown,
        Convert,
        Keep
    }

    // Slice of `text` covering the letters of `convertedForm` (same length,
    // converted character by character).
    private static string AlignedSlice(string text, string convertedForm)
    {
        int start = 0;
        int end = convertedForm.Length;
        while (start < end && !char.IsLetter(convertedForm[start]))
        {
            start++;
        }

        while (end > start && !char.IsLetter(convertedForm[end - 1]))
        {
            end--;
        }

        return end <= text.Length ? text.Substring(start, end - start) : string.Empty;
    }

    private Verdict DecideByShortWords(
        string visible,
        string converted,
        ConvertedLanguage detected,
        string? previousWord)
    {
        NgramDecisionOptions options = Ngram;
        if (!options.ShortWordsEnabled ||
            converted.Length < 2 || converted.Length > ShortWordRanks.MaxLength ||
            visible.Length != converted.Length)
        {
            // Single letters are left to the trigger tables and the sentence
            // context.
            return Verdict.Unknown;
        }

        // ALL-CAPS short tokens are usually abbreviations ("DJ", "PDF"): they
        // need overwhelming evidence ("YT" -> "НЕ").
        bool abbreviation = IsAllUpper(converted);

        ConvertedLanguage target = Oppose(detected);
        int visibleRank = IsAllLetters(visible) ? RankOf(detected, visible) : ShortWordRanks.Unknown;
        int convertedRank = RankOf(target, converted);
        if (visibleRank == ShortWordRanks.Unknown && convertedRank == ShortWordRanks.Unknown)
        {
            return Verdict.Unknown;
        }

        double margin = Math.Log(visibleRank) - Math.Log(convertedRank);
        double threshold = options.ShortMargin + (abbreviation ? options.ShortAbbreviationPenalty : 0);
        ConvertedLanguage context = ContextLanguage(previousWord);
        if (context == detected)
        {
            threshold += options.ShortSameContextPenalty;
        }
        else if (context == target)
        {
            threshold -= options.ShortTargetContextBonus;
        }

        Logger.Log($"TryFinalDecision: short words visible={visibleRank} converted={convertedRank} margin={margin:F2} threshold={threshold:F2}");
        if (margin >= threshold)
        {
            return Verdict.Convert;
        }

        return visibleRank != ShortWordRanks.Unknown ? Verdict.Keep : Verdict.Unknown;
    }

    private ConvertedLanguage ContextLanguage(string? previousWord)
    {
        if (string.IsNullOrEmpty(previousWord) || EndsSentence(previousWord))
        {
            return ConvertedLanguage.Unknown;
        }

        string core = TrimNonLetters(previousWord);
        return core.Length >= 2 ? _converter.DetectLanguage(core) : ConvertedLanguage.Unknown;
    }

    // "Hello. z" or "(see above.) z": the previous word closes a sentence and
    // says nothing about the language of the next one.
    private static bool EndsSentence(string word)
    {
        string trimmed = word.TrimEnd('"', '\'', ')', ']', '»');
        return trimmed.Length > 0 && trimmed[^1] is '.' or '?' or '…';
    }

    /// <summary>
    /// A single-letter word left in the source layout right before a word that
    /// is being converted to <paramref name="target"/>: "z" in "hello z levf".
    /// On its own such a letter stays as typed after a word of its layout
    /// ("plan b"), but the converted word that follows shows what was meant:
    /// "hello я думаю". Only letters that are a word in the target language and
    /// not in the source one qualify (EN d/f/z/b/c/e/j/r -> RU в/а/я/и/с/у/о/к,
    /// RU ф/ш -> EN a/i). Capitals are names and labels ("plan B"), except "I".
    /// </summary>
    public bool TryConvertPrecedingLetter(string letter, ConvertedLanguage target, out string converted)
    {
        converted = string.Empty;
        ConvertedLanguage source = Oppose(target);
        if (source == ConvertedLanguage.Unknown ||
            letter.Length != 1 ||
            !char.IsLetter(letter[0]) ||
            _converter.DetectLanguage(letter) != source)
        {
            return false;
        }

        string candidate = _converter.Convert(letter, source);
        if (candidate.Length != 1 || !char.IsLetter(candidate[0]))
        {
            return false;
        }

        if (!char.IsLower(letter[0]) && !string.Equals(candidate, "I", StringComparison.Ordinal))
        {
            return false;
        }

        if (IsSingleLetterWord(source, letter) || !IsSingleLetterWord(target, candidate))
        {
            return false;
        }

        // The user explicitly keeps this letter in its layout.
        bool userKeeps = source == ConvertedLanguage.English
            ? _userDictionary.ContainsEnglish(letter)
            : _userDictionary.ContainsRussian(letter);
        if (userKeeps)
        {
            return false;
        }

        converted = candidate;
        return true;
    }

    private bool IsSingleLetterWord(ConvertedLanguage language, string letter)
    {
        return language switch
        {
            ConvertedLanguage.Russian => _triggers.ContainsFinalRu(letter),
            ConvertedLanguage.English => _triggers.ContainsFinalEn(letter),
            _ => false
        };
    }

    // "Claude и ChatGPT", "Claude с Opus", "API и SDK": Russian text is full of
    // English names and terms, so an English word before a single letter is
    // weak evidence that the sentence is English. The letter stays only after
    // an ordinary lowercase English word ("plan b") in a sentence without
    // Russian words. Applies to lowercase letters that are a Russian word and
    // not an English one (d/f/z/b/c/e/j/r -> в/а/я/и/с/у/о/к); "plan B" keeps.
    private bool IsRussianLetterAmongEnglishTerms(
        string text,
        ConvertedLanguage detected,
        string? previousWord,
        string? sentenceBefore)
    {
        if (detected != ConvertedLanguage.English ||
            !string.Equals(TrimNonLetters(text), text, StringComparison.Ordinal) ||
            !TryConvertPrecedingLetter(text, ConvertedLanguage.Russian, out _))
        {
            return false;
        }

        if (AlwaysConvertSingleLetters())
        {
            Logger.Log($"TryFinalDecision: '{text}' -> Russian letter (always convert single letters)");
            return true;
        }

        string previousCore = TrimNonLetters(previousWord ?? string.Empty);
        if (!IsAllLower(previousCore))
        {
            Logger.Log($"TryFinalDecision: '{text}' after the name/term '{previousCore}' -> Russian letter");
            return true;
        }

        if (ContainsCyrillic(sentenceBefore))
        {
            Logger.Log($"TryFinalDecision: '{text}' in a Russian sentence -> Russian letter");
            return true;
        }

        return false;
    }

    private static bool IsAllLower(string core)
    {
        foreach (char character in core)
        {
            if (char.IsLetter(character) && !char.IsLower(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsCyrillic(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (char character in text)
        {
            if (character is >= '\u0400' and <= '\u04FF')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAllUpper(string core)
    {
        foreach (char character in core)
        {
            if (char.IsLetter(character) && !char.IsUpper(character))
            {
                return false;
            }
        }

        return true;
    }

    // `advantage` = how much more plausible (nats per letter) the converted
    // spelling is than the visible one; 0 when the models cannot tell.
    private Verdict DecideByNgrams(
        string visible,
        string converted,
        ConvertedLanguage detected,
        out double advantage)
    {
        advantage = 0;
        NgramDecisionOptions options = Ngram;
        if (!options.FinalEnabled ||
            converted.Length < options.FinalMinLength ||
            visible.Length != converted.Length ||
            HasInnerUppercase(converted))
        {
            return Verdict.Unknown;
        }

        ConvertedLanguage target = Oppose(detected);
        NgramScore visibleScore = Score(detected, visible, complete: true);
        NgramScore convertedScore = Score(target, converted, complete: true);
        if (!convertedScore.IsValid)
        {
            return Verdict.Unknown;
        }

        double visibleValue = visibleScore.IsValid ? visibleScore.Relative : options.InvalidVisibleScore;
        advantage = convertedScore.Relative - visibleValue;
        Logger.Log($"TryFinalDecision: ngram visible={visibleValue:F2}/{visibleScore.UnseenTrigrams} " +
                   $"converted={convertedScore.Relative:F2}/{convertedScore.UnseenTrigrams} advantage={advantage:F2}");

        if (convertedScore.Relative >= options.FinalMinTarget &&
            convertedScore.UnseenTrigrams <= Math.Max(options.FinalMaxTargetUnseen, converted.Length / 4) &&
            advantage >= options.FinalMargin)
        {
            return Verdict.Convert;
        }

        if (visibleScore.IsValid &&
            visibleScore.Relative >= options.KeepMinVisible &&
            visibleScore.UnseenTrigrams == 0 &&
            advantage <= 0)
        {
            return Verdict.Keep;
        }

        return Verdict.Unknown;
    }

    public NgramScore Score(ConvertedLanguage language, string core, bool complete)
    {
        NgramModel? model = language switch
        {
            ConvertedLanguage.Russian => _russianModel,
            ConvertedLanguage.English => _englishModel,
            _ => null
        };

        if (model is null || !model.IsLoaded)
        {
            return NgramScore.Invalid;
        }

        string? normalized = model.Normalize(core);
        return normalized is null ? NgramScore.Invalid : model.Score(normalized, complete);
    }

    private bool TryAffixDecision(string russianForm, string englishForm, out ConvertedLanguage target)
    {
        target = ConvertedLanguage.Unknown;

        // Length-weighted: each matched affix contributes its length, so a
        // specific 5-char stem beats a generic 3-char one.
        int ru = _affixes.RussianScore(russianForm);
        int en = _affixes.EnglishScore(englishForm);

        Logger.Log($"AffixDecision: ru={ru} en={en}");

        if (ru > en && ru > 0)
        {
            target = ConvertedLanguage.Russian;
            return true;
        }

        if (en > ru && en > 0)
        {
            target = ConvertedLanguage.English;
            return true;
        }

        // tie (incl. both 0): inconclusive, do nothing
        return false;
    }

    // ── SHARED HELPERS ───────────────────────────────────────────────────────

    private bool ContainsLiveTrigger(ConvertedLanguage language, string lookup)
    {
        if (lookup.Length == 0)
        {
            return false;
        }

        return language switch
        {
            ConvertedLanguage.Russian => _triggers.ContainsLiveRu(lookup),
            ConvertedLanguage.English => _triggers.ContainsLiveEn(lookup),
            _ => false
        };
    }

    private bool ContainsAnyTrigger(ConvertedLanguage language, string lookup)
    {
        if (lookup.Length == 0)
        {
            return false;
        }

        return language switch
        {
            ConvertedLanguage.Russian => _triggers.ContainsLiveRu(lookup) || _triggers.ContainsFinalRu(lookup),
            ConvertedLanguage.English => _triggers.ContainsLiveEn(lookup) || _triggers.ContainsFinalEn(lookup),
            _ => false
        };
    }

    // UserDictionary live guard, split by the layout the guard word lives in.
    // `systemLanguage` is the CURRENT layout; a guard word found there means
    // "user might be typing that word right now" (hold off). A guard word found
    // in the opposite (target) layout AGREES with a switch to that layout.
    private bool UserDictionaryGuardInCurrentLayout(string typed, ConvertedLanguage systemLanguage)
    {
        return systemLanguage switch
        {
            // Current layout is EN -> typed form is the EN spelling.
            ConvertedLanguage.English => _userDictionary.HasEnglishPrefix(typed),
            ConvertedLanguage.Russian => _userDictionary.HasRussianPrefix(typed),
            _ => false
        };
    }

    private bool UserDictionaryGuardInTargetLayout(string converted, ConvertedLanguage systemLanguage)
    {
        return systemLanguage switch
        {
            // Target is RU -> the converted form is the RU spelling.
            ConvertedLanguage.English => _userDictionary.HasRussianPrefix(converted),
            ConvertedLanguage.Russian => _userDictionary.HasEnglishPrefix(converted),
            _ => false
        };
    }

    public static string TrimNonLetters(string text)
    {
        return TextNormalizer.TrimToWord(text);
    }

    public static ConvertedLanguage Oppose(ConvertedLanguage language)
    {
        return language == ConvertedLanguage.Russian
            ? ConvertedLanguage.English
            : (language == ConvertedLanguage.English ? ConvertedLanguage.Russian : ConvertedLanguage.Unknown);
    }

    // True when every character is a letter (no punctuation, digits, spaces).
    // Used by the live-oscillation guard: a pure-letter token must convert to a
    // pure-letter token, otherwise the layout map has injected a non-letter at
    // an edge (e.g. RU 'б' -> EN ',') and trimming it would create a phantom
    // symmetric trigger pair.
    private static bool IsAllLetters(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (char character in text)
        {
            if (!char.IsLetter(character))
            {
                return false;
            }
        }

        return true;
    }

    // "iPhone", "camelCase", "McDonald": an uppercase letter after a
    // lowercase one is typical for names and identifiers, not for a word
    // typed in the wrong layout. ALL-CAPS and Capitalised words are fine.
    private static bool HasInnerUppercase(string core)
    {
        for (int index = 1; index < core.Length; index++)
        {
            if (char.IsUpper(core[index]) && char.IsLower(core[index - 1]))
            {
                return true;
            }
        }

        return false;
    }

    // URLs, e-mail addresses and file-system/URL paths. The checks only look at
    // characters that are not produced by a single layout letter key, so a
    // wrong-layout Russian word cannot accidentally match them.
    private static bool LooksLikeAddress(string text)
    {
        if (text.Contains("://", StringComparison.Ordinal) ||
            text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int at = text.IndexOf('@');
        return at > 0 && at < text.Length - 1 &&
               char.IsLetterOrDigit(text[at - 1]) && char.IsLetterOrDigit(text[at + 1]);
    }

    private static (string Core, string LeadingEdge, string TrailingEdge) GetWordEdgeInfo(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (string.Empty, string.Empty, string.Empty);
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

        return (
            text.Substring(start, end - start),
            text.Substring(0, start),
            text.Substring(end));
    }

    private static bool ShouldSuppressConvertedEdgeLookup(
        string visibleText,
        ConvertedLanguage visibleLanguage,
        ConvertedLanguage candidateLanguage,
        string candidateForm,
        string candidateLookup)
    {
        if (visibleLanguage == candidateLanguage || candidateForm.Length == candidateLookup.Length)
        {
            return false;
        }

        var visibleEdges = GetWordEdgeInfo(visibleText);
        if (!IsAllLetters(visibleEdges.Core))
        {
            return false;
        }

        var candidateEdges = GetWordEdgeInfo(candidateForm);
        if (IsAllowedConvertedWrapper(candidateEdges.LeadingEdge, candidateEdges.TrailingEdge))
        {
            return false;
        }

        return !string.Equals(candidateEdges.LeadingEdge, visibleEdges.LeadingEdge, StringComparison.Ordinal) ||
               !string.Equals(candidateEdges.TrailingEdge, visibleEdges.TrailingEdge, StringComparison.Ordinal);
    }

    private static bool ShouldSuppressConvertedUserDictionaryLookup(
        string visibleText,
        ConvertedLanguage visibleLanguage,
        ConvertedLanguage candidateLanguage,
        string candidateForm,
        string candidateLookup)
    {
        if (visibleLanguage == candidateLanguage || candidateForm.Length == candidateLookup.Length)
        {
            return false;
        }

        var visibleEdges = GetWordEdgeInfo(visibleText);
        if (!IsAllLetters(visibleEdges.Core))
        {
            return false;
        }

        var candidateEdges = GetWordEdgeInfo(candidateForm);
        if (IsAllowedConvertedWrapper(candidateEdges.LeadingEdge, candidateEdges.TrailingEdge))
        {
            return false;
        }

        // User words may keep trailing punctuation from the wrong layout
        // ("пшерги," -> "github?"), but a generated leading edge usually means
        // the visible word started with a normal Russian letter that maps to a
        // symbol ("\"было\"" -> "@,skj@"). Do not trust the trimmed lookup there.
        return candidateEdges.LeadingEdge.Length > 0;
    }

    private static bool IsAllowedConvertedWrapper(string leadingEdge, string trailingEdge)
    {
        return (leadingEdge, trailingEdge) switch
        {
            ("\"", "\"") => true,
            ("'", "'") => true,
            ("`", "`") => true,
            ("[", "]") => true,
            ("{", "}") => true,
            ("(", ")") => true,
            ("<", ">") => true,
            _ => false
        };
    }
}

internal readonly record struct LiveDecision(bool Hit, string Converted, ConvertedLanguage Target, string Source)
{
    public static LiveDecision Miss => new(false, string.Empty, ConvertedLanguage.Unknown, string.Empty);
}

/// <summary>
/// Thresholds of the n-gram and short-word layers. N-gram values are in nats
/// per letter transition RELATIVE to the language's typical score for a word
/// of the same length (0 = a normal word, -1 = clearly unusual). Calibrated
/// with tools/LayoutFixer.Tools on independent frequency lists for a
/// near-zero false-positive rate; see tools/LayoutFixer.Tools/README.md.
/// </summary>
internal sealed class NgramDecisionOptions
{
    public bool FinalEnabled { get; set; } = true;

    public int FinalMinLength { get; set; } = 3;

    // How much more "normal" the converted spelling must read.
    public double FinalMargin { get; set; } = 1.2;

    // The converted spelling itself must not be far below a normal word.
    public double FinalMinTarget { get; set; } = -1.5;

    public int FinalMaxTargetUnseen { get; set; } = 1;

    // Relative score used for a visible form the model cannot read at all,
    // e.g. Latin text with layout symbols inside the word ("j,]zdktybt").
    public double InvalidVisibleScore { get; set; } = -4.5;

    // A complete visible word at least this normal, with every trigram
    // attested and no better converted reading, is kept as typed and the
    // weaker affix fallback is not consulted.
    public double KeepMinVisible { get; set; } = -0.3;

    public bool LiveEnabled { get; set; } = true;

    public int LiveMinLength { get; set; } = 4;

    public double LiveMargin { get; set; } = 2.5;

    public double LiveMinTarget { get; set; } = -0.3;

    public int LiveMaxTargetUnseen { get; set; }

    public int LiveMinVisibleUnseen { get; set; } = 2;

    // Live waits for the final stage when the typed prefix is itself a common
    // word of the current language (rank in the short-word table).
    public int LiveHoldRank { get; set; } = 5000;

    // Live trigger hits are ignored when the visible prefix reads as a normal
    // prefix of the current language, at most this much less normal than the
    // converted prefix.
    public bool LiveVetoEnabled { get; set; } = true;

    public int LiveVetoMinLength { get; set; } = 2;

    public double LiveVetoMargin { get; set; } = 1.0;

    // The affix fallback may only convert a word when the converted spelling
    // reads at least this much more normally (nats per letter).
    public double AffixMinAdvantage { get; set; } = 0.8;

    public bool ShortWordsEnabled { get; set; } = true;

    // ln(rank ratio) the converted short word must win by: ln(30) ~ 3.4.
    public double ShortMargin { get; set; } = 3.4;

    // ALL-CAPS short tokens (abbreviations): extra evidence needed.
    public double ShortAbbreviationPenalty { get; set; } = 3.0;

    // Previous word typed in the same layout: much stronger evidence needed.
    public double ShortSameContextPenalty { get; set; } = 4.0;

    // Previous word already in the target language: weaker evidence suffices.
    public double ShortTargetContextBonus { get; set; } = 2.0;
}
