using System;
using System.IO;
using LayoutFixer.Input;
using LayoutFixer.Services;
using LayoutFixer.Settings;
using Xunit;

namespace LayoutFixer.Tests;

public sealed class InputPipelineTests
{
    // ── InputBatch: edits made while the separator is still held by the hook ─

    [Fact]
    public void UntouchedBatchLetsTheSeparatorThrough()
    {
        using InputBatch batch = TextInputSender.BeginBatch(" ");
        Assert.False(batch.HasChanges);
    }

    [Fact]
    public void LayoutFixReplacesOnlyWhatIsOnScreen()
    {
        // Buffer "ghbdtn " (separator not delivered yet): screen has 6 chars.
        using InputBatch batch = TextInputSender.BeginBatch(" ");
        Assert.True(TextInputSender.ReplaceTail("ghbdtn ".Length, "привет "));
        Assert.True(batch.HasChanges);
        Assert.Equal(6, batch.Backspaces);
        Assert.Equal("привет ", batch.Text);
    }

    [Fact]
    public void SuccessiveStepsComposeIntoOneReplacement()
    {
        // ". ghbdtn" + Enter: layout fix, then sentence capitalisation.
        using InputBatch batch = TextInputSender.BeginBatch("\n");
        TextInputSender.ReplaceTail("ghbdtn\n".Length, "привет\n");
        TextInputSender.ReplaceTail("привет\n".Length, "Привет\n");
        Assert.Equal(6, batch.Backspaces);
        Assert.Equal("Привет\n", batch.Text);
    }

    [Fact]
    public void ShortEditInsidePendingTextNeedsNoBackspaces()
    {
        using InputBatch batch = TextInputSender.BeginBatch(" ");
        // Replace only the pending separator with ", " (e.g. a replacement
        // that re-applies punctuation) -> no backspace into the document.
        TextInputSender.ReplaceTail(1, ", ");
        Assert.Equal(0, batch.Backspaces);
        Assert.Equal(", ", batch.Text);
    }

    [Fact]
    public void BatchClosesWithItsScope()
    {
        using (TextInputSender.BeginBatch(" "))
        {
            Assert.True(TextInputSender.IsBatching);
        }

        Assert.False(TextInputSender.IsBatching);
    }

    // ── case fixes ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ПРивет", "Привет")]
    [InlineData("THis", "This")]
    [InlineData("HEllo,", "Hello,")]
    [InlineData("\"ПРивет\"", "\"Привет\"")]
    [InlineData("IDs", "IDs")]
    [InlineData("GHz", "GHz")]
    [InlineData("PhD", "PhD")]
    [InlineData("iPhone", "iPhone")]
    [InlineData("USSR", "USSR")]
    [InlineData("IOException", "IOException")]
    [InlineData("TVшка", "TVшка")]
    [InlineData("Привет", "Привет")]
    public void TwoInitialCapitals(string typed, string expected)
    {
        Assert.Equal(expected, TextTransformationService.FixTwoInitialCapitals(typed));
    }

    // ── layout languages ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x0409, "English")]
    [InlineData(0x0809, "English")] // UK
    [InlineData(0x1009, "English")] // Canada
    [InlineData(0x0419, "Russian")]
    [InlineData(0x0819, "Russian")] // Moldova
    [InlineData(0x0422, "Unknown")] // Ukrainian
    [InlineData(0x0423, "Unknown")] // Belarusian
    [InlineData(0x0407, "Unknown")] // German
    public void LayoutLanguageUsesPrimaryLanguage(int langId, string expected)
    {
        Assert.Equal(expected, LayoutProbe.LanguageOf((ushort)langId).ToString());
    }

    // ── keyboard buffer ──────────────────────────────────────────────────────

    [Fact]
    public void PreviousWordNeedsAWhitespaceGap()
    {
        var buffer = new KeyboardBuffer();
        buffer.ReplaceTail(0, "plan b");
        Assert.Equal("plan", buffer.GetWordBeforeLastToken());

        buffer.Clear();
        buffer.ReplaceTail(0, "b");
        Assert.Null(buffer.GetWordBeforeLastToken());

        buffer.Clear();
        buffer.ReplaceTail(0, "(b");
        Assert.Null(buffer.GetWordBeforeLastToken());
    }

    [Fact]
    public void ExclamationMarkEndsTheSentenceContext()
    {
        var buffer = new KeyboardBuffer();
        buffer.ReplaceTail(0, "hello! d");
        Assert.Null(buffer.GetWordBeforeLastToken());

        // A new chat message or paragraph.
        buffer.Clear();
        buffer.ReplaceTail(0, "thanks \nz");
        Assert.Null(buffer.GetWordBeforeLastToken());
    }

    [Fact]
    public void SentenceBeforeLastTokenStopsAtTheSentenceEnd()
    {
        var buffer = new KeyboardBuffer();
        buffer.ReplaceTail(0, "Hi. Сравни docker b ");
        Assert.Equal(" Сравни docker ", buffer.GetSentenceBeforeLastToken());

        buffer.Clear();
        buffer.ReplaceTail(0, "b");
        Assert.Equal(string.Empty, buffer.GetSentenceBeforeLastToken());
    }

    [Fact]
    public void TokenBeforeLastTokenKeepsTheExactGap()
    {
        var buffer = new KeyboardBuffer();
        buffer.ReplaceTail(0, "hello z  levf. ");
        Assert.True(buffer.TryGetTokenBeforeLastToken(out string word, out string gap));
        Assert.Equal("z", word);
        Assert.Equal("  ", gap);

        buffer.Clear();
        buffer.ReplaceTail(0, "levf ");
        Assert.False(buffer.TryGetTokenBeforeLastToken(out _, out _));

        buffer.Clear();
        buffer.ReplaceTail(0, "z!levf");
        Assert.True(buffer.TryGetTokenBeforeLastToken(out word, out gap));
        Assert.Equal("z", word);
        Assert.Equal("!", gap);
    }

    // 1.10.1: "it" Space, Break -> "ше", Space must not run the separator
    // pipeline on "ше" again.
    [Theory]
    [InlineData(" ", false)]
    [InlineData("\n", false)]
    [InlineData("! ", false)]
    [InlineData(") ", false)]
    [InlineData("  ", true)]
    [InlineData(" -", true)]
    [InlineData("\n\n", true)]
    [InlineData("\n ", true)]
    public void SecondSeparatorAfterAWordIsDetected(string trailing, bool expected)
    {
        Assert.Equal(expected, new BufferToken("ше", trailing).HasEarlierTerminator);
    }

    [Fact]
    public void LastCharacterReflectsTheBufferTail()
    {
        var buffer = new KeyboardBuffer();
        Assert.Null(buffer.LastCharacter);
        buffer.ReplaceTail(0, "ghbdtn\n");
        Assert.Equal('\n', buffer.LastCharacter);
    }

    // ── settings and storage ─────────────────────────────────────────────────

    [Fact]
    public void ExcludedAppsAreNormalised()
    {
        var apps = AppSettingsService.NormalizeAppList(new[]
        {
            "Game.EXE", @"C:\Program Files\Tool\tool.exe", "notepad", "game.exe", " ", "\"quoted.exe\""
        });
        Assert.Equal(new[] { "game.exe", "tool.exe", "notepad.exe", "quoted.exe" }, apps);
    }

    [Fact]
    public void AtomicFileKeepsBackupAndRecoversFromCorruption()
    {
        string folder = Path.Combine(Path.GetTempPath(), "LayoutFixer.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "data.json");
            AtomicFile.WriteAllText(path, "first");
            AtomicFile.WriteAllText(path, "second");
            Assert.Equal("second", File.ReadAllText(path));
            Assert.Equal("first", File.ReadAllText(AtomicFile.BackupPath(path)));

            // A damaged main file: the loader falls back to the backup and keeps
            // the damaged copy aside instead of deleting it.
            File.WriteAllText(path, "garbage");
            string? loaded = AtomicFile.Load(path, text => text == "garbage" ? throw new FormatException() : text, out bool recovered);
            Assert.Equal("first", loaded);
            Assert.True(recovered);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(folder, "data.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void UserDictionarySurvivesReload()
    {
        string folder = Path.Combine(Path.GetTempPath(), "LayoutFixer.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "user-dictionary.json");
            var dictionary = new UserDictionary(path);
            Assert.True(dictionary.Add(ConvertedLanguage.English, "github"));
            Assert.True(dictionary.Add(ConvertedLanguage.Russian, "мёд"));

            var reloaded = new UserDictionary(path);
            Assert.True(reloaded.ContainsEnglish("github"));
            Assert.True(reloaded.ContainsRussian("мед"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
