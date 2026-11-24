using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Starforge.Cobol.Copy;
using Starforge.Cobol.Deps;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Source;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Emit;

// The symbolic platform-replacement contract for a DFHAID or DFHBMSCA copy: no vendor values, only the program's uses
// of the symbolic names, each bound to syntax (and for DFHBMSCA to a symbolic map field). Any use that cannot be
// represented fails the whole binding, so a consumer never sees a partial contract as complete.
internal sealed class PlatformContract
{
    private static readonly HashSet<string> TerminalEvents = new(
        new[] { "DFHENTER", "DFHCLEAR", "DFHPA1", "DFHPA2", "DFHPA3" }.Concat(Enumerable.Range(1, 24).Select(i => $"DFHPF{i}")),
        StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, char> BmsAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DFHBMASK"] = 'A', ["DFHBMPRF"] = 'A', ["DFHBMFSE"] = 'A', ["DFHBMUNP"] = 'A', ["DFHBMPRO"] = 'A', ["DFHBMBRY"] = 'A',
        ["DFHBMDAR"] = 'A', ["DFHBMASF"] = 'A', ["DFHBMASB"] = 'A', ["DFHNEUTR"] = 'C', ["DFHGREEN"] = 'C', ["DFHDFCOL"] = 'C',
        ["DFHBLUE"] = 'C', ["DFHRED"] = 'C', ["DFHPINK"] = 'C', ["DFHTURQ"] = 'C', ["DFHYELLO"] = 'C', ["DFHUNDLN"] = 'H',
        ["DFHBLINK"] = 'H', ["DFHREVRS"] = 'H',
    };

    private static readonly HashSet<string> ConditionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "EIBAID", "=", "NOT", "AND", "OR", "IS", "EQUAL", "TO",
    };

    private readonly CompilationResult _result;
    private readonly TokenStream _tokens;
    private readonly int[] _lineStarts;
    private readonly Dictionary<string, JsonElement?> _layouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _projections = new(StringComparer.Ordinal);

    // Set for the native dependency contract: usages become Starforge nodes rooted in their source range and data
    // declarations come from the native exporter, instead of the cobol-rekt shaped trees of the emitted tree.
    private readonly Native.SyntaxRoots? _roots;
    private readonly Func<DataItem, JsonObject?>? _entry;

    private PlatformContract(CompilationResult result, Native.SyntaxRoots? roots = null, Func<DataItem, JsonObject?>? entry = null)
    {
        _result = result;
        _roots = roots;
        _entry = entry;
        _tokens = result.Tokens;
        var content = result.Expanded.Content;
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        _lineStarts = starts.ToArray();
    }

    // Returns the runtimeContract object, or null with the reason no complete binding exists.
    public static JsonObject? Bind(CompilationResult result, string name, string projectionPath, string? contractsPath, out string? failure)
        => Bind(new PlatformContract(result), name, projectionPath, contractsPath, out failure);

    public static JsonObject? BindNative(
        CompilationResult result, Native.SyntaxRoots roots, Func<DataItem, JsonObject?> entry, string name, string projectionPath, string? contractsPath, out string? failure)
        => Bind(new PlatformContract(result, roots, entry), name, projectionPath, contractsPath, out failure);

    private static JsonObject? Bind(PlatformContract binder, string name, string projectionPath, string? contractsPath, out string? failure)
    {
        failure = null;
        if (contractsPath is null || !File.Exists(contractsPath))
        {
            failure = "no platform replacement profile (platform-contracts.json) found";
            return null;
        }

        using var profile = JsonDocument.Parse(File.ReadAllBytes(contractsPath));
        if (!profile.RootElement.TryGetProperty("copybooks", out var copybooks) || !copybooks.TryGetProperty(name, out var definition)
            || profile.RootElement.GetProperty("profileName").GetString() is not { Length: > 0 } profileName)
        {
            failure = $"platform replacement profile has no {name} recipe";
            return null;
        }

        var usages = name.Equals("DFHAID", StringComparison.OrdinalIgnoreCase) ? binder.TerminalUsages(out failure) : binder.BmsUsages(out failure);
        if (usages is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["name"] = name.ToUpperInvariant(),
            ["profileName"] = profileName,
            ["profileKind"] = profile.RootElement.GetProperty("profileKind").GetString(),
            ["sourcePath"] = Path.GetFullPath(contractsPath),
            ["sourceSha256"] = Sha256(contractsPath),
            ["outputPath"] = Path.GetFullPath(projectionPath),
            ["outputSha256"] = Sha256(projectionPath),
            ["definition"] = JsonNode.Parse(definition.GetRawText()),
            ["resolutionKind"] = "symbolic-platform-replacement",
            ["runtimeMapping"] = "implementation-required",
            ["sourceValues"] = "not-supplied",
            ["bindingStatus"] = "complete",
            ["usages"] = new JsonArray(usages.OrderBy(u => u.Token).Select(u => (JsonNode)u.Usage).ToArray()),
        };
    }

    private IEnumerable<(ProcedureDivision Procedure, ProcedureBindings Bindings, ProgramData Data)> Procedures()
    {
        var programs = _result.Syntax.AllPrograms().ToList();
        for (var p = 0; p < programs.Count && p < _result.Programs.Count; p++)
        {
            if (programs[p].Procedure is { } procedure)
            {
                yield return (procedure, p < _result.Bindings.Count ? _result.Bindings[p] : ProcedureBindings.Empty, _result.Programs[p]);
            }
        }
    }

    private List<(int Token, JsonObject Usage)>? TerminalUsages(out string? failure)
    {
        failure = null;
        var usages = new List<(int Token, JsonObject Usage)>();
        var covered = new HashSet<int>();
        foreach (var (procedure, _, _) in Procedures())
        {
            foreach (var statement in procedure.AllStatements())
            {
                foreach (var condition in Conditions(statement))
                {
                    if (!TerminalCondition(condition.Span, usages, covered, out failure))
                    {
                        return null;
                    }
                }

                if (statement is EvaluateStatement { Subjects: [{ Kind: EvaluateOperandKind.Expression, Value: DataRef subject }] } evaluate
                    && IsWord(subject.Span, "EIBAID"))
                {
                    var subjectSyntax = _roots is not null
                        ? Node("EvaluateSubject", Token(Text(subject.Span.FirstToken)))
                        : Rule("EvaluateSelectContext", Word(Text(subject.Span.FirstToken)));
                    usages.Add((subject.Span.FirstToken, Usage("EvaluateSelectContext", subject.Span, subjectSyntax, "evaluate-subject")));
                    covered.Add(subject.Span.FirstToken);
                    foreach (var alternative in evaluate.Whens.SelectMany(w => w.Alternatives))
                    {
                        if (alternative is not [{ Kind: EvaluateOperandKind.Expression, Value: DataRef value, Thru: null, Not: false }]
                            || value.Span.TokenCount != 1 || !TerminalEvents.Contains(Text(value.Span.FirstToken))
                            || value.Span.FirstToken == 0 || !Text(value.Span.FirstToken - 1).Equals("WHEN", StringComparison.OrdinalIgnoreCase))
                        {
                            failure = $"EVALUATE EIBAID has a WHEN that is not a single named terminal event (line {Line(evaluate.Span.FirstToken)})";
                            return null;
                        }

                        var span = new NodeSpan(value.Span.FirstToken - 1, value.Span.EndToken, _tokens[value.Span.FirstToken - 1].Start, value.Span.End);
                        var syntax = _roots is not null
                            ? Node("EvaluateWhen", Token(Text(span.FirstToken)), Token(Text(value.Span.FirstToken)))
                            : Rule("EvaluateWhenContext", Token(Text(span.FirstToken)),
                                Rule("EvaluateConditionContext", Rule("EvaluateValueContext", Word(Text(value.Span.FirstToken)))));
                        usages.Add((span.FirstToken, Usage("EvaluateWhenContext", span, syntax, "evaluate-when")));
                        covered.Add(value.Span.FirstToken);
                    }
                }
            }

            for (var i = procedure.Span.FirstToken; i < procedure.Span.EndToken; i++)
            {
                if (_tokens[i].Kind == TokenKind.Word && (TerminalEvents.Contains(Text(i)) || Text(i).Equals("EIBAID", StringComparison.OrdinalIgnoreCase)
                    || Text(i).Equals("DFHAID", StringComparison.OrdinalIgnoreCase) && !(i > 0 && Text(i - 1).Equals("COPY", StringComparison.OrdinalIgnoreCase)))
                    && !covered.Contains(i))
                {
                    failure = $"{Text(i).ToUpperInvariant()} is used outside an EIBAID comparison (line {Line(i)})";
                    return null;
                }
            }
        }

        return usages;
    }

    private static IEnumerable<Condition> Conditions(Statement statement) => statement switch
    {
        IfStatement s => [s.Condition],
        PerformStatement s => (s.Until is null ? [] : new[] { s.Until }).Concat(s.Varying.Select(v => v.Until)),
        EvaluateStatement s => s.Subjects.Concat(s.Whens.SelectMany(w => w.Alternatives.SelectMany(a => a)))
            .Select(o => o.Condition).OfType<Condition>(),
        SearchStatement s => s.Whens.Select(w => w.Condition),
        _ => [],
    };

    // A condition made only of EIBAID comparisons becomes one ConditionContext; one ANDed with application relations
    // contributes each EIBAID relation as its own conjunct. Anything else (OR with application terms, parentheses) fails.
    private bool TerminalCondition(NodeSpan span, List<(int, JsonObject)> usages, HashSet<int> covered, out string? failure)
    {
        failure = null;
        var indices = Enumerable.Range(span.FirstToken, span.TokenCount).ToList();
        if (!indices.Any(IsTerminal))
        {
            return true;
        }

        if (indices.All(i => _tokens[i].Kind != TokenKind.AlphanumericLiteral && (ConditionWords.Contains(Text(i)) || TerminalEvents.Contains(Text(i)))))
        {
            var tree = ConditionTree(indices);
            if (tree is null)
            {
                failure = $"unsupported EIBAID condition shape (line {Line(span.FirstToken)})";
                return false;
            }

            usages.Add((span.FirstToken, Usage("ConditionContext", span, tree, null)));
            covered.UnionWith(indices);
            return true;
        }

        var conjuncts = new List<List<int>> { new() };
        var depth = 0;
        foreach (var i in indices)
        {
            depth += _tokens[i].Kind == TokenKind.LeftParen ? 1 : _tokens[i].Kind == TokenKind.RightParen ? -1 : 0;
            if (depth == 0 && _tokens[i].Kind == TokenKind.Word && Text(i).Equals("AND", StringComparison.OrdinalIgnoreCase))
            {
                conjuncts.Add(new());
            }
            else if (depth == 0 && _tokens[i].Kind == TokenKind.Word && Text(i).Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                failure = $"EIBAID comparison ORed with application conditions (line {Line(span.FirstToken)})";
                return false;
            }
            else
            {
                conjuncts[^1].Add(i);
            }
        }

        foreach (var conjunct in conjuncts.Where(c => c.Any(IsTerminal)))
        {
            var next = 0;
            var simple = conjunct.Count >= 3 && Text(conjunct[0]).Equals("EIBAID", StringComparison.OrdinalIgnoreCase)
                ? Relation(conjunct, 0, out next) : null;
            if (simple is null || next != conjunct.Count)
            {
                failure = $"unsupported EIBAID conjunct (line {Line(conjunct[0])})";
                return false;
            }

            var part = new NodeSpan(conjunct[0], conjunct[^1] + 1, _tokens[conjunct[0]].Start, _tokens[conjunct[^1]].End);
            usages.Add((part.FirstToken, Usage("SimpleConditionContext", part, simple, "application-and-conjunct")));
            covered.UnionWith(conjunct);
        }

        return true;
    }

    // condition := relation ((AND|OR) (relation | event))*, relation := EIBAID [IS] [NOT] (= | EQUAL [TO]) event.
    private JsonObject? ConditionTree(List<int> indices)
    {
        if (!Text(indices[0]).Equals("EIBAID", StringComparison.OrdinalIgnoreCase) || Relation(indices, 0, out var position) is not { } first)
        {
            return null;
        }

        var children = new List<JsonNode> { first };
        while (position < indices.Count)
        {
            var conjunction = Text(indices[position]);
            if (conjunction.ToUpperInvariant() is not ("AND" or "OR") || position + 1 >= indices.Count)
            {
                return null;
            }

            position++;
            JsonObject simple;
            if (Text(indices[position]).Equals("EIBAID", StringComparison.OrdinalIgnoreCase))
            {
                if (Relation(indices, position, out position) is not { } relation)
                {
                    return null;
                }

                simple = relation;
            }
            else if (TerminalEvents.Contains(Text(indices[position])))
            {
                simple = _roots is not null
                    ? Node("RelationCondition", Token(Text(indices[position++])))
                    : Rule("SimpleConditionContext", Word(Text(indices[position++])));
            }
            else
            {
                return null;
            }

            children.Add(_roots is not null ? Node("CombinedCondition", Token(conjunction), simple) : Rule("AdditionalConditionContext", Token(conjunction), simple));
        }

        return _roots is not null ? Node("Condition", children.ToArray()) : Rule("ConditionContext", children.ToArray());
    }

    private JsonObject? Relation(List<int> indices, int start, out int next)
    {
        next = start;
        var position = start + 1;
        var op = new List<JsonNode>();
        string Peek() => position < indices.Count ? Text(indices[position]).ToUpperInvariant() : "";
        if (Peek() == "IS")
        {
            op.Add(Token(Text(indices[position++])));
        }

        if (Peek() == "NOT")
        {
            op.Add(Token(Text(indices[position++])));
        }

        var basis = new List<JsonNode>();
        if (Peek() == "=")
        {
            basis.Add(Token(Text(indices[position++])));
        }
        else if (Peek() == "EQUAL")
        {
            basis.Add(Token(Text(indices[position++])));
            if (Peek() == "TO")
            {
                basis.Add(Token(Text(indices[position++])));
            }
        }
        else
        {
            return null;
        }

        if (position >= indices.Count || !TerminalEvents.Contains(Text(indices[position])))
        {
            return null;
        }

        next = position + 1;
        if (_roots is not null)
        {
            return Node("RelationCondition", indices[start..next].Select(i => (JsonNode)Token(Text(i))).ToArray());
        }

        op.Add(Rule("CharBasedOperatorContext", basis.ToArray()));
        var simple = Rule("SimpleConditionContext", Word(Text(indices[start])),
            Rule("RelationCombinedComparisonContext", Rule("RelationalOperatorContext", op.ToArray()), Word(Text(indices[position]))));
        return simple;
    }

    private List<(int Token, JsonObject Usage)>? BmsUsages(out string? failure)
    {
        failure = null;
        var usages = new List<(int Token, JsonObject Usage)>();
        var covered = new HashSet<int>();
        var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (procedure, bindings, data) in Procedures())
        {
            foreach (var move in procedure.AllStatements().OfType<MoveStatement>())
            {
                var symbolic = move.Source is DataRef { Subscripts.Count: 0 } source && BmsAttributes.ContainsKey(source.Name.Name);
                var targets = move.Targets.Select(t => (Ref: t as DataRef, Field: t is DataRef r ? MapField(r, bindings) : null)).ToList();
                if (!symbolic && targets.All(t => t.Field is null))
                {
                    continue;
                }

                JsonObject? usage;
                try
                {
                    usage = BmsUsage(move, targets, bindings, out failure);
                }
                catch (NativeEntryException error)
                {
                    (usage, failure) = (null, error.Message);
                }

                if (usage is null)
                {
                    failure = $"{failure} (line {Line(move.Span.FirstToken)})";
                    return null;
                }

                usages.Add((move.Span.FirstToken, usage));
                covered.UnionWith(Enumerable.Range(move.Span.FirstToken, move.Span.TokenCount));
            }

            foreach (var projection in _result.Units.Select(u => Projection(u.Path)).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                foreach (var map in Layout(projection)?.GetProperty("maps").EnumerateArray() ?? Enumerable.Empty<JsonElement>())
                {
                    foreach (var field in map.GetProperty("fields").EnumerateArray())
                    {
                        foreach (var suffix in "ACH")
                        {
                            fieldNames.Add(field.GetProperty("name").GetString() + suffix);
                        }
                    }
                }
            }

            for (var i = procedure.Span.FirstToken; i < procedure.Span.EndToken; i++)
            {
                if (_tokens[i].Kind == TokenKind.Word && (BmsAttributes.ContainsKey(Text(i)) || fieldNames.Contains(Text(i))
                    || Text(i).Equals("DFHBMSCA", StringComparison.OrdinalIgnoreCase) && !(i > 0 && Text(i - 1).Equals("COPY", StringComparison.OrdinalIgnoreCase)))
                    && !covered.Contains(i))
                {
                    failure = $"{Text(i).ToUpperInvariant()} is used outside a BMS attribute MOVE (line {Line(i)})";
                    return null;
                }
            }
        }

        return usages;
    }

    private sealed record FieldBinding(DataItem Item, DataItem Record, string MapRecord, string Path, JsonElement Map);

    // A field of a symbolic map record (<map>I / <map>O) whose copybook has a .bms-layout.json, or one in an
    // application FILLER REDEFINES overlay of such a record.
    private FieldBinding? MapField(DataRef reference, ProcedureBindings bindings)
    {
        if (bindings.ItemOf(reference) is not { } item || item.Record is not { } record || ReferenceEquals(item, record))
        {
            return null;
        }

        var mapRecord = record;
        if (record.IsFiller && record.Redefines is { } redefined)
        {
            mapRecord = redefined;
        }

        var unit = mapRecord.Entry.Unit;
        if (unit <= 0 || unit >= _result.Units.Count || mapRecord.Name is not { Length: > 1 } recordName
            || Projection(_result.Units[unit].Path) is not { } projection || Layout(projection) is not { } layout)
        {
            return null;
        }

        var map = layout.GetProperty("maps").EnumerateArray()
            .Where(m => m.GetProperty("name").GetString() is { } n && recordName.Equals(n + "I", StringComparison.OrdinalIgnoreCase)
                || m.GetProperty("name").GetString() is { } o && recordName.Equals(o + "O", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (map.Count != 1 || item.Name is not { Length: > 1 } name || "ACH".IndexOf(char.ToUpperInvariant(name[^1])) < 0)
        {
            return null;
        }

        // Only attribute bytes (<field>A/C/H, or a one-byte overlay member) are governed by DFHBMSCA.
        var attribute = record.IsFiller
            ? item.Length == 1
            : map[0].GetProperty("fields").EnumerateArray().Any(f => name[..^1].Equals(f.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase));
        return attribute ? new FieldBinding(item, record, recordName, projection, map[0]) : null;
    }

    private JsonObject? BmsUsage(MoveStatement move, List<(DataRef? Ref, FieldBinding? Field)> targets, ProcedureBindings bindings, out string? failure)
    {
        failure = null;
        var value = Text(move.Source.Span.FirstToken);
        var normalized = value.StartsWith('\'') || value.StartsWith('"') ? value : value.ToUpperInvariant();
        string kind;
        char? attributeSuffix = null;
        if (move.Source is DataRef && BmsAttributes.TryGetValue(value, out var s))
        {
            kind = "named-attribute";
            attributeSuffix = s;
        }
        else if (move.Source is LiteralExpr && normalized is "'A'" or "\"A\"")
        {
            kind = "source-character-literal";
        }
        else if (move.Source is FigurativeExpr { Figurative: FigurativeKind.HighValue } && normalized is "HIGH-VALUE" or "HIGH-VALUES")
        {
            kind = "figurative-constant";
        }
        else
        {
            failure = $"MOVE {normalized} to a symbolic map attribute is not a named attribute, 'A' or HIGH-VALUE";
            return null;
        }

        var fields = new JsonArray();
        var declarations = new JsonArray();
        var overlays = new JsonArray();
        var targetSyntax = new List<JsonNode>();
        foreach (var (reference, field) in targets)
        {
            if (reference is null || field is null)
            {
                failure = "MOVE target is not a symbolic map field";
                return null;
            }

            var name = reference.Name.Name.ToUpperInvariant();
            var suffix = name[^1];
            var fieldName = name[..^1];
            var extended = field.Map.GetProperty("extendedAttributes").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            var valid = suffix is 'A' or 'C' or 'H'
                && field.Map.GetProperty("mode").GetString() is "OUT" or "INOUT"
                && (suffix == 'A' || extended.Contains(suffix == 'C' ? "COLOR" : "HILIGHT"))
                && (kind == "named-attribute" ? attributeSuffix == suffix : kind == "source-character-literal" ? suffix == 'A' : suffix is 'C' or 'H')
                && reference.Name.Qualifiers.Count <= 1
                && (reference.Name.Qualifiers.Count == 0 || reference.Name.Qualifiers[0].Equals(field.MapRecord, StringComparison.OrdinalIgnoreCase));
            if (!valid)
            {
                failure = $"MOVE {normalized} TO {name} does not match the field's attribute suffix or map";
                return null;
            }

            var mapName = field.Map.GetProperty("name").GetString()!;
            var mapset = field.Map.GetProperty("mapset").GetString()!;
            var layoutPath = field.Path + ".bms-layout.json";
            if (field.Record.IsFiller)
            {
                if (reference.Subscripts is not [var subscript] || subscript.Span.TokenCount != 1 || reference.Name.Qualifiers.Count != 0)
                {
                    failure = $"overlay target {name} needs exactly one simple subscript";
                    return null;
                }

                var table = field.Item.Ancestors().Where(a => a.Occurs is not null).ToList();
                if (table.Count != 1)
                {
                    failure = $"overlay target {name} is not under exactly one OCCURS group";
                    return null;
                }

                var occurrences = new JsonArray();
                var layoutFields = field.Map.GetProperty("fields").EnumerateArray().ToList();
                for (var k = 0; k < table[0].Occurs!.Max; k++)
                {
                    var offset = field.Item.Offset + k * table[0].Length;
                    var host = layoutFields.FirstOrDefault(f => f.GetProperty("offset").GetInt32() is var o && offset - o == AttributeDelta(suffix, extended));
                    if (host.ValueKind != JsonValueKind.Object || host.GetProperty("occurs").GetInt32() != 1)
                    {
                        failure = $"overlay target {name} occurrence {k + 1} does not land on a map field attribute";
                        return null;
                    }

                    occurrences.Add(new JsonObject { ["field"] = host.GetProperty("name").GetString(), ["offset"] = offset });
                }

                overlays.Add(new JsonObject
                {
                    ["name"] = name,
                    ["subscript"] = Text(subscript.Span.FirstToken).ToUpperInvariant(),
                    ["map"] = mapName,
                    ["mapset"] = mapset,
                    ["suffix"] = suffix.ToString(),
                    ["layoutPath"] = layoutPath,
                    ["layoutSha256"] = Sha256(layoutPath),
                    ["outputPath"] = field.Path,
                    ["outputSha256"] = Sha256(field.Path),
                    ["recordSyntax"] = EntrySyntax(field.Record),
                    ["memberSyntaxes"] = new JsonArray(field.Record.SelfAndDescendants().Skip(1).Select(d => (JsonNode)EntrySyntax(d)).ToArray()),
                    ["definitionSyntax"] = EntrySyntax(field.Item),
                    ["occurrences"] = occurrences,
                });
                targetSyntax.Add(Identifier(reference));
                continue;
            }

            if (!field.Map.GetProperty("fields").EnumerateArray().Any(f => f.GetProperty("name").GetString() == fieldName && f.GetProperty("occurs").GetInt32() == 1))
            {
                failure = $"{name} is not a single-occurrence field of map {mapName}";
                return null;
            }

            fields.Add(new JsonObject
            {
                ["name"] = name,
                ["map"] = mapName,
                ["mapset"] = mapset,
                ["field"] = fieldName,
                ["suffix"] = suffix.ToString(),
                ["layoutPath"] = layoutPath,
                ["layoutSha256"] = Sha256(layoutPath),
                ["outputPath"] = field.Path,
                ["outputSha256"] = Sha256(field.Path),
            });
            declarations.Add(EntrySyntax(field.Item));
            targetSyntax.Add(Identifier(reference));
        }

        if (overlays.Count > 0 && fields.Count > 0)
        {
            failure = "MOVE mixes overlay and plain symbolic map targets";
            return null;
        }

        var sending = move.Source switch
        {
            DataRef r => Identifier(r),
            LiteralExpr => Rule("LiteralContext", Token(value)),
            _ => Rule("LiteralContext", Rule("FigurativeConstantContext", Token(value))),
        };
        var span = Trimmed(move.Span);
        var syntax = _roots is not null
            ? Node("MoveStatement", Enumerable.Range(span.FirstToken, span.TokenCount)
                .Where(i => _tokens[i].Kind is not (TokenKind.Comma or TokenKind.Semicolon)).Select(i => (JsonNode)Token(Text(i))).ToArray())
            : Rule("MoveStatementContext", Token(Text(move.Span.FirstToken)),
                Rule("MoveToStatementContext", [Rule("MoveToSendingAreaContext", sending), Token(Text(move.Source.Span.EndToken)), .. targetSyntax]));
        var usage = Usage("MoveStatementContext", span, syntax, null);
        if (overlays.Count > 0)
        {
            usage["overlayBindings"] = overlays;
        }
        else if (targets.Count == 1 && targets[0].Ref!.Name.Qualifiers.Count == 0)
        {
            usage["fieldBinding"] = fields[0]!.DeepClone();
            usage["definitionSyntax"] = declarations[0]!.DeepClone();
        }
        else
        {
            usage["fieldBindings"] = fields;
            usage["definitionSyntaxes"] = declarations;
        }

        usage["valueKind"] = kind;
        return usage;
    }

    // Symbolic map field: length halfword at +0, attribute byte at +2, extended attributes from +3 in DSATTS order.
    private static int AttributeDelta(char suffix, List<string> extended) => suffix switch
    {
        'A' => 2,
        'C' => extended.IndexOf("COLOR") is >= 0 and var c ? 3 + c : -1,
        _ => extended.IndexOf("HILIGHT") is >= 0 and var h ? 3 + h : -1,
    };

    // The layout proof sits next to the generated map copybook; a hand-kept copybook the resolver preferred is
    // bound to it only when the preprocessor recorded it as token-equivalent to that generated copy.
    private string? Projection(string copybookPath)
    {
        if (!_projections.TryGetValue(copybookPath, out var projection))
        {
            projection = File.Exists(copybookPath + ".bms-layout.json") ? copybookPath : EquivalentProjection(copybookPath);
            _projections[copybookPath] = projection;
        }

        return projection;
    }

    private static string? EquivalentProjection(string copybookPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(copybookPath));
        while (folder is not null && !Directory.Exists(Path.Combine(folder, CopybookResolver.PreprocessedFolder)))
        {
            folder = Path.GetDirectoryName(folder);
        }

        return folder is not null
            && EquivalentMaps.GetOrAdd(Path.Combine(folder, CopybookResolver.PreprocessedFolder), IndexEquivalentMaps)
                .TryGetValue(Sha256(copybookPath), out var matches) && matches.Count == 1
            ? matches[0]
            : null;
    }

    private static readonly ConcurrentDictionary<string, Dictionary<string, List<string>>> EquivalentMaps = new(StringComparer.Ordinal);

    // Dot folders inside .preprocessed hold earlier snapshots, never the current projection.
    private static Dictionary<string, List<string>> IndexEquivalentMaps(string preprocessed)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var pending = new Stack<string>([preprocessed]);
        var sidecars = new List<string>();
        while (pending.TryPop(out var folder))
        {
            sidecars.AddRange(Directory.EnumerateFiles(folder, "*.preprocess.json"));
            foreach (var child in Directory.EnumerateDirectories(folder).Where(d => !Path.GetFileName(d).StartsWith('.')))
            {
                pending.Push(child);
            }
        }

        foreach (var sidecar in sidecars.Order(StringComparer.Ordinal))
        {
            var projection = sidecar[..^".preprocess.json".Length];
            if (!File.Exists(projection + ".bms-layout.json"))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(sidecar));
            using var layout = JsonDocument.Parse(File.ReadAllBytes(projection + ".bms-layout.json"));
            if (document.RootElement.TryGetProperty("originalSourceSha256", out var original) && original.GetString() is { } sha
                && layout.RootElement.TryGetProperty("copybookMatch", out var match) && match.GetString() == "token-equivalent")
            {
                if (!index.TryGetValue(sha, out var list))
                {
                    index[sha] = list = [];
                }

                list.Add(projection);
            }
        }

        return index;
    }

    private JsonElement? Layout(string copybookPath)
    {
        if (_layouts.TryGetValue(copybookPath, out var cached))
        {
            return cached;
        }

        JsonElement? layout = null;
        var path = copybookPath + ".bms-layout.json";
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.TryGetProperty("status", out var status) && status.GetString() == "complete"
                && root.TryGetProperty("outputSha256", out var output) && output.GetString() == Sha256(copybookPath))
            {
                layout = root.Clone();
            }
        }

        _layouts[copybookPath] = layout;
        return layout;
    }

    private JsonObject Identifier(DataRef reference)
    {
        var first = reference.Span.FirstToken;
        var parts = new List<JsonNode> { Rule("VariableUsageNameContext", Rule("CobolWordContext", Token(Text(first)))) };
        for (var i = first + 1; i < reference.Span.EndToken; i++)
        {
            var word = Text(i).ToUpperInvariant();
            if (word is "OF" or "IN" && i + 1 < reference.Span.EndToken)
            {
                parts.Add(Rule("InDataContext", Token(Text(i)), Rule("VariableUsageNameContext", Rule("CobolWordContext", Token(Text(i + 1))))));
                i++;
            }
            else if (_tokens[i].Kind == TokenKind.LeftParen && i + 2 < reference.Span.EndToken)
            {
                parts.Add(Rule("TableCallContext", Token(Text(i)), Word(Text(i + 1)), Token(Text(i + 2))));
                i += 2;
            }
            else
            {
                parts.Add(Token(Text(i)));
            }
        }

        return Rule("GeneralIdentifierContext", Rule("QualifiedDataNameContext", parts.ToArray()));
    }

    // Data description entry as level, name and clause groups; consumers read its tokens.
    // The entry's own description up to its period, shaped like the data-description grammar rules.
    private JsonObject EntrySyntax(DataItem item)
    {
        if (_entry is not null)
        {
            return _entry(item) ?? throw new NativeEntryException(item.Name ?? "FILLER");
        }

        var span = item.Entry.Span;
        var children = new List<JsonNode> { Rule("LevelNumberContext", Token(Text(span.FirstToken))) };
        var i = span.FirstToken + 1;
        if (i < span.EndToken && _tokens[i].Kind == TokenKind.Word && !IsClauseKeyword(Text(i)))
        {
            children.Add(Text(i).Equals("FILLER", StringComparison.OrdinalIgnoreCase)
                ? Rule("EntryNameContext", Token(Text(i++)))
                : Rule("EntryNameContext", DataName(Text(i++))));
        }

        while (i < span.EndToken && _tokens[i].Kind != TokenKind.Period)
        {
            var start = i++;
            while (i < span.EndToken && _tokens[i].Kind != TokenKind.Period && !IsClauseKeyword(Text(i))
                   || i < span.EndToken && IsUsageWord(Text(i)) && Text(start).Equals("USAGE", StringComparison.OrdinalIgnoreCase))
            {
                i++;
            }

            children.Add(Clause(start, i));
        }

        if (i < span.EndToken)
        {
            children.Add(Rule("Dot_fsContext", Token(Text(i))));
        }

        return Rule("DataDescriptionEntryFormat1Context", children.ToArray());
    }

    private JsonObject Clause(int start, int end)
    {
        var keyword = Text(start).ToUpperInvariant();
        var tokens = Enumerable.Range(start, end - start).ToList();
        switch (keyword)
        {
            case "PIC" or "PICTURE":
                var picture = tokens.Skip(1).SkipWhile(t => Text(t).Equals("IS", StringComparison.OrdinalIgnoreCase)).ToList();
                return Rule("DataPictureClauseContext", tokens.Take(tokens.Count - picture.Count).Select(t => (JsonNode)Token(Text(t)))
                    .Append(Rule("PictureStringContext", Rule("CharStringContext", picture.Select(t => (JsonNode)Token(Text(t))).ToArray()))).ToArray());
            case "REDEFINES" when tokens.Count == 2:
                return Rule("DataRedefinesClauseContext", Token(Text(start)), DataName(Text(start + 1)));
            case "OCCURS":
                return Rule("DataOccursClauseContext", tokens.Select(t => _tokens[t].Kind == TokenKind.IntegerLiteral
                    ? Rule("IntegerLiteralContext", Token(Text(t))) : (JsonNode)Token(Text(t))).ToArray());
            case var usage when IsUsageWord(usage) || usage == "USAGE":
                return Rule("DataUsageClauseContext", tokens.Select(t => IsUsageWord(Text(t))
                    ? Rule("UsageFormatContext", Token(Text(t))) : (JsonNode)Token(Text(t))).ToArray());
            default:
                var rule = keyword is "VALUE" or "VALUES" ? "DataValueClauseContext" : "DataDescriptionClauseContext";
                return Rule(rule, tokens.Select(t => (JsonNode)Token(Text(t))).ToArray());
        }
    }

    private static bool IsClauseKeyword(string word) => word.ToUpperInvariant() is "PIC" or "PICTURE" or "REDEFINES" or "OCCURS"
        or "VALUE" or "VALUES" or "USAGE" || IsUsageWord(word);

    private static bool IsUsageWord(string word) => word.ToUpperInvariant() is "COMP" or "COMPUTATIONAL" or "BINARY" or "COMP-3"
        or "COMP-4" or "COMP-5" or "COMPUTATIONAL-3" or "COMPUTATIONAL-4" or "COMPUTATIONAL-5" or "PACKED-DECIMAL" or "DISPLAY";

    private static JsonObject DataName(string text) => Rule("DataNameContext", Rule("CobolWordContext", Token(text)));

    // A statement span can include its separator period; usage text ends at the last syntax token.
    private NodeSpan Trimmed(NodeSpan span)
    {
        var end = span.EndToken;
        while (end > span.FirstToken + 1 && _tokens[end - 1].Kind is TokenKind.Period or TokenKind.Comma or TokenKind.Semicolon)
        {
            end--;
        }

        return new NodeSpan(span.FirstToken, end, _tokens[span.FirstToken].Start, _tokens[end - 1].End);
    }

    private JsonObject Usage(string grammarRule, NodeSpan span, JsonObject syntax, string? role)
    {
        var start = _tokens[span.FirstToken].Start;
        var end = _tokens[span.EndToken - 1].End;
        var line = Array.BinarySearch(_lineStarts, start);
        line = line >= 0 ? line : ~line - 1;
        var text = _result.Expanded.Content[start..end];
        // Fixed-format text drops the sequence and indicator areas; continuation lines get them back as blanks.
        if (text.Contains('\n') && _result.Program.Source.InitialFormat == SourceFormat.Fixed)
        {
            text = text.Replace("\n", "\n       ", StringComparison.Ordinal);
        }

        if (_roots is not null)
        {
            grammarRule = (string)syntax["node"]!;
            syntax = _roots.RootTokens(syntax, span.FirstToken, span.EndToken, "platform-usage:" + grammarRule);
        }

        var usage = new JsonObject { ["grammarRule"] = grammarRule };
        if (role is not null)
        {
            usage["conditionRole"] = role;
        }

        usage["sourceText"] = text;
        usage["syntax"] = syntax;
        usage["line"] = line + 1;
        // Columns are 0-based from the physical line start, which fixed format puts 6 + 1 dropped columns earlier.
        usage["column"] = start - _lineStarts[line] + (_result.Program.Source.InitialFormat == SourceFormat.Fixed ? 7 : 0);
        usage["coordinateSpace"] = "expanded-parser-input";
        return usage;
    }

    private bool IsTerminal(int index) => _tokens[index].Kind == TokenKind.Word
        && (TerminalEvents.Contains(Text(index)) || Text(index).Equals("EIBAID", StringComparison.OrdinalIgnoreCase));

    private bool IsWord(NodeSpan span, string word) => span.TokenCount == 1 && Text(span.FirstToken).Equals(word, StringComparison.OrdinalIgnoreCase);

    private string Text(int index) => _tokens.Text(index);

    private int Line(int token) => _tokens.ProgramLine(token);

    private static JsonObject Rule(string rule, params JsonNode[] children) => new() { ["rule"] = rule, ["children"] = new JsonArray(children) };

    private static JsonObject Token(string text) => new() { ["token"] = text };

    private static JsonObject Node(string kind, params JsonNode[] children) => Native.NativeSyntax.Node(kind, children);

    private sealed class NativeEntryException(string name) : Exception("data entry has no native syntax: " + name);

    private static JsonObject Word(string text) =>
        Rule("ArithmeticExpressionContext", Rule("MultDivsContext", Rule("PowersContext", Rule("BasisContext", Rule("GeneralIdentifierContext",
            Rule("QualifiedDataNameContext", Rule("VariableUsageNameContext", Rule("CobolWordContext", Token(text)))))))));

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
