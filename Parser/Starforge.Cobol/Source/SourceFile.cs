using System.Text;

namespace Starforge.Cobol.Source;

public sealed class SourceFile
{
    public const int TabStop = 8;

    private SourceFile(string path, string text, string encoding, string[] lines, bool[] tabLines)
    {
        Path = path;
        Text = text;
        Encoding = encoding;
        Lines = lines;
        TabExpanded = tabLines;
    }

    public string Path { get; }

    public string Text { get; }

    public string Encoding { get; }

    // Physical lines after CR stripping and tab expansion; index 0 is line 1.
    public IReadOnlyList<string> Lines { get; }

    public IReadOnlyList<bool> TabExpanded { get; }

    public static SourceFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (text, encoding) = Decode(bytes);
        return FromText(path, text, encoding);
    }

    public static SourceFile FromText(string path, string text, string encoding = "utf-8")
    {
        var raw = SplitLines(text);
        var lines = new string[raw.Count];
        var tabs = new bool[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            var line = raw[i];
            if (line.Contains('\t'))
            {
                tabs[i] = true;
                line = ExpandTabs(line);
            }

            lines[i] = line;
        }

        return new SourceFile(path, text, encoding, lines, tabs);
    }

    public static (string Text, string Encoding) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3), "utf-8-bom");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "utf-16le");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "utf-16be");
        }

        if (LooksLikeEbcdic(bytes))
        {
            var ebcdic = CodePagesEncodingProvider.Instance.GetEncoding(1140) ?? System.Text.Encoding.Latin1;
            var decoded = ebcdic.GetString(bytes);
            // Mainframe datasets arrive as fixed 80-byte records with no line terminators.
            var sb = new StringBuilder(decoded.Length + (decoded.Length / 80));
            for (var i = 0; i < decoded.Length; i += 80)
            {
                sb.Append(decoded, i, Math.Min(80, decoded.Length - i)).Append('\n');
            }

            return (sb.ToString(), "ibm-1140");
        }

        try
        {
            return (new UTF8Encoding(false, true).GetString(bytes), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            return (System.Text.Encoding.Latin1.GetString(bytes), "iso-8859-1");
        }
    }

    public static string ExpandTabs(string line)
    {
        var sb = new StringBuilder(line.Length + 16);
        foreach (var c in line)
        {
            if (c == '\t')
            {
                var pad = TabStop - (sb.Length % TabStop);
                sb.Append(' ', pad);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                lines.Add(text.Substring(start, end - start));
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            var end = text[^1] == '\r' ? text.Length - 1 : text.Length;
            lines.Add(text.Substring(start, end - start));
        }

        return lines;
    }

    private static bool LooksLikeEbcdic(byte[] bytes)
    {
        if (bytes.Length < 80 || bytes.Length % 80 != 0 || Array.IndexOf(bytes, (byte)0x0A) >= 0)
        {
            return false;
        }

        // EBCDIC space is 0x40; ASCII text has almost none of its letters/digits in 0xC1-0xF9.
        var spaces = 0;
        var upper = 0;
        foreach (var b in bytes)
        {
            if (b == 0x40)
            {
                spaces++;
            }
            else if (b >= 0xC1 && b <= 0xF9)
            {
                upper++;
            }
        }

        return spaces > bytes.Length / 5 && upper > bytes.Length / 20;
    }
}
