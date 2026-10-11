using System.Collections.Generic;

namespace LayoutFixer.Input;

/// <summary>
/// Maps physical keyboard scan codes (set 1) to characters for the standard
/// English (US) and Russian layouts. Used as a reliable fallback because
/// ToUnicodeEx can return unexpected characters when the focused window
/// belongs to another process.
/// </summary>
internal static class ScanCodeMap
{
    public const ushort EnglishLangId = 0x0409;
    public const ushort RussianLangId = 0x0419;

    private static readonly Dictionary<uint, (char Base, char Shift)> English = BuildEnglish();
    private static readonly Dictionary<uint, (char Base, char Shift)> Russian = BuildRussian();

    public static bool TryLookup(uint scanCode, bool shift, ushort langId, out char c)
    {
        Dictionary<uint, (char Base, char Shift)> map = langId == RussianLangId ? Russian : English;
        if (map.TryGetValue(scanCode, out (char Base, char Shift) pair))
        {
            c = shift ? pair.Shift : pair.Base;
            return true;
        }

        c = default;
        return false;
    }

    public static bool IsSupported(ushort langId) => langId == EnglishLangId || langId == RussianLangId;

    private static Dictionary<uint, (char, char)> BuildEnglish()
    {
        var m = new Dictionary<uint, (char, char)>(64);
        m[0x02] = ('1', '!');
        m[0x03] = ('2', '@');
        m[0x04] = ('3', '#');
        m[0x05] = ('4', '$');
        m[0x06] = ('5', '%');
        m[0x07] = ('6', '^');
        m[0x08] = ('7', '&');
        m[0x09] = ('8', '*');
        m[0x0A] = ('9', '(');
        m[0x0B] = ('0', ')');
        m[0x0C] = ('-', '_');
        m[0x0D] = ('=', '+');
        m[0x10] = ('q', 'Q');
        m[0x11] = ('w', 'W');
        m[0x12] = ('e', 'E');
        m[0x13] = ('r', 'R');
        m[0x14] = ('t', 'T');
        m[0x15] = ('y', 'Y');
        m[0x16] = ('u', 'U');
        m[0x17] = ('i', 'I');
        m[0x18] = ('o', 'O');
        m[0x19] = ('p', 'P');
        m[0x1A] = ('[', '{');
        m[0x1B] = (']', '}');
        m[0x1E] = ('a', 'A');
        m[0x1F] = ('s', 'S');
        m[0x20] = ('d', 'D');
        m[0x21] = ('f', 'F');
        m[0x22] = ('g', 'G');
        m[0x23] = ('h', 'H');
        m[0x24] = ('j', 'J');
        m[0x25] = ('k', 'K');
        m[0x26] = ('l', 'L');
        m[0x27] = (';', ':');
        m[0x28] = ('\'', '"');
        m[0x29] = ('`', '~');
        m[0x2B] = ('\\', '|');
        m[0x2C] = ('z', 'Z');
        m[0x2D] = ('x', 'X');
        m[0x2E] = ('c', 'C');
        m[0x2F] = ('v', 'V');
        m[0x30] = ('b', 'B');
        m[0x31] = ('n', 'N');
        m[0x32] = ('m', 'M');
        m[0x33] = (',', '<');
        m[0x34] = ('.', '>');
        m[0x35] = ('/', '?');
        m[0x39] = (' ', ' ');
        return m;
    }

    private static Dictionary<uint, (char, char)> BuildRussian()
    {
        var m = new Dictionary<uint, (char, char)>(64);
        m[0x02] = ('1', '!');
        m[0x03] = ('2', '"');
        m[0x04] = ('3', '№');
        m[0x05] = ('4', ';');
        m[0x06] = ('5', '%');
        m[0x07] = ('6', ':');
        m[0x08] = ('7', '?');
        m[0x09] = ('8', '*');
        m[0x0A] = ('9', '(');
        m[0x0B] = ('0', ')');
        m[0x0C] = ('-', '_');
        m[0x0D] = ('=', '+');
        m[0x10] = ('й', 'Й');
        m[0x11] = ('ц', 'Ц');
        m[0x12] = ('у', 'У');
        m[0x13] = ('к', 'К');
        m[0x14] = ('е', 'Е');
        m[0x15] = ('н', 'Н');
        m[0x16] = ('г', 'Г');
        m[0x17] = ('ш', 'Ш');
        m[0x18] = ('щ', 'Щ');
        m[0x19] = ('з', 'З');
        m[0x1A] = ('х', 'Х');
        m[0x1B] = ('ъ', 'Ъ');
        m[0x1E] = ('ф', 'Ф');
        m[0x1F] = ('ы', 'Ы');
        m[0x20] = ('в', 'В');
        m[0x21] = ('а', 'А');
        m[0x22] = ('п', 'П');
        m[0x23] = ('р', 'Р');
        m[0x24] = ('о', 'О');
        m[0x25] = ('л', 'Л');
        m[0x26] = ('д', 'Д');
        m[0x27] = ('ж', 'Ж');
        m[0x28] = ('э', 'Э');
        m[0x29] = ('ё', 'Ё');
        m[0x2B] = ('\\', '/');
        m[0x2C] = ('я', 'Я');
        m[0x2D] = ('ч', 'Ч');
        m[0x2E] = ('с', 'С');
        m[0x2F] = ('м', 'М');
        m[0x30] = ('и', 'И');
        m[0x31] = ('т', 'Т');
        m[0x32] = ('ь', 'Ь');
        m[0x33] = ('б', 'Б');
        m[0x34] = ('ю', 'Ю');
        m[0x35] = ('.', ',');
        m[0x39] = (' ', ' ');
        return m;
    }
}
