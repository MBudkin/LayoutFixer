using System;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Input;

internal sealed class ConversionService
{
    private static readonly char[] WordSeparators = { ' ', '\t', '\r', '\n' };

    private readonly KeyboardBuffer _buffer;
    private readonly LayoutConverter _converter = new();
    private readonly LayoutDecisionEngine _engine;
    private readonly UserReplacements _replacements;
    private readonly Func<bool> _respectReplacementPunctuation;
    private LastConversion? _lastConversion;
    private LastCapitalization? _lastCapitalization;

    // Per-word flag: once a live (mid-word) trigger has fired, the rest of the
    // word was already typed in the new layout and must NOT be reprocessed by a
    // final trigger or the affix fallback. Cleared on the next terminator, on
    // buffer resets (ClearUndo), or when the user edits the word with a tracked
    // Backspace/Delete so the corrected continuation can be evaluated again.
    private bool _currentWordHadLiveTrigger;

    // The visible text the user explicitly chose via the manual hotkey
    // (Pause/Break): the original restored by a revert, or the result of a
    // manual conversion. In normalized form. While the buffer's last word
    // equals this, auto-conversion (live OR final) must NOT touch it again —
    // otherwise the next separator flips the word right back. Moved to
    // _previousWordLock as soon as the user starts a different word, cleared
    // on any untrackable reset.
    private string? _revertedWord;

    // The user-chosen word typed right BEFORE the current one (see above).
    // Keeps the single-letter look-back from overriding it: after the user
    // turned "я" back into "z", "z levf" must not become "я думаю".
    private string? _previousWordLock;

    public ConversionService(
        KeyboardBuffer buffer,
        LayoutDecisionEngine engine,
        UserReplacements replacements,
        Func<bool> respectReplacementPunctuation)
    {
        _buffer = buffer;
        _engine = engine;
        _replacements = replacements;
        _respectReplacementPunctuation = respectReplacementPunctuation;
    }

    public static bool ContainsLetter(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (char character in text)
        {
            if (char.IsLetter(character))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsDigit(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (char character in text)
        {
            if (char.IsDigit(character))
            {
                return true;
            }
        }

        return false;
    }

    public ConversionOutcome ConvertSelection(string selection)
    {
        Logger.Log("=== ConvertSelection START ===");
        Logger.Log(Logger.Format("selection", selection));

        if (string.IsNullOrEmpty(selection) || !_converter.CanConvert(selection))
        {
            Logger.Log("ConvertSelection: no convertible selection");
            return ConversionOutcome.From(ConversionResult.NoConvertibleWord);
        }

        string converted = _converter.Convert(selection);
        var language = _converter.DetectLanguage(converted);

        Logger.Log(Logger.Format("converted", converted));
        Logger.Log($"language={language} layoutBefore=0x{LayoutProbe.GetCurrentLayoutId():X}");

        if (!SwitchLayoutPreservingForeground(language, nameof(ConvertSelection)))
        {
            return ConversionOutcome.From(ConversionResult.SendFailed);
        }

        Logger.Log($"layoutAfter=0x{LayoutProbe.GetCurrentLayoutId():X}");

        if (!TextInputSender.SendString(converted))
        {
            Logger.Log("ConvertSelection SendString FAILED");
            return ConversionOutcome.From(ConversionResult.SendFailed);
        }

        Logger.Log("ConvertSelection SendString OK");

        // Note: selection conversion intentionally does NOT seed the keyboard
        // buffer or record a "last conversion". Otherwise the next last-word
        // hotkey would treat the whole converted selection as a toggle target and
        // revert/convert it, dragging in unrelated text across line breaks.
        bool singleWord = !string.IsNullOrWhiteSpace(selection) &&
                          selection.IndexOfAny(WordSeparators) < 0;

        return new ConversionOutcome
        {
            Result = ConversionResult.Success,
            OriginalText = selection,
            ConvertedText = converted,
            Language = language,
            IsSingleWord = singleWord
        };
    }

    public ConversionOutcome ConvertLastWord()
    {
        Logger.Log("=== ConvertLastWord START ===");
        Logger.Log(Logger.Format("buffer", _buffer.GetSnapshot()));

        // Auto-capitalization is an independent layer above layout conversion.
        // Break removes that layer first, leaving the previous layout state in
        // place so the next Break continues with the normal layout toggle.
        if (TryUndoAutoCapitalization(out ConversionOutcome capitalizationUndo))
        {
            return capitalizationUndo;
        }

        if (_lastConversion is not null && _buffer.TailEquals(_lastConversion.Converted))
        {
            Logger.Log("Toggle branch: reverting");
            LastConversion snapshot = _lastConversion;
            ConvertedLanguage originalLanguage = OpposeLanguage(snapshot.Language);
            ConversionResult revertResult = Revert(snapshot);
            return new ConversionOutcome
            {
                Result = revertResult,
                WasRevert = revertResult == ConversionResult.Success,
                // On revert the "correct" word is the restored original.
                OriginalText = snapshot.OriginalWord,
                ConvertedText = snapshot.OriginalWord,
                Language = originalLanguage,
                IsSingleWord = true,
                FromAuto = snapshot.FromAuto
            };
        }

        if (!_buffer.TryGetLastToken(out BufferToken token))
        {
            Logger.Log("No convertible word");
            return ConversionOutcome.From(ConversionResult.NoConvertibleWord);
        }

        // When the token carries letters, DetectLanguage reliably picks the
        // conversion direction from those letters. But a letter-less token
        // (single punctuation/sign, e.g. ":" from Shift+6) defeats DetectLanguage,
        // which would fall back to EN->RU and turn a Russian ":" into "Ж" instead
        // of "^". For such tokens, take the conversion direction from the CURRENT
        // system layout: a Russian layout means the sign was typed here, so
        // convert RU->EN (and vice versa). This is only safe for the last typed
        // token (Break), where the current layout matches how it was typed.
        bool hasLetters = ContainsLetter(token.Text);
        ConvertedLanguage sourceLanguage = ConvertedLanguage.Unknown;
        if (!hasLetters)
        {
            sourceLanguage = LanguageFromSystemLayout();
        }

        if (!_converter.CanConvert(token.Text, sourceLanguage))
        {
            Logger.Log(Logger.Format("tokenText", token.Text));
            Logger.Log("No convertible word");
            return ConversionOutcome.From(ConversionResult.NoConvertibleWord);
        }

        string converted = _converter.Convert(token.Text, sourceLanguage);
        string replacement = converted + token.Trailing;
        // The result language drives the system layout switch. For a letter-less
        // result DetectLanguage returns Unknown and the layout would not switch,
        // so derive it as the opposite of the source we just converted from.
        ConvertedLanguage language = hasLetters
            ? _converter.DetectLanguage(converted)
            : OpposeLanguage(sourceLanguage);

        Logger.Log(Logger.Format("tokenText", token.Text));
        Logger.Log(Logger.Format("tokenTrailing", token.Trailing));
        Logger.Log(Logger.Format("converted", converted));
        Logger.Log(Logger.Format("replacement", replacement));
        Logger.Log($"originalLength={token.OriginalLength} language={language}");
        Logger.Log($"layoutBefore=0x{LayoutProbe.GetCurrentLayoutId():X}");

        if (!SwitchLayoutPreservingForeground(language, nameof(ConvertLastWord)))
        {
            return ConversionOutcome.From(ConversionResult.SendFailed);
        }

        Logger.Log($"layoutAfter=0x{LayoutProbe.GetCurrentLayoutId():X}");

        if (!TextInputSender.ReplaceTail(token.OriginalLength, replacement))
        {
            Logger.Log("ReplaceTail FAILED");
            return ConversionOutcome.From(ConversionResult.SendFailed);
        }

        Logger.Log("ReplaceTail OK");
        _buffer.ReplaceTail(token.OriginalLength, replacement);
        _lastCapitalization = null;
        Logger.Log(Logger.Format("bufferAfter", _buffer.GetSnapshot()));
        _lastConversion = new LastConversion(token.OriginalSegment, replacement, language, DateTimeOffset.UtcNow, token.Text, converted, FromAuto: false);
        // The user picked this spelling by hand: the separator typed next must
        // not convert it back (same lock as after a revert).
        _revertedWord = TextNormalizer.TrimToWord(converted);
        return new ConversionOutcome
        {
            Result = ConversionResult.Success,
            OriginalText = token.Text,
            ConvertedText = converted,
            Language = language,
            IsSingleWord = true,
            AlreadyRecognized = hasLetters && IsRecognizedByEngine(token.Text, language)
        };
    }

    // True when the final decision alone (without sentence context) converts
    // `typed` into `language`. Uses the case the user meant: with Caps Lock
    // "dSGBIB" is judged as "Dsgbib".
    private bool IsRecognizedByEngine(string typed, ConvertedLanguage language)
    {
        string text = TextTransformationService.NormalizeAccidentalCapsLock(typed);
        bool recognized = _engine.TryFinalDecision(text, null, out ConvertedLanguage target) && target == language;
        if (recognized)
        {
            Logger.Log($"ConvertLastWord: the word is already recognized automatically ({_engine.LastFinalSource})");
        }

        return recognized;
    }

    public ConversionResult ApplyReplacement()
    {
        Logger.Log("=== ApplyReplacement START ===");

        if (!_replacements.HasAny)
        {
            return ConversionResult.Skipped;
        }

        if (!_buffer.TryGetLastToken(out BufferToken token) || token.Text.Length == 0)
        {
            return ConversionResult.Skipped;
        }

        // When "respect punctuation" is on, surrounding punctuation is treated as
        // a separator: "спс," looks up "спс" and re-applies the trailing comma to
        // the expansion ("спасибо,"). Layout punctuation that maps to a Russian
        // letter ("," -> "б") is NOT a buffer separator, so without this the
        // whole "спс," token would miss the trigger.
        bool respectPunctuation = _respectReplacementPunctuation();
        string? expansion = _replacements.Find(token.Text, respectPunctuation, out string prefix, out string suffix);
        if (string.IsNullOrEmpty(expansion))
        {
            return ConversionResult.Skipped;
        }

        string replacement = prefix + expansion + suffix + token.Trailing;
        Logger.Log(Logger.Format("trigger", token.Text));
        Logger.Log(Logger.Format("expansion", expansion));
        Logger.Log(Logger.Format("prefix", prefix));
        Logger.Log(Logger.Format("suffix", suffix));
        Logger.Log(Logger.Format("replacement", replacement));

        if (!TextInputSender.ReplaceTail(token.OriginalLength, replacement))
        {
            Logger.Log("ApplyReplacement ReplaceTail FAILED");
            return ConversionResult.SendFailed;
        }

        _buffer.ReplaceTail(token.OriginalLength, replacement);
        _lastCapitalization = null;
        Logger.Log("ApplyReplacement OK");
        // Replacements are plain text (typed via Unicode), so no layout switch is
        // needed and no last-conversion is recorded (Break won't revert them).
        return ConversionResult.Success;
    }

    public ConversionResult AutoCapitalizeLastWord()
    {
        Logger.Log("=== AutoCapitalizeLastWord START ===");

        if (!_buffer.IsLastTokenAfterSentenceTerminator() ||
            !_buffer.TryGetLastToken(out BufferToken token) ||
            token.Text.Length == 0)
        {
            Logger.Log("AutoCapitalizeLastWord: not a sentence start");
            return ConversionResult.Skipped;
        }

        string capitalized = TextTransformationService.CapitalizeFirstLetter(token.Text);
        if (string.Equals(capitalized, token.Text, StringComparison.Ordinal))
        {
            Logger.Log("AutoCapitalizeLastWord: first letter is already uppercase");
            return ConversionResult.Skipped;
        }

        string replacement = capitalized + token.Trailing;
        Logger.Log(Logger.Format("tokenText", token.Text));
        Logger.Log(Logger.Format("capitalized", capitalized));

        if (!TextInputSender.ReplaceTail(token.OriginalLength, replacement))
        {
            Logger.Log("AutoCapitalizeLastWord ReplaceTail FAILED");
            return ConversionResult.SendFailed;
        }

        _buffer.ReplaceTail(token.OriginalLength, replacement);
        _lastCapitalization = new LastCapitalization(token.Text, capitalized);
        Logger.Log("AutoCapitalizeLastWord OK");
        Logger.Log(Logger.Format("bufferAfter", _buffer.GetSnapshot()));
        return ConversionResult.Success;
    }

    public ConversionResult FixAccidentalCapsLock()
    {
        Logger.Log("=== FixAccidentalCapsLock START ===");
        if (!IsCapsLockOn())
        {
            Logger.Log("FixAccidentalCapsLock: Caps Lock is off");
            return ConversionResult.Skipped;
        }

        if (!_buffer.TryGetLastToken(out BufferToken token) || token.Text.Length == 0)
        {
            return ConversionResult.Skipped;
        }

        string normalized = TextTransformationService.NormalizeAccidentalCapsLock(token.Text);
        if (string.Equals(normalized, token.Text, StringComparison.Ordinal))
        {
            Logger.Log("FixAccidentalCapsLock: token does not match");
            return ConversionResult.Skipped;
        }

        string replacement = normalized + token.Trailing;
        Logger.Log(Logger.Format("tokenText", token.Text));
        Logger.Log(Logger.Format("normalized", normalized));
        LastConversion? conversion = _lastConversion;
        bool fixesLastConversion = conversion is not null && _buffer.TailEquals(conversion.Converted);
        if (!TextInputSender.ReplaceTail(token.OriginalLength, replacement))
        {
            return ConversionResult.SendFailed;
        }

        _buffer.ReplaceTail(token.OriginalLength, replacement);
        _lastCapitalization = null;
        if (fixesLastConversion)
        {
            // "dSGBIB" was just converted to "вЫПИШИ" and is now "Выпиши".
            // Break must still undo the layout fix, to "Dsgbib": Caps Lock is
            // off now, so the typed case would be wrong too.
            _lastConversion = WithNormalizedCase(conversion!, token, replacement);
        }
        if (!TextInputSender.TurnOffCapsLock())
        {
            // The visible text is already corrected. Do not report SendFailed
            // and risk a second replacement; leave a diagnostic record instead.
            Logger.Log("FixAccidentalCapsLock: text fixed, Caps Lock toggle failed");
        }

        Logger.Log(Logger.Format("bufferAfter", _buffer.GetSnapshot()));
        return ConversionResult.Success;
    }

    private static LastConversion? WithNormalizedCase(LastConversion conversion, BufferToken token, string replacement)
    {
        string originalTail = conversion.OriginalWord + token.Trailing;
        if (!conversion.Original.EndsWith(originalTail, StringComparison.Ordinal) ||
            !conversion.Converted.EndsWith(token.OriginalSegment, StringComparison.Ordinal))
        {
            return null;
        }

        string originalWord = TextTransformationService.NormalizeAccidentalCapsLock(conversion.OriginalWord);
        return conversion with
        {
            Original = conversion.Original[..^originalTail.Length] + originalWord + token.Trailing,
            Converted = conversion.Converted[..^token.OriginalLength] + replacement,
            OriginalWord = originalWord,
            ConvertedWord = TextNormalizer.TrimToWord(replacement)
        };
    }

    // "ПРивет" -> "Привет". Recorded as a case layer, so the first Break
    // restores the typed spelling exactly like the sentence capitalisation.
    public ConversionResult FixTwoInitialCapitals()
    {
        Logger.Log("=== FixTwoInitialCapitals START ===");
        if ((NativeMethods.GetKeyState(NativeMethods.VkCapital) & 1) != 0)
        {
            // With Caps Lock on, "пРИВЕТ" handling is FixAccidentalCapsLock's job.
            return ConversionResult.Skipped;
        }

        if (!_buffer.TryGetLastToken(out BufferToken token) || token.Text.Length == 0)
        {
            return ConversionResult.Skipped;
        }

        string fixedText = TextTransformationService.FixTwoInitialCapitals(token.Text);
        if (string.Equals(fixedText, token.Text, StringComparison.Ordinal))
        {
            return ConversionResult.Skipped;
        }

        string replacement = fixedText + token.Trailing;
        Logger.Log(Logger.Format("tokenText", token.Text));
        Logger.Log(Logger.Format("fixed", fixedText));
        if (!TextInputSender.ReplaceTail(token.OriginalLength, replacement))
        {
            return ConversionResult.SendFailed;
        }

        _buffer.ReplaceTail(token.OriginalLength, replacement);
        _lastCapitalization = new LastCapitalization(token.Text, fixedText);
        Logger.Log(Logger.Format("bufferAfter", _buffer.GetSnapshot()));
        return ConversionResult.Success;
    }

    public ConversionResult TransformLastWord(
        TextTransformationKind kind,
        bool invertEachCharacterCase = false)
    {
        Logger.Log($"=== TransformLastWord START kind={kind} ===");
        if (!_buffer.TryGetLastToken(out BufferToken token) || token.Text.Length == 0)
        {
            return ConversionResult.NoConvertibleWord;
        }

        string transformed = TextTransformationService.Transform(
            token.Text,
            kind,
            invertEachCharacterCase);
        if (string.Equals(transformed, token.Text, StringComparison.Ordinal))
        {
            return ConversionResult.NoConvertibleWord;
        }

        string replacement = transformed + token.Trailing;
        Logger.Log(Logger.Format("tokenText", token.Text));
        Logger.Log(Logger.Format("transformed", transformed));
        if (!TextInputSender.ReplaceTail(token.OriginalLength, replacement))
        {
            return ConversionResult.SendFailed;
        }

        _buffer.ReplaceTail(token.OriginalLength, replacement);
        ClearTextTransformationState();
        Logger.Log(Logger.Format("bufferAfter", _buffer.GetSnapshot()));
        return ConversionResult.Success;
    }

    public ConversionResult TransformSelection(
        string selection,
        TextTransformationKind kind,
        bool invertEachCharacterCase = false)
    {
        Logger.Log($"=== TransformSelection START kind={kind} ===");
        if (string.IsNullOrEmpty(selection))
        {
            return ConversionResult.NoConvertibleWord;
        }

        string transformed = TextTransformationService.Transform(
            selection,
            kind,
            invertEachCharacterCase);
        if (string.Equals(transformed, selection, StringComparison.Ordinal))
        {
            return ConversionResult.NoConvertibleWord;
        }

        Logger.Log(Logger.Format("selection", selection));
        Logger.Log(Logger.Format("transformed", transformed));
        if (!TextInputSender.SendString(transformed))
        {
            return ConversionResult.SendFailed;
        }

        // A selection may live anywhere in the document. The keyboard buffer can
        // no longer claim to mirror the caret after replacing it.
        _buffer.Clear();
        ClearUndo();
        return ConversionResult.Success;
    }

    // LIVE conversion: called from the low-level keyboard hook on every typable,
    // non-terminator keystroke. Runs synchronously on the hook thread, so by the
    // time it returns the buffer is consistent with the typed text. Returns true
    // when it actually performed a conversion (so the caller can play a sound).
    public bool TryLiveConvert()
    {
        // A live trigger fires at most once per word: after it, the rest of the
        // word is typed in the new layout and any further live checks would be
        // meaningless (the buffer already holds the converted text).
        if (_currentWordHadLiveTrigger)
        {
            return false;
        }

        if (!_buffer.TryGetLastToken(out BufferToken token) || token.Text.Length == 0)
        {
            return false;
        }

        // If the user reverted this exact word via the manual hotkey, do not
        // auto-convert it again — they already rejected the conversion once.
        // The guard stays until the word changes.
        if (IsRevertedWord(token.Text))
        {
            return false;
        }

        // The word differs from the reverted one: the user edited it or
        // started the next word, so the revert lock no longer applies here.
        ReleaseRevertLock();

        // Punctuation/symbol-only tokens must never be live-fixed (they often
        // map to valid single letters). Words glued to digits are left for the
        // final stage too.
        if (!ContainsLetter(token.Text) || ContainsDigit(token.Text))
        {
            return false;
        }

        ConvertedLanguage systemLanguage = LanguageFromSystemLayout();
        if (systemLanguage == ConvertedLanguage.Unknown)
        {
            return false;
        }

        LiveDecision decision = _engine.DecideLive(token.Text, systemLanguage);
        if (!decision.Hit)
        {
            return false;
        }

        return PerformLiveReplace(token, decision.Converted, decision.Target);
    }

    // Shared tail of TryLiveConvert: sends the replacement, switches layout,
    // updates the buffer, records the conversion. Extracted so both the
    // user-dict path and the trigger path share the exact same mechanics
    // (including the N-1 backspace / keystroke-suppression fix).
    private bool PerformLiveReplace(BufferToken token, string converted, ConvertedLanguage target)
    {
        // "z lev" -> "я дум": a single letter kept by the sentence context
        // changes layout together with the word that follows it.
        TryGetPrecedingLetter(target, _previousWordLock, out string letterTyped, out string letterFixed);
        string replacement = letterFixed + converted;
        Logger.Log("=== TryLiveConvert HIT ===");
        Logger.Log(Logger.Format("tokenText", token.Text));
        Logger.Log(Logger.Format("converted", converted));
        Logger.Log(Logger.Format("replacement", replacement));
        Logger.Log($"target={target} layoutBefore=0x{LayoutProbe.GetCurrentLayoutId():X}");

        // CRITICAL: the low-level keyboard hook fires BEFORE the keystroke is
        // delivered to the focused window. By the time we get here the buffer
        // already contains the triggering keystroke, but the application does
        // NOT — that last character has not been typed yet. So we must:
        //   * send N-1 backspaces to the app (only what is actually on screen),
        //   * then send the full converted text,
        //   * and the caller MUST suppress the triggering keystroke, otherwise
        //     the OS would still deliver it on top of our replacement, producing
        //     a duplicate trailing character (e.g. "руддщ" -> "helllo").
        int appCharsOnScreen = letterTyped.Length + token.OriginalLength - 1;
        if (appCharsOnScreen < 0)
        {
            appCharsOnScreen = 0;
        }

        // ReplaceTail deletes exactly appCharsOnScreen characters and types the
        // converted text. Use a dedicated path so the buffer (which holds all N)
        // is updated separately below with the full length.
        if (!TextInputSender.ReplaceTail(appCharsOnScreen, replacement))
        {
            Logger.Log("TryLiveConvert ReplaceTail FAILED");
            return false;
        }

        // Bring the buffer in sync: remove everything the token covered (all N
        // chars, including the still-undelivered triggering key) and append the
        // converted text. After this the buffer matches what is now on screen.
        _buffer.ReplaceTail(letterTyped.Length + token.OriginalLength, replacement);
        LayoutSwitcher.PostSwitchTo(target);
        _currentWordHadLiveTrigger = true;
        _lastCapitalization = null;

        Logger.Log("TryLiveConvert OK");
        Logger.Log(Logger.Format("bufferAfter", _buffer.GetSnapshot()));
        // Record the conversion so the manual hotkey can still toggle/revert it,
        // and so the learn prompt works. FromAuto marks it as automatic.
        _lastConversion = new LastConversion(letterTyped + token.OriginalSegment, replacement, target, DateTimeOffset.UtcNow, token.Text, converted, FromAuto: true);
        return true;
    }

    // FINAL conversion: called from the terminator (Space/Enter/Tab/-) path on a
    // background task. Uses the conservative foreground-checked switch.
    public ConversionResult FinalConvert()
    {
        Logger.Log("=== FinalConvert START ===");

        // The lock of the word before this one matters only for this word.
        string? previousLock = _previousWordLock;
        _previousWordLock = null;

        // If the word already had a live trigger, the remainder was typed in the
        // correct layout and MUST NOT be reprocessed. Clear the flag for the next
        // word and bail out.
        if (_currentWordHadLiveTrigger)
        {
            Logger.Log("FinalConvert: word had a live trigger -> skip final");
            _currentWordHadLiveTrigger = false;
            return ConversionResult.Skipped;
        }

        // A third layout (Ukrainian, German, ...) is active: the text was typed
        // in it on purpose, and switching to RU/EN would only get in the way.
        if (LanguageFromSystemLayout() == ConvertedLanguage.Unknown)
        {
            Logger.Log("FinalConvert: current layout is neither Russian nor English -> skip");
            return ConversionResult.Skipped;
        }

        if (!_buffer.TryGetLastToken(out BufferToken token) || !_converter.CanConvert(token.Text))
        {
            Logger.Log(Logger.Format("tokenText", token.Text));
            Logger.Log("FinalConvert: no convertible word");
            return ConversionResult.Skipped;
        }

        // If the user reverted this exact word via the manual hotkey, do not
        // auto-convert it again. This is what stops the loop where a live-
        // converted "рудд" -> "hell" is reverted to "рудд", and then the next
        // terminator re-runs the affix/trigger logic and converts it back.
        if (IsRevertedWord(token.Text))
        {
            Logger.Log("FinalConvert: word was reverted by the user -> keep as typed");
            return ConversionResult.Skipped;
        }

        // Different word -> the revert lock belongs to the word before it.
        previousLock = _revertedWord ?? previousLock;
        _revertedWord = null;

        // Words glued to digits and letter-less tokens are not auto-fixable
        // (manual Pause still works for them).
        if (ContainsDigit(token.Text) || !ContainsLetter(token.Text))
        {
            Logger.Log("FinalConvert: token has digits or no letters -> keep as typed");
            return ConversionResult.Skipped;
        }

        // With Caps Lock on, "dSGBIB" is "Dsgbib" typed with the wrong case,
        // not a "VkShift"-style identifier: judge the word in the case the
        // user meant. The replacement keeps the typed case; FixAccidentalCapsLock
        // runs right after and fixes it ("вЫПИШИ" -> "Выпиши").
        string decisionText = token.Text;
        if (IsCapsLockOn())
        {
            decisionText = TextTransformationService.NormalizeAccidentalCapsLock(token.Text);
        }

        if (!_engine.TryFinalDecision(
                decisionText,
                _buffer.GetWordBeforeLastToken(),
                _buffer.GetSentenceBeforeLastToken(),
                out ConvertedLanguage target))
        {
            Logger.Log("FinalConvert: no decision, keeping as typed");
            return ConversionResult.Skipped;
        }

        // `target` is the language we want the result to be in. Convert from the
        // opposite direction so the output is in `target`.
        string converted = ConvertInDirection(token.Text, target);
        string replacement = converted + token.Trailing;

        // "hello z levf" -> "hello я думаю": the single letter kept by the
        // sentence context is fixed together with the word that follows it.
        TryGetPrecedingLetter(target, previousLock, out string letterTyped, out string letterFixed);
        int replacedLength = letterTyped.Length + token.OriginalLength;
        replacement = letterFixed + replacement;

        Logger.Log(Logger.Format("tokenText", token.Text));
        Logger.Log(Logger.Format("tokenTrailing", token.Trailing));
        Logger.Log(Logger.Format("converted", converted));
        Logger.Log(Logger.Format("replacement", replacement));
        Logger.Log($"originalLength={token.OriginalLength} target={target}");
        Logger.Log($"layoutBefore=0x{LayoutProbe.GetCurrentLayoutId():X}");

        // "Claude b ChatGPT": the letter becomes "и", but the layout stays
        // English for the next English term (see TermLetterSource).
        bool keepLayout = letterTyped.Length == 0 &&
                          _engine.LastFinalSource == LayoutDecisionEngine.TermLetterSource;
        if (keepLayout)
        {
            Logger.Log("FinalConvert: single letter among English terms -> keep the layout");
        }
        else if (!SwitchLayoutPreservingForeground(target, nameof(FinalConvert)))
        {
            return ConversionResult.SendFailed;
        }

        Logger.Log($"layoutAfter=0x{LayoutProbe.GetCurrentLayoutId():X}");

        if (!TextInputSender.ReplaceTail(replacedLength, replacement))
        {
            Logger.Log("FinalConvert ReplaceTail FAILED");
            return ConversionResult.SendFailed;
        }

        Logger.Log("FinalConvert ReplaceTail OK");
        _buffer.ReplaceTail(replacedLength, replacement);
        _lastCapitalization = null;
        Logger.Log(Logger.Format("bufferAfter", _buffer.GetSnapshot()));
        _lastConversion = new LastConversion(letterTyped + token.OriginalSegment, replacement, target, DateTimeOffset.UtcNow, token.Text, converted, FromAuto: true);
        return ConversionResult.Success;
    }

    // The separator pipeline already ran for the last word when an earlier
    // separator follows it ("word  ", "word -"). Running it again would
    // re-decide a word the user may have fixed by hand since then: "it ",
    // Break -> "ше ", Space must not turn it back into "it".
    public bool IsLastWordAlreadyTerminated()
    {
        return _buffer.TryGetLastToken(out BufferToken token) && token.HasEarlierTerminator;
    }

    // A single-letter word right before the last token, separated by spaces
    // only, that should change layout together with it. `typed` is the text
    // to replace (letter + spaces), `fixedText` its replacement; both are
    // empty when there is nothing to fix.
    private bool TryGetPrecedingLetter(
        ConvertedLanguage target,
        string? lockedWord,
        out string typed,
        out string fixedText)
    {
        typed = string.Empty;
        fixedText = string.Empty;
        if (!_buffer.TryGetTokenBeforeLastToken(out string word, out string gap) ||
            gap.Length == 0 ||
            gap.Trim(' ').Length != 0)
        {
            return false;
        }

        if (lockedWord is not null && string.Equals(lockedWord, word, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Log($"Preceding letter '{word}' was chosen by the user -> keep");
            return false;
        }

        if (!_engine.TryConvertPrecedingLetter(word, target, out string letter))
        {
            return false;
        }

        Logger.Log($"Preceding letter '{word}' -> '{letter}' together with the converted word");
        typed = word + gap;
        fixedText = letter + gap;
        return true;
    }

    private void ReleaseRevertLock()
    {
        if (_revertedWord is not null)
        {
            _previousWordLock = _revertedWord;
            _revertedWord = null;
        }
    }

    private static bool IsCapsLockOn()
    {
        return (NativeMethods.GetKeyState(NativeMethods.VkCapital) & 1) != 0;
    }

    // Converts text so that the result is in `targetLanguage`. The caller knows
    // the desired target, so we convert from the opposite layout. DetectLanguage
    // is intentionally not used here: the decision step already resolved the
    // direction, and a mixed-script token could confuse detection.
    private string ConvertInDirection(string text, ConvertedLanguage targetLanguage)
    {
        ConvertedLanguage source = targetLanguage == ConvertedLanguage.Russian
            ? ConvertedLanguage.English
            : ConvertedLanguage.Russian;
        return _converter.Convert(text, source);
    }

    public ConversionResult UndoLastConversion()
    {
        Logger.Log("=== UndoLastConversion ===");
        if (_lastConversion is null)
        {
            Logger.Log("No last conversion");
            return ConversionResult.NoConvertibleWord;
        }

        if (_buffer.TailEquals(_lastConversion.Converted))
        {
            return Revert(_lastConversion);
        }

        Logger.Log("Buffer tail does not match last conversion");
        return ConversionResult.NoConvertibleWord;
    }

    public ConversionResult ToggleLastWord()
    {
        return ConvertLastWord().Result;
    }

    public ConvertedLanguage LastConversionLanguage
    {
        get
        {
            ConvertedLanguage language = ConvertedLanguage.Unknown;
            LastConversion? snapshot = _lastConversion;
            if (snapshot is not null)
            {
                language = snapshot.Language;
            }

            return language;
        }
    }

    public bool IsNextConversionAToggle =>
        _lastConversion is not null && _buffer.TailEquals(_lastConversion.Converted);

    public void ClearUndo()
    {
        _lastConversion = null;
        _lastCapitalization = null;
        // A buffer reset starts a new word context: any live-trigger state from
        // the abandoned word must not leak into the next one.
        _currentWordHadLiveTrigger = false;
        // The user moved on (navigation, click, paste, Ctrl+Backspace, ...).
        // Forget the reverted-word lock so future typing can convert normally.
        _revertedWord = null;
        _previousWordLock = null;
    }

    // A normal Backspace/Delete is mirrored precisely by KeyboardBuffer, so the
    // buffer remains usable. It does, however, invalidate the previous decision
    // about this word: after correcting a falsely switched word, the next typed
    // character must be allowed to trigger conversion in the opposite direction.
    public void NotifyTrackedDeletion()
    {
        _lastConversion = null;
        _lastCapitalization = null;

        if (_currentWordHadLiveTrigger)
        {
            Logger.Log("NotifyTrackedDeletion: rearming live conversion for edited word");
        }

        _currentWordHadLiveTrigger = false;

        // Keep the explicit Pause-revert lock when only trailing punctuation or
        // a separator was deleted and the visible word itself is unchanged.
        if (!_buffer.TryGetLastToken(out BufferToken token) || !IsRevertedWord(token.Text))
        {
            _revertedWord = null;
        }
    }

    // Returns true when `text` is the exact visible word the user just reverted
    // via the manual hotkey. The comparison uses the normalized core so a
    // trailing punctuation (e.g. "рудд ") still counts as "рудд".
    private bool IsRevertedWord(string? text)
    {
        if (string.IsNullOrEmpty(_revertedWord) || string.IsNullOrEmpty(text))
        {
            return false;
        }

        return string.Equals(_revertedWord, TextNormalizer.TrimToWord(text), StringComparison.OrdinalIgnoreCase);
    }

    private bool TryUndoAutoCapitalization(out ConversionOutcome outcome)
    {
        outcome = default;
        LastCapitalization? capitalization = _lastCapitalization;
        if (capitalization is null)
        {
            return false;
        }

        if (!_buffer.TryGetLastToken(out BufferToken token) ||
            !string.Equals(token.Text, capitalization.CapitalizedWord, StringComparison.Ordinal))
        {
            // The user moved on or edited the word; the one-shot capitalization
            // undo no longer applies, but normal Break processing may continue.
            _lastCapitalization = null;
            return false;
        }

        string replacement = capitalization.OriginalWord + token.Trailing;
        Logger.Log("=== UndoAutoCapitalization ===");
        Logger.Log(Logger.Format("capitalized", capitalization.CapitalizedWord));
        Logger.Log(Logger.Format("original", capitalization.OriginalWord));
        if (!TextInputSender.ReplaceTail(token.OriginalLength, replacement))
        {
            outcome = ConversionOutcome.From(ConversionResult.SendFailed);
            return true;
        }

        _buffer.ReplaceTail(token.OriginalLength, replacement);
        _lastCapitalization = null;
        outcome = new ConversionOutcome
        {
            Result = ConversionResult.Success,
            OriginalText = capitalization.CapitalizedWord,
            ConvertedText = capitalization.OriginalWord,
            Language = _converter.DetectLanguage(capitalization.OriginalWord),
            IsSingleWord = true,
            WasCapitalizationUndo = true
        };
        Logger.Log(Logger.Format("bufferAfter", _buffer.GetSnapshot()));
        return true;
    }

    private void ClearTextTransformationState()
    {
        _lastConversion = null;
        _lastCapitalization = null;
        _currentWordHadLiveTrigger = false;
        _revertedWord = null;
        _previousWordLock = null;
    }

    private ConversionResult Revert(LastConversion conversion)
    {
        Logger.Log($"=== Revert ===");
        Logger.Log(Logger.Format("original", conversion.Original));
        Logger.Log(Logger.Format("converted", conversion.Converted));

        ConvertedLanguage previousLanguage = conversion.Language == ConvertedLanguage.Russian
            ? ConvertedLanguage.English
            : (conversion.Language == ConvertedLanguage.English ? ConvertedLanguage.Russian : ConvertedLanguage.Unknown);

        Logger.Log($"layoutBefore=0x{LayoutProbe.GetCurrentLayoutId():X}");
        if (!SwitchLayoutPreservingForeground(previousLanguage, nameof(Revert)))
        {
            return ConversionResult.SendFailed;
        }
        Logger.Log($"layoutAfter=0x{LayoutProbe.GetCurrentLayoutId():X}");

        if (!TextInputSender.ReplaceTail(conversion.Converted.Length, conversion.Original))
        {
            Logger.Log("Revert ReplaceTail FAILED");
            return ConversionResult.SendFailed;
        }

        Logger.Log("Revert ReplaceTail OK");
        _buffer.ReplaceTail(conversion.Converted.Length, conversion.Original);
        // Remember the restored word so auto-conversion does not immediately
        // re-apply the same conversion the user just rejected. Cleared on the
        // next distinct edit / reset. Use the normalized core of the original
        // word (Original may also hold a single letter fixed together with it).
        _revertedWord = TextNormalizer.TrimToWord(conversion.OriginalWord);
        _lastConversion = null;
        return ConversionResult.Success;
    }

    private sealed record LastConversion(
        string Original,
        string Converted,
        ConvertedLanguage Language,
        DateTimeOffset Timestamp,
        string OriginalWord,
        string ConvertedWord,
        bool FromAuto);

    private sealed record LastCapitalization(string OriginalWord, string CapitalizedWord);

    private static bool SwitchLayoutPreservingForeground(ConvertedLanguage language, string operation)
    {
        if (TextInputSender.IsBatching)
        {
            // Inside the keyboard hook (separator pipeline): the replacement is
            // sent before the hook returns, to the window that receives this
            // very keystroke, so there is nothing to wait for or re-verify.
            // Same fire-and-forget switch as the live path (invariant 9).
            LayoutSwitcher.PostSwitchTo(language);
            return true;
        }

        IntPtr foregroundBefore = NativeMethods.GetForegroundWindow();
        if (foregroundBefore == IntPtr.Zero)
        {
            Logger.Log($"{operation}: no foreground window before text send");
            return false;
        }

        LayoutSwitcher.SwitchTo(language);
        System.Threading.Thread.Sleep(40);

        IntPtr foregroundAfter = NativeMethods.GetForegroundWindow();
        if (foregroundAfter != foregroundBefore)
        {
            Logger.Log($"{operation}: foreground changed from 0x{(long)foregroundBefore:X} to 0x{(long)foregroundAfter:X}; text send aborted");
            return false;
        }

        return true;
    }

    private static ConvertedLanguage OpposeLanguage(ConvertedLanguage language)
    {
        return language == ConvertedLanguage.Russian
            ? ConvertedLanguage.English
            : (language == ConvertedLanguage.English ? ConvertedLanguage.Russian : ConvertedLanguage.Unknown);
    }

    // Maps the foreground window's current keyboard layout to a conversion
    // source language. Used for letter-less tokens (where the layout cannot be
    // inferred from the text) and as the live-conversion direction source.
    private static ConvertedLanguage LanguageFromSystemLayout()
    {
        return LayoutProbe.GetCurrentLanguage();
    }
}
