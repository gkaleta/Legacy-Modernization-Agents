using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Starforge.Cobol.Copy;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Native;

// Renders Starforge syntax as the native dependency contract's trees: {"token": text} leaves and
// {"node": kind, "children": [...]} nodes. Roots also carry the original source range and the SHA-256 of
// the exact source text the range covers, so the gate can re-read the file and check both.
public static class NativeSyntax
{
    public static JsonObject Token(string text) => new() { ["token"] = text };

    public static JsonObject Node(string kind, IEnumerable<JsonNode> children)
    {
        var array = new JsonArray();
        foreach (var child in children)
        {
            array.Add(child);
        }

        return new JsonObject { ["node"] = kind, ["children"] = array };
    }

    // Lines split on '\n' with a trailing '\r' dropped; columns are 1-based and the end column is inclusive.
    public static string? Slice(string text, int startLine, int startColumn, int endLine, int endColumn)
    {
        var lines = text.Split('\n');
        if (startLine < 1 || endLine < startLine || endLine > lines.Length || startColumn < 1 || endColumn < 1)
        {
            return null;
        }

        string Line(int number) => lines[number - 1].EndsWith('\r') ? lines[number - 1][..^1] : lines[number - 1];
        var first = Line(startLine);
        var last = Line(endLine);
        if (startColumn > first.Length || endColumn > last.Length || (startLine == endLine && endColumn < startColumn))
        {
            return null;
        }

        if (startLine == endLine)
        {
            return first.Substring(startColumn - 1, endColumn - startColumn + 1);
        }

        var builder = new StringBuilder(first[(startColumn - 1)..]);
        for (var line = startLine + 1; line < endLine; line++)
        {
            builder.Append('\n').Append(Line(line));
        }

        return builder.Append('\n').Append(last[..endColumn]).ToString();
    }

    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(text))).ToLowerInvariant();

    public static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

// Turns token or expanded-text ranges of one compilation into rooted syntax trees.
internal sealed class SyntaxRoots
{
    private readonly CompilationResult _result;
    private readonly Dictionary<string, string?> _files = new(StringComparer.Ordinal);

    public SyntaxRoots(CompilationResult result) => _result = result;

    public List<string> Problems { get; } = new();

    public TokenStream Tokens => _result.Tokens;

    // A root over [start, end) of the expanded text; it must come from one original file and no REPLACING text.
    public JsonObject Root(JsonObject node, int start, int end, string what)
    {
        var origin = _result.Expanded.OriginOf(start, end);
        var startOrigin = _result.Expanded.OriginOf(start, start + 1);
        var endOrigin = _result.Expanded.OriginOf(Math.Max(start, end - 1), Math.Max(start, end - 1) + 1);
        var range = origin.Range;
        var text = Read(range.File);
        var slice = text is null ? null : NativeSyntax.Slice(text, range.StartLine, range.StartColumn, range.EndLine, range.EndColumn);
        var replacing = origin.FromReplacement && startOrigin.Unit == endOrigin.Unit && slice is not null ? Replacing(origin.Unit) : null;
        if (origin.FromReplacement && replacing is null || startOrigin.Unit != endOrigin.Unit || slice is null)
        {
            Problems.Add($"syntax-range-unavailable:{what}");
        }

        node["range"] = new JsonObject
        {
            ["path"] = Path.GetFullPath(range.File),
            ["start"] = new JsonArray(range.StartLine, range.StartColumn),
            ["end"] = new JsonArray(range.EndLine, range.EndColumn),
        };
        node["textSha256"] = NativeSyntax.Sha256(slice ?? string.Empty);
        if (replacing is not null)
        {
            node["replacing"] = replacing;
        }

        return node;
    }

    // Copybook text changed by COPY ... REPLACING is proved by the including statement: one range per
    // non-blank source line, program area only, so the gate can re-read it and re-apply the pseudo-text pairs.
    private JsonObject? Replacing(int unitId)
    {
        var unit = _result.Expanded.Units[unitId];
        if (!unit.Replacing || unit.IncludedAt is not { } at || unit.ParentId < 0)
        {
            return null;
        }

        var parent = _result.Expanded.Units[unit.ParentId].Source;
        var text = Read(parent.Path);
        if (text is null)
        {
            return null;
        }

        var pieces = new JsonArray();
        for (var number = at.StartLine; number <= at.EndLine; number++)
        {
            var line = parent.Lines.Count >= number && parent.Lines[number - 1].Number == number
                ? parent.Lines[number - 1]
                : parent.Lines.FirstOrDefault(l => l.Number == number);
            if (line is null || line.HadTabs || line.Kind is not (SourceLineKind.Code or SourceLineKind.Blank or SourceLineKind.Comment))
            {
                return null;
            }

            if (line.Kind is not SourceLineKind.Code)
            {
                continue;
            }

            var first = line.Format == SourceFormat.Fixed ? SourceLine.ProgramAreaStart : 1;
            var last = line.Format == SourceFormat.Fixed ? Math.Min(SourceLine.ProgramAreaEnd, line.Text.Length) : line.Text.Length;
            first = number == at.StartLine ? Math.Max(first, at.StartColumn) : first;
            last = number == at.EndLine ? Math.Min(last, at.EndColumn) : last;
            while (first <= last && char.IsWhiteSpace(line.Text[first - 1]))
            {
                first++;
            }

            while (last >= first && char.IsWhiteSpace(line.Text[last - 1]))
            {
                last--;
            }

            if (first > last)
            {
                continue;
            }

            var slice = NativeSyntax.Slice(text, number, first, number, last);
            if (slice is null || slice != line.Text.Substring(first - 1, last - first + 1))
            {
                return null;
            }

            pieces.Add(new JsonObject
            {
                ["path"] = Path.GetFullPath(parent.Path),
                ["start"] = new JsonArray(number, first),
                ["end"] = new JsonArray(number, last),
                ["textSha256"] = NativeSyntax.Sha256(slice),
            });
        }

        return pieces.Count == 0 ? null : new JsonObject { ["statement"] = pieces };
    }

    public JsonObject RootTokens(JsonObject node, int firstToken, int endToken, string what)
    {
        var tokens = _result.Tokens;
        return Root(node, tokens[firstToken].Start, tokens[endToken - 1].End, what);
    }

    public (int Line, int Column) Position(int expandedOffset)
    {
        var range = _result.Expanded.OriginOf(expandedOffset, expandedOffset + 1).Range;
        return (range.StartLine, range.StartColumn);
    }

    private string? Read(string path)
    {
        if (!_files.TryGetValue(path, out var text))
        {
            text = File.Exists(path) ? File.ReadAllText(path, Encoding.Latin1) : null;
            _files[path] = text;
        }

        return text;
    }
}
