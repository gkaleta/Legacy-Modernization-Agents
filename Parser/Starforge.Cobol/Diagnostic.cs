using System.Text;
using Starforge.Cobol.Source;

namespace Starforge.Cobol;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message, SourceRange? Range, string Shape)
{
    // Clustering key: same code + same normalized token shape groups failures across programs.
    public string Fingerprint => Shape.Length == 0 ? Code : Code + ":" + Shape;

    public static Diagnostic Create(string code, DiagnosticSeverity severity, string message, SourceRange? range, string shape = "")
        => new(code, severity, message, range, shape);
}

public static class DiagnosticShape
{
    // Words are upper-cased, digit runs collapse to 9 and literal bodies are dropped so that
    // "MOVE 'abc' TO X1" and "move 'xyz' to x22" share one fingerprint.
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        var lastWasSpace = true;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                i++;
                continue;
            }

            lastWasSpace = false;
            if (c is '\'' or '"')
            {
                var close = text.IndexOf(c, i + 1);
                sb.Append(c).Append('…').Append(c);
                i = close < 0 ? text.Length : close + 1;
                continue;
            }

            if (char.IsDigit(c))
            {
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }

                sb.Append('9');
                continue;
            }

            sb.Append(char.ToUpperInvariant(c));
            i++;
        }

        return sb.ToString().Trim();
    }
}
