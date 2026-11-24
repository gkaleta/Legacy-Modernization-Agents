using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
        {
            // A platform copy's usages are the program's own statements, so they root in the program's inputs.
            if (Str(child, "dependencyKind") == "PLATFORM_COPY")
                CheckRoots("runtimeContract", Get(child, "runtimeContract"), files, root, texts, errors);
            else if (Str(child, "dependencyKind") != "COPY" && Items(child, "dependencyDialectStatements").Any())
                StarforgeSyntaxRoots(child, root, errors);
        }
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
            // An explicit SQL catalog binding's declaration lives in its own hash-bound catalog file, which the
            // dialect statement check requires to be a configured declaration input.
            case JsonObject obj when Str(obj, "definitionScope") == "explicit-sql-catalog" && Str(obj, "sourcePath") is { } catalog:
                var withCatalog = new HashSet<string>(files, StringComparer.Ordinal) { PyCompat.Resolve(catalog) };
                foreach (var (name, item) in obj) CheckRoots(name, item, withCatalog, root, texts, errors);
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
        if (node.ContainsKey("replacing"))
        {
            if (Replaced(Get(node, "replacing"), path, compact, files, root, texts) is not { } replaced)
            {
                errors.Add($"starforge-syntax-root-replacing-invalid:{kind}");
                return;
            }
            compact = replaced;
        }
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

    private static readonly Regex CopyReplacing = new(
        @"^COPY\s+(?:""([^""]+)""|'([^']+)'|([A-Z0-9$#@-]+))(?:\s+(?:OF|IN)\s+\S+)?\s+REPLACING((?:\s*==(?:(?!==).)*==\s*BY\s*==(?:(?!==).)*==)+)\s*\.?$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex PseudoTextPair = new(@"==((?:(?!==).)*)==\s*BY\s*==((?:(?!==).)*)==",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // The proof is the COPY statement that included the root's copybook, one hash-bound range per source line
    // (so fixed-format sequence areas stay out). Its pseudo-text pairs, applied first-match left to right,
    // turn the copybook text into the text the tokens were parsed from.
    private static string? Replaced(JsonNode? replacing, string copybook, string compact, HashSet<string> files, string root,
        Dictionary<string, string> texts)
    {
        if (replacing is not JsonObject proof || proof.Count != 1 || Get(proof, "statement") is not JsonArray { Count: > 0 } pieces)
            return null;
        var lines = new List<string>();
        string? file = null;
        long previous = 0;
        foreach (var piece in pieces)
        {
            if (Str(piece, "path") is not { } path || !IsAbsolute(path) || Get(piece, "start") is not JsonArray { Count: 2 } start
                || Get(piece, "end") is not JsonArray { Count: 2 } end || !start.Concat(end).All(item => IsInt(item, out _))
                || Str(piece, "textSha256") is not { } sha || ((JsonObject)piece!).Count != 4)
                return null;
            path = PyCompat.Resolve(path);
            IsInt(start[0], out var line);
            IsInt(start[1], out var startColumn);
            IsInt(end[0], out var endLine);
            IsInt(end[1], out var endColumn);
            if (file is not null && path != file || line != endLine || line <= previous) return null;
            if (file is null && (!files.Contains(path) || !IsRelativeTo(path, root) || !File.Exists(path))) return null;
            file = path;
            previous = line;
            if (!texts.TryGetValue(path, out var text)) texts[path] = text = PyCompat.ReadText(path, PyCompat.Latin1);
            var slice = SourceSlice(text, line, startColumn, endLine, endColumn);
            if (slice is null || PyCompat.Digest(PyCompat.Latin1.GetBytes(slice)) != sha) return null;
            lines.Add(slice);
        }
        var statement = CopyReplacing.Match(string.Join(' ', lines).Trim());
        if (!statement.Success) return null;
        var name = new[] { 1, 2, 3 }.Select(group => statement.Groups[group]).First(group => group.Success).Value;
        if (!string.Equals(Path.GetFileNameWithoutExtension(name), Path.GetFileNameWithoutExtension(copybook), StringComparison.OrdinalIgnoreCase))
            return null;
        var pairs = PseudoTextPair.Matches(statement.Groups[4].Value).Select(pair => (From: Compact(pair.Groups[1].Value), To: Compact(pair.Groups[2].Value))).ToList();
        if (pairs.Any(pair => pair.From.Length == 0)) return null;
        var builder = new StringBuilder();
        for (var index = 0; index < compact.Length;)
        {
            var match = pairs.FindIndex(pair => string.Compare(compact, index, pair.From, 0, pair.From.Length, StringComparison.OrdinalIgnoreCase) == 0);
            if (match < 0)
            {
                builder.Append(compact[index++]);
                continue;
            }
            builder.Append(pairs[match].To);
            index += pairs[match].From.Length;
        }
        return builder.ToString();
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
