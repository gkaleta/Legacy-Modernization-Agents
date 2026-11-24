using System.Text;
using System.Text.Json.Nodes;

namespace CobolToQuarkusMigration.Agents.Infrastructure.Rekt;

// A Starforge syntax root names the file and the 1-based inclusive range it was parsed from, plus the SHA-256
// of that exact text. Re-reading the hash-bound input proves the tree is Starforge's parse of it: a root must
// point into the program or one of its exported copybooks, match the hash, and spell its tokens in order.
public static partial class RektEvidence
{
    private static readonly HashSet<string> SyntaxKeys =
        ["syntax", "definitionSyntax", "descriptionSyntax", "semanticSyntax", "ancestorSyntax", "usageSyntax"];

    internal static void StarforgeSyntaxRoots(JsonNode? document, string root, List<string> errors)
    {
        if (document is not JsonObject program) return;
        var files = new HashSet<string>(StringComparer.Ordinal);
        if (Str(program, "sourcePath") is { } own) files.Add(PyCompat.Resolve(own));
        foreach (var child in Items(program, "dependencies"))
            if (Str(child, "dependencyKind") == "COPY" && Str(child, "sourcePath") is { } copy) files.Add(PyCompat.Resolve(copy));
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in program)
            if (key != "dependencies") CheckRoots(key, value, files, root, texts, errors);
        foreach (var child in Items(program, "dependencies"))
            if (Str(child, "dependencyKind") != "COPY" && Items(child, "dependencyDialectStatements").Any())
                StarforgeSyntaxRoots(child, root, errors);
    }

    private static void CheckRoots(string key, JsonNode? value, HashSet<string> files, string root,
        Dictionary<string, string> texts, List<string> errors)
    {
        switch (value)
        {
            case JsonObject node when NativeProducer.IsStarforgeNode(node):
                if (node.ContainsKey("range")) RootProblem(node, files, root, texts, errors);
                else if (SyntaxKeys.Contains(key)) errors.Add($"starforge-syntax-root-unbound:{key}");
                return;
            case JsonObject when SyntaxKeys.Contains(key):
                errors.Add($"starforge-syntax-root-foreign:{key}");
                return;
            case JsonObject obj:
                foreach (var (name, item) in obj) CheckRoots(name, item, files, root, texts, errors);
                return;
            case JsonArray array:
                foreach (var item in array) CheckRoots(key, item, files, root, texts, errors);
                return;
        }
    }

    private static void RootProblem(JsonObject node, HashSet<string> files, string root, Dictionary<string, string> texts, List<string> errors)
    {
        var kind = Str(node, "node");
        var range = Get(node, "range");
        if (Str(range, "path") is not { } path || !IsAbsolute(path) || Get(range, "start") is not JsonArray { Count: 2 } start
            || Get(range, "end") is not JsonArray { Count: 2 } end || !start.Concat(end).All(item => IsInt(item, out _))
            || Str(node, "textSha256") is not { } sha)
        {
            errors.Add($"starforge-syntax-root-invalid:{kind}");
            return;
        }
        path = PyCompat.Resolve(path);
        if (!files.Contains(path) || !IsRelativeTo(path, root) || !File.Exists(path))
        {
            errors.Add($"starforge-syntax-root-outside-inputs:{kind}");
            return;
        }
        if (!texts.TryGetValue(path, out var text)) texts[path] = text = PyCompat.ReadText(path, PyCompat.Latin1);
        IsInt(start[0], out var startLine);
        IsInt(start[1], out var startColumn);
        IsInt(end[0], out var endLine);
        IsInt(end[1], out var endColumn);
        var slice = SourceSlice(text, startLine, startColumn, endLine, endColumn);
        if (slice is null || PyCompat.Digest(PyCompat.Latin1.GetBytes(slice)) != sha)
        {
            errors.Add($"starforge-syntax-root-hash-mismatch:{kind}");
            return;
        }
        var compact = Compact(slice);
        var at = 0;
        foreach (var token in Tokens(SyntaxNodes(node)))
        {
            var word = Compact(token);
            if (word.Length == 0) continue;
            var found = compact.IndexOf(word, at, StringComparison.Ordinal);
            if (found < 0)
            {
                errors.Add($"starforge-syntax-root-text-mismatch:{kind}");
                return;
            }
            at = found + word.Length;
        }
    }

    // Lines split on '\n' with a trailing '\r' dropped; columns are 1-based and the end column is inclusive.
    internal static string? SourceSlice(string text, long startLine, long startColumn, long endLine, long endColumn)
    {
        var lines = text.Split('\n');
        if (startLine < 1 || endLine < startLine || endLine > lines.Length || startColumn < 1 || endColumn < 1) return null;
        string Line(long number) => lines[number - 1].EndsWith('\r') ? lines[number - 1][..^1] : lines[number - 1];
        var first = Line(startLine);
        var last = Line(endLine);
        if (startColumn > first.Length || endColumn > last.Length || startLine == endLine && endColumn < startColumn) return null;
        if (startLine == endLine) return first.Substring((int)startColumn - 1, (int)(endColumn - startColumn + 1));
        var builder = new StringBuilder(first[((int)startColumn - 1)..]);
        for (var line = startLine + 1; line < endLine; line++) builder.Append('\n').Append(Line(line));
        return builder.Append('\n').Append(last[..(int)endColumn]).ToString();
    }

    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
