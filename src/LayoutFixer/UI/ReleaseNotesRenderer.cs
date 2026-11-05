using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LayoutFixer.UI;

internal sealed record RenderedReleaseNotes(string Rtf, string Text);

/// <summary>
/// Renders the Markdown commonly used in release notes into native RichEdit
/// formatting. HTML is always literal text; images and remote content are never
/// loaded. Link destinations stay visible so the reader can inspect them.
/// </summary>
internal static class ReleaseNotesRenderer
{
    private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})[ \t]+(.*?)(?:[ \t]+#+[ \t]*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex ListItem = new(@"^([ \t]*)(?:[-+*]|(\d{1,9})[.)])[ \t]+(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Fence = new(@"^ {0,3}(`{3,}|~{3,})(.*)$", RegexOptions.CultureInvariant);

    public static RenderedReleaseNotes Render(string? markdown, string fontFamily = "Segoe UI", float fontSize = 9f)
    {
        int normalSize = Math.Max(12, (int)Math.Round(fontSize * 2));
        var rtf = new StringBuilder(@"{\rtf1\ansi\deff0\uc1{\fonttbl{\f0 ");
        Escape(rtf, fontFamily);
        rtf.Append(@";}{\f1 Consolas;}}\viewkind4 ");
        var plain = new StringBuilder();
        string text = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length == 0)
        {
            return new RenderedReleaseNotes(rtf.Append('}').ToString(), string.Empty);
        }

        char fenceCharacter = '\0';
        int fenceLength = 0;
        bool firstLine = true;
        foreach (string sourceLine in text.Split('\n'))
        {
            Match fence = Fence.Match(sourceLine);
            if (fence.Success && (fenceCharacter == '\0' ||
                (fence.Groups[1].Value[0] == fenceCharacter && fence.Groups[1].Length >= fenceLength &&
                 string.IsNullOrWhiteSpace(fence.Groups[2].Value))))
            {
                if (fenceCharacter == '\0')
                {
                    fenceCharacter = fence.Groups[1].Value[0];
                    fenceLength = fence.Groups[1].Length;
                }
                else
                {
                    fenceCharacter = '\0';
                }
                continue;
            }

            if (!firstLine)
            {
                rtf.Append(@"\par ");
                plain.Append(Environment.NewLine);
            }
            firstLine = false;
            rtf.Append(@"\pard\plain\f0\fs").Append(normalSize).Append(@"\sa60 ");
            string line = sourceLine;
            if (fenceCharacter != '\0')
            {
                rtf.Append(@"\f1\li180\sa0 ");
                AppendLiteral(rtf, plain, line);
                continue;
            }

            Match heading = Heading.Match(line);
            Match item = ListItem.Match(line);
            if (heading.Success)
            {
                int size = normalSize + Math.Max(2, 12 - heading.Groups[1].Length * 2);
                rtf.Append(@"\b\fs").Append(size).Append(@"\sb120\sa100 ");
                line = heading.Groups[2].Value;
            }
            else if (item.Success)
            {
                int indent = Math.Min(8, item.Groups[1].Value.Replace("\t", "    ").Length / 2);
                rtf.Append(@"\li").Append((indent + 1) * 240).Append(@"\fi-180 ");
                string marker = item.Groups[2].Success ? item.Groups[2].Value + ". " : "• ";
                AppendLiteral(rtf, plain, new string(' ', indent * 2) + marker);
                line = item.Groups[3].Value;
                if (line.StartsWith("[ ] ", StringComparison.Ordinal) || line.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLiteral(rtf, plain, line[1] == ' ' ? "☐ " : "☑ ");
                    line = line[4..];
                }
            }
            else if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                rtf.Append(@"\li240\i ");
                line = line.TrimStart()[1..].TrimStart();
            }

            AppendInline(rtf, plain, line, 0);
        }

        return new RenderedReleaseNotes(rtf.Append('}').ToString(), plain.ToString());
    }

    public static bool IsWebLink(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
               !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static void AppendInline(StringBuilder rtf, StringBuilder plain, string text, int depth)
    {
        if (depth >= 12)
        {
            AppendLiteral(rtf, plain, text);
            return;
        }

        int literalStart = 0;
        for (int i = 0; i < text.Length;)
        {
            int consumed = 0;
            string? content = null;
            string? style = null;
            string? destination = null;
            bool literal = false;
            if (text[i] == '\\' && i + 1 < text.Length && char.IsPunctuation(text[i + 1]))
            {
                consumed = 2;
                content = text.Substring(i + 1, 1);
                literal = true;
            }
            else if (text[i] == '`')
            {
                int count = CountRun(text, i, '`');
                string marker = new('`', count);
                int end = text.IndexOf(marker, i + count, StringComparison.Ordinal);
                if (end >= 0)
                {
                    consumed = end + count - i;
                    content = text.Substring(i + count, end - i - count);
                    style = @"\f1 ";
                    literal = true;
                }
            }
            else if (text[i] == '[' || (text[i] == '!' && i + 1 < text.Length && text[i + 1] == '['))
            {
                int start = text[i] == '!' ? i + 1 : i;
                int labelEnd = text.IndexOf("](", start, StringComparison.Ordinal);
                if (labelEnd >= 0)
                {
                    int end = FindLinkEnd(text, labelEnd + 2);
                    if (end >= 0)
                    {
                        consumed = end + 1 - i;
                        content = text.Substring(start + 1, labelEnd - start - 1);
                        destination = text.Substring(labelEnd + 2, end - labelEnd - 2).Trim();
                        if (destination.StartsWith('<') && destination.EndsWith('>'))
                        {
                            destination = destination[1..^1];
                        }
                    }
                }
            }
            else if (text[i] == '<')
            {
                int end = text.IndexOf('>', i + 1);
                if (end > i && IsWebLink(text.Substring(i + 1, end - i - 1)))
                {
                    consumed = end + 1 - i;
                    content = text.Substring(i + 1, end - i - 1);
                    literal = true;
                }
            }
            else if (text[i] is '*' or '_' or '~' &&
                     !(text[i] == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1])))
            {
                int count = Math.Min(3, CountRun(text, i, text[i]));
                if (text[i] == '~') count = Math.Min(2, count);
                if (text[i] != '~' || count == 2)
                {
                    string marker = new(text[i], count);
                    int end = text.IndexOf(marker, i + count, StringComparison.Ordinal);
                    if (end > i + count && !char.IsWhiteSpace(text[i + count]) && !char.IsWhiteSpace(text[end - 1]))
                    {
                        consumed = end + count - i;
                        content = text.Substring(i + count, end - i - count);
                        style = text[i] == '~' ? @"\strike " : count == 3 ? @"\b\i " : count == 2 ? @"\b " : @"\i ";
                    }
                }
            }

            if (consumed == 0)
            {
                i++;
                continue;
            }

            AppendLiteral(rtf, plain, text.Substring(literalStart, i - literalStart));
            rtf.Append('{').Append(style);
            if (literal) AppendLiteral(rtf, plain, content!);
            else AppendInline(rtf, plain, content!, depth + 1);
            rtf.Append('}');
            if (IsWebLink(destination) && !string.Equals(content, destination, StringComparison.Ordinal))
            {
                AppendLiteral(rtf, plain, " (" + destination + ")");
            }
            i += consumed;
            literalStart = i;
        }
        AppendLiteral(rtf, plain, text[literalStart..]);
    }

    private static int CountRun(string text, int start, char character)
    {
        int end = start;
        while (end < text.Length && text[end] == character) end++;
        return end - start;
    }

    private static int FindLinkEnd(string text, int start)
    {
        int nesting = 0;
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length) { i++; continue; }
            if (text[i] == '(') nesting++;
            if (text[i] == ')' && nesting-- == 0) return i;
        }
        return -1;
    }

    private static void AppendLiteral(StringBuilder rtf, StringBuilder plain, string text)
    {
        plain.Append(text);
        Escape(rtf, text);
    }

    private static void Escape(StringBuilder rtf, string text)
    {
        foreach (char character in text)
        {
            switch (character)
            {
                case '\\': case '{': case '}': rtf.Append('\\').Append(character); break;
                case '\t': rtf.Append(@"\tab "); break;
                default:
                    if (character > 127 || char.IsControl(character))
                    {
                        rtf.Append(@"\u").Append(unchecked((short)character).ToString(CultureInfo.InvariantCulture)).Append('?');
                    }
                    else rtf.Append(character);
                    break;
            }
        }
    }
}
