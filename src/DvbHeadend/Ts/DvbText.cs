using System.Text;

namespace DvbHeadend.Ts;

/// <summary>Text decoding per ETSI EN 300 468 Annex A.</summary>
public static class DvbText
{
    private static readonly Dictionary<byte, char> Iso6937High = new()
    {
        [0xA0] = ' ', [0xA1] = '¡', [0xA2] = '¢', [0xA3] = '£', [0xA4] = '€', [0xA5] = '¥', [0xA7] = '§',
        [0xA8] = '¤', [0xA9] = '‘', [0xAA] = '“', [0xAB] = '«', [0xAC] = '←', [0xAD] = '↑', [0xAE] = '→', [0xAF] = '↓',
        [0xB0] = '°', [0xB1] = '±', [0xB2] = '²', [0xB3] = '³', [0xB4] = '×', [0xB5] = 'µ', [0xB6] = '¶', [0xB7] = '·',
        [0xB8] = '÷', [0xB9] = '’', [0xBA] = '”', [0xBB] = '»', [0xBC] = '¼', [0xBD] = '½', [0xBE] = '¾', [0xBF] = '¿',
        [0xD0] = '―', [0xD1] = '¹', [0xD2] = '®', [0xD3] = '©', [0xD4] = '™', [0xD5] = '♪', [0xD6] = '¬', [0xD7] = '¦',
        [0xDC] = '⅛', [0xDD] = '⅜', [0xDE] = '⅝', [0xDF] = '⅞',
        [0xE0] = 'Ω', [0xE1] = 'Æ', [0xE2] = 'Đ', [0xE3] = 'ª', [0xE4] = 'Ħ', [0xE6] = 'Ĳ', [0xE7] = 'Ŀ',
        [0xE8] = 'Ł', [0xE9] = 'Ø', [0xEA] = 'Œ', [0xEB] = 'º', [0xEC] = 'Þ', [0xED] = 'Ŧ', [0xEE] = 'Ŋ', [0xEF] = 'ŉ',
        [0xF0] = 'ĸ', [0xF1] = 'æ', [0xF2] = 'đ', [0xF3] = 'ð', [0xF4] = 'ħ', [0xF5] = 'ı', [0xF6] = 'ĳ', [0xF7] = 'ŀ',
        [0xF8] = 'ł', [0xF9] = 'ø', [0xFA] = 'œ', [0xFB] = 'ß', [0xFC] = 'þ', [0xFD] = 'ŧ', [0xFE] = 'ŋ', [0xFF] = '­'
    };

    // ISO 6937 non-spacing diacritics (0xC1..0xCF precede the letter they modify).
    // Composed by table rather than string.Normalize, which needs ICU.
    private static readonly (string Bases, string Composed)[] Diacritics =
    [
        ("", ""),
        ("AEIOUaeiou", "ÀÈÌÒÙàèìòù"),
        ("ACEILNORSUYZaceilnorsuyz", "ÁĆÉÍĹŃÓŔŚÚÝŹáćéíĺńóŕśúýź"),
        ("ACEGHIJOSUWYaceghijosuwy", "ÂĈÊĜĤÎĴÔŜÛŴŶâĉêĝĥîĵôŝûŵŷ"),
        ("AINOUainou", "ÃĨÑÕŨãĩñõũ"),
        ("AEIOUaeiou", "ĀĒĪŌŪāēīōū"),
        ("AGUagu", "ĂĞŬăğŭ"),
        ("CEGIZcegz", "ĊĖĠİŻċėġż"),
        ("AEIOUYaeiouy", "ÄËÏÖÜŸäëïöüÿ"),
        ("", ""),
        ("AUau", "ÅŮåů"),
        ("CGKLNRSTcklnrst", "ÇĢĶĻŅŖŞŢçķļņŗşţ"),
        ("", ""),
        ("OUou", "ŐŰőű"),
        ("AEIUaeiu", "ĄĘĮŲąęįų"),
        ("CDELNRSTZcdelnrstz", "ČĎĚĽŇŘŠŤŽčďěľňřšťž")
    ];

    static DvbText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>True when the text is compressed (Freeview HD / Freesat Huffman, or another private scheme).</summary>
    public static bool IsCompressed(ReadOnlySpan<byte> text) => text.Length > 0 && text[0] == 0x1F;

    public static string Decode(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty)
        {
            return "";
        }

        byte first = text[0];
        if (first >= 0x20)
        {
            return DecodeIso6937(text);
        }
        if (first == 0x1F)
        {
            // Freeview HD / Freesat compressed text; empty if it uses some other scheme.
            return Clean(FreesatHuffman.Decode(text) ?? "");
        }
        if (first == 0x15)
        {
            return Clean(Encoding.UTF8.GetString(text[1..]));
        }
        if (first == 0x11)
        {
            return Clean(Encoding.BigEndianUnicode.GetString(text[1..]));
        }
        if (first == 0x10 && text.Length >= 3)
        {
            return DecodeSingleByte(text[3..], "iso-8859-" + text[2]);
        }
        if (first >= 0x01 && first <= 0x0B)
        {
            return DecodeSingleByte(text[1..], "iso-8859-" + (first + 4));
        }
        return DecodeSingleByte(text[1..], "iso-8859-1");
    }

    private static string DecodeSingleByte(ReadOnlySpan<byte> text, string encodingName)
    {
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(encodingName);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.Latin1;
        }

        // 0x80..0x9F are DVB control codes in every single-byte table.
        var bytes = new List<byte>(text.Length);
        foreach (byte b in text)
        {
            if (b == 0x8A)
            {
                bytes.Add((byte)'\n');
            }
            else if (b < 0x80 || b > 0x9F)
            {
                bytes.Add(b);
            }
        }
        return Clean(encoding.GetString(bytes.ToArray()));
    }

    private static string DecodeIso6937(ReadOnlySpan<byte> text)
    {
        var sb = new StringBuilder(text.Length);
        int diacritic = 0;
        foreach (byte b in text)
        {
            if (b < 0x80)
            {
                char c = (char)b;
                if (diacritic != 0)
                {
                    var (bases, composed) = Diacritics[diacritic];
                    int index = bases.IndexOf(c);
                    if (index >= 0)
                    {
                        c = composed[index];
                    }
                    diacritic = 0;
                }
                sb.Append(c);
            }
            else if (b == 0x8A)
            {
                sb.Append('\n');
            }
            else if (b <= 0x9F)
            {
                // emphasis on/off and other control codes
            }
            else if (b >= 0xC1 && b <= 0xCF)
            {
                diacritic = b - 0xC0;
            }
            else if (Iso6937High.TryGetValue(b, out char mapped))
            {
                sb.Append(mapped);
            }
        }
        return Clean(sb.ToString());
    }

    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '')
            {
                sb.Append('\n');
            }
            else if (c == '\n' || (!char.IsControl(c) && (c < '' || c > '')))
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }
}
