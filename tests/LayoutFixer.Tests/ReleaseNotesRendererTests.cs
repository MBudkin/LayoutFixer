using System;
using System.Threading;
using System.Windows.Forms;
using LayoutFixer.UI;
using Xunit;

namespace LayoutFixer.Tests;

public sealed class ReleaseNotesRendererTests
{
    [Fact]
    public void HeadingsEmphasisAndListsHaveNativeFormatting()
    {
        RenderedReleaseNotes result = ReleaseNotesRenderer.Render("## Новое\n- **Жирный** и *курсив*\n1. `код`\n  - вложенный");
        Assert.Equal(string.Join(Environment.NewLine, "Новое", "• Жирный и курсив", "1. код", "  • вложенный"), result.Text);
        WithRichTextBox(box =>
        {
            box.Rtf = result.Rtf;
            Assert.Equal(result.Text.Replace(Environment.NewLine, "\n"), box.Text);
            Select(box, "Новое");
            Assert.True(box.SelectionFont!.Bold);
            Assert.True(box.SelectionFont.SizeInPoints > 9);
            Select(box, "Жирный");
            Assert.True(box.SelectionFont!.Bold);
            Select(box, "курсив");
            Assert.True(box.SelectionFont!.Italic);
            Select(box, "код");
            Assert.Equal("Consolas", box.SelectionFont!.Name);
        });
    }

    [Fact]
    public void FencedCodeIsLiteralAndPreservesIndentation()
    {
        RenderedReleaseNotes result = ReleaseNotesRenderer.Render("```csharp\n  var value = \"**literal**\";\n  { \\b }\n```\nПосле");
        Assert.Equal(string.Join(Environment.NewLine, "  var value = \"**literal**\";", "  { \\b }", "После"), result.Text);
        WithRichTextBox(box =>
        {
            box.Rtf = result.Rtf;
            Assert.Equal(result.Text.Replace(Environment.NewLine, "\n"), box.Text);
            Select(box, "literal");
            Assert.False(box.SelectionFont!.Bold);
            Assert.Equal("Consolas", box.SelectionFont.Name);
            Select(box, "После");
            Assert.NotEqual("Consolas", box.SelectionFont!.Name);
        });
    }

    [Fact]
    public void LinkAddressesAreRetainedAndNoHtmlOrRtfCanExecute()
    {
        RenderedReleaseNotes result = ReleaseNotesRenderer.Render("[Issue](https://example.com/a_(b))\n[bad](file:///C:/test.exe)\n<script>alert(1)</script> {\\field fake} 😀");
        Assert.Contains("Issue (https://example.com/a_(b))", result.Text);
        Assert.DoesNotContain("file:", result.Text);
        Assert.Contains("<script>alert(1)</script>", result.Text);
        WithRichTextBox(box =>
        {
            box.Rtf = result.Rtf;
            Assert.Equal(result.Text.Replace(Environment.NewLine, "\n"), box.Text);
            Assert.Contains("{\\field fake} 😀", box.Text);
        });
    }

    [Theory]
    [InlineData("https://github.com/MBudkin/LayoutFixer", true)]
    [InlineData("http://example.com/path", true)]
    [InlineData("file:///C:/program.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("mailto:someone@example.com", false)]
    [InlineData("https://user:secret@example.com", false)]
    [InlineData("C:\\program.exe", false)]
    [InlineData(null, false)]
    public void OnlyWebLinksCanBeOpened(string? url, bool expected)
    {
        Assert.Equal(expected, ReleaseNotesRenderer.IsWebLink(url));
    }

    [Fact]
    public void EscapesUnmatchedMarkupAndIdentifiersStayReadable()
    {
        RenderedReleaseNotes result = ReleaseNotesRenderer.Render(@"\*literal\* file_name_here **unclosed `unclosed");
        Assert.Equal("*literal* file_name_here **unclosed `unclosed", result.Text);
    }

    [Fact]
    public void TaskListsAndBlockQuotesAreReadable()
    {
        RenderedReleaseNotes result = ReleaseNotesRenderer.Render("- [x] Готово\n- [ ] Позже\n> **Важно**");
        Assert.Equal(string.Join(Environment.NewLine, "• ☑ Готово", "• ☐ Позже", "Важно"), result.Text);
    }

    private static void Select(RichTextBox box, string text)
    {
        box.Select(box.Text.IndexOf(text, StringComparison.Ordinal), text.Length);
    }

    private static void WithRichTextBox(Action<RichTextBox> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var box = new RichTextBox();
                action(box);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "RichEdit test did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
