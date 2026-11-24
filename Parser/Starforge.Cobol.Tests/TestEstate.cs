using Starforge.Cobol.Lexing;

namespace Starforge.Cobol.Tests;

// Each test owns a fresh estate folder under the test output directory and deletes it afterwards.
internal sealed class TestEstate : IDisposable
{
    public TestEstate()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "test-estates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Write(string relativePath, params string[] lines)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Join("\n", lines) + "\n");
        return full;
    }

    public CompilationResult Process(string relativePath, bool? debuggingMode = null)
        => new CobolFrontEnd(new FrontEndOptions(new[] { Root }, debuggingMode)).Process(Path.Combine(Root, relativePath));

    public CompilationResult Program(params string[] lines)
    {
        Write("src/MAIN.cbl", lines);
        return Process("src/MAIN.cbl");
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal static class Fixed
{
    // Text starting in Area A (column 8).
    public static string A(string text) => "       " + text;

    // Text starting in Area B (column 12).
    public static string B(string text) => "           " + text;

    public static string Indicator(char indicator, string text) => "      " + indicator + text;

    public static string Line(string sequence, char indicator, string programArea, string identification)
        => sequence.PadRight(6) + indicator + programArea.PadRight(65) + identification;
}

internal static class ResultExtensions
{
    public static List<(TokenKind Kind, string Text)> KindsAndTexts(this CompilationResult r)
        => r.Tokens.Select(t => (t.Kind, r.Tokens.Text(t))).ToList();

    public static List<string> Texts(this CompilationResult r) => r.Tokens.Select(t => r.Tokens.Text(t)).ToList();

    public static int IndexOfText(this CompilationResult r, string text)
    {
        for (var i = 0; i < r.Tokens.Count; i++)
        {
            if (r.Tokens.Text(i) == text)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"token '{text}' not found in: {string.Join(" ", r.Texts())}");
    }

    public static Token TokenWithText(this CompilationResult r, string text) => r.Tokens[r.IndexOfText(text)];

    public static (int StartLine, int StartColumn, int EndLine, int EndColumn, string File) Where(this CompilationResult r, string text)
    {
        var range = r.Tokens.Origin(r.IndexOfText(text)).Range;
        return (range.StartLine, range.StartColumn, range.EndLine, range.EndColumn, Path.GetFileName(range.File));
    }
}
