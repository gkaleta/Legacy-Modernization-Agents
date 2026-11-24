using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Native;

// XCTL and INQUIRE PROGRAM operands that are not a single constant: an entry of a read-only table redefining
// literal VALUE items, or (XCTL only) the program names the structured assignments reaching the operand can hold.
internal sealed partial class NativeCics
{
    private IEnumerable<JsonObject> ProgramTargets(Command command, string effect)
    {
        var option = command.Single("PROGRAM");
        var argument = option.Argument!;
        List<string> names;
        JsonObject proof;
        if (argument.Kind == Exec.ExecArgumentKind.DataRef && TableR().Match(argument.Text) is { Success: true } table)
        {
            (names, proof) = Table(command, table.Groups[1].Value, table.Groups[2].Value, 1, 8);
        }
        else
        {
            try
            {
                var (name, constant) = Name(command, option, 1, 8);
                (names, proof) = ([name], constant);
            }
            catch (Problem) when (effect == "transfer-control" && argument.Kind == Exec.ExecArgumentKind.DataRef && NameR().IsMatch(argument.Text))
            {
                try
                {
                    var (values, reaching) = FlowReaching(command, option, 1, 8);
                    (names, proof) = (values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList(), reaching);
                }
                catch (Problem)
                {
                    if (SlotTargets(command, argument.Text, 1, 8) is not { } slot)
                    {
                        throw;
                    }

                    (names, proof) = slot;
                }
            }
        }

        foreach (var name in names)
        {
            var binding = Program(name.ToUpperInvariant());
            binding["targetResolution"] = proof.DeepClone();
            binding["controlEffect"] = effect;
            yield return binding;
        }
    }

    // NAME(SUBSCRIPT) over NAME's OCCURS group redefining an area of literal VALUE items no statement writes.
    private (List<string> Names, JsonObject Proof) Table(Command command, string operand, string subscript, int minimum, int maximum)
    {
        var unit = command.Unit;
        var data = _data.DataOf(unit) ?? throw new Problem("table:" + operand);
        var member = data.Symbols.Resolve(operand).Item ?? throw new Problem("table-name:" + operand);
        if (data.Symbols.Resolve(subscript).Item is not { Occurs: null } index || index.IsGroup)
        {
            throw new Problem("table-subscript:" + subscript);
        }

        var occurs = member.Parent;
        var redefining = occurs?.Parent;
        var redefined = redefining?.Redefines;
        if (member.Occurs is not null || occurs?.Occurs is not { DependingOn: null, HasTo: false } clause || clause.Min != clause.Max
            || redefining is not { Occurs: null } || redefined is not { Occurs: null, Redefines: null }
            || redefining.Section is not (DataSectionKind.WorkingStorage or DataSectionKind.LocalStorage)
            || redefining.External || redefining.Record.External || redefining.Global
            || !_data.ReadOnly(unit, redefined, NameOptions) || !_data.ReadOnly(unit, redefining, NameOptions))
        {
            throw new Problem("table-shape:" + operand);
        }

        JsonObject Entry(DataItem item) => _data.Entry(item) ?? throw new Problem("table-entry:" + operand);
        string Text(DataItem item) => string.Concat(Words(Entry(item)));
        var ancestors = redefining.Ancestors().Reverse().ToList();
        var members = occurs.Children.ToList();
        var values = redefined.Children.ToList();
        if (ancestors.Count == 0 || ancestors[0].Level != 1 || ancestors.Any(a => !GroupR().IsMatch(Text(a)))
            || members.Any(m => m.IsGroup || m.Occurs is not null || !MemberR().IsMatch(Text(m)))
            || values.Count == 0 || values.Any(v => v.IsGroup || v.Occurs is not null)
            || !Regex.IsMatch(Text(redefining), @"^\d+[A-Za-z][A-Za-z0-9-]*REDEFINES[A-Za-z][A-Za-z0-9-]*\.$", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(Text(occurs), @"^\d+[A-Za-z][A-Za-z0-9-]*OCCURS0*[1-9][0-9]{0,3}(?:TIMES)?\.$", RegexOptions.IgnoreCase)
            || !GroupR().IsMatch(Text(redefined)))
        {
            throw new Problem("table-shape:" + operand);
        }

        var area = new StringBuilder();
        foreach (var value in values)
        {
            var text = Text(value);
            var alphanumeric = Regex.Match(text, """^\d+(?:[A-Za-z][A-Za-z0-9-]*)?PIC(?:TURE)?(?:IS)?X\(0*([1-9][0-9]{0,3})\)VALUE(?:IS)?(['"])([^'"]*)\2\.$""", RegexOptions.IgnoreCase);
            var numeric = Regex.Match(text, @"^\d+(?:[A-Za-z][A-Za-z0-9-]*)?PIC(?:TURE)?(?:IS)?9\(0*([1-9][0-9]?)\)VALUE(?:IS)?([0-9]+)\.$", RegexOptions.IgnoreCase);
            if (alphanumeric.Success && alphanumeric.Groups[3].Length <= int.Parse(alphanumeric.Groups[1].Value))
            {
                area.Append(alphanumeric.Groups[3].Value.PadRight(int.Parse(alphanumeric.Groups[1].Value)));
            }
            else if (numeric.Success && numeric.Groups[2].Length <= int.Parse(numeric.Groups[1].Value))
            {
                area.Append(numeric.Groups[2].Value.PadLeft(int.Parse(numeric.Groups[1].Value), '0'));
            }
            else
            {
                throw new Problem("table-value:" + operand);
            }
        }

        var offset = member.Offset - occurs.Offset;
        var occurrences = new List<string?>();
        for (var i = 0; i < clause.Max; i++)
        {
            var start = i * occurs.Length + offset;
            if (start >= area.Length)
            {
                occurrences.Add(null);
                continue;
            }

            var value = start + member.Length <= area.Length ? area.ToString(start, member.Length).TrimEnd(' ') : string.Empty;
            if (!ProgramNameR().IsMatch(value) || value.Length < minimum || value.Length > maximum)
            {
                throw new Problem("table-occurrence:" + operand);
            }

            occurrences.Add(value);
        }

        var names = occurrences.Where(v => v is not null).Select(v => v!).Distinct().ToList();
        return (names, new JsonObject
        {
            ["kind"] = "immutable-redefined-table-values",
            ["operand"] = operand,
            ["subscript"] = subscript,
            ["definitionSyntax"] = Entry(member),
            ["ancestorSyntax"] = new JsonArray(ancestors.Select(a => (JsonNode)Entry(a)).ToArray()),
            ["redefiningSyntax"] = Entry(redefining),
            ["occursSyntax"] = Entry(occurs),
            ["memberSyntax"] = new JsonArray(members.Select(m => (JsonNode)Entry(m)).ToArray()),
            ["redefinedSyntax"] = Entry(redefined),
            ["valueSyntax"] = new JsonArray(values.Select(v => (JsonNode)Entry(v)).ToArray()),
            ["occurrenceValues"] = new JsonArray(occurrences.Select(v => (JsonNode?)v).ToArray()),
            ["usageProof"] = "single-native-program:read-only-table-references",
        });
    }

    // The values the assignments reaching the operand over the structured procedure can store, with their proofs.
    private (List<string?> Values, JsonObject Proof) FlowReaching(Command command, Option option, int minimum, int maximum)
    {
        var operand = option.Argument!.Text;
        var unit = command.Unit;
        var (item, definition, ancestors, shape) = ReachingItem(command, operand);
        var length = int.Parse(shape.Groups[2].Value);
        (List<Statement> Assignments, bool Initial, bool Unknown) flow;
        try
        {
            flow = ReachingDefinitions.Analyze(unit.Procedure!, _data.BindingsOf(unit), _data.DataOf(unit)!, _data.Roots.Tokens, item, command.Exec);
        }
        catch (ReachingDefinitions.Unsupported e)
        {
            throw new Problem("reaching-flow:" + e.Message);
        }

        if (flow.Unknown)
        {
            throw new Problem("reaching-unknown:" + operand);
        }

        string? Value(string text)
        {
            var value = text.TrimEnd(' ');
            return value.Length == 0 ? null
                : ProgramNameR().IsMatch(value) && value.Length >= minimum && value.Length <= Math.Min(maximum, length) ? value
                : throw new Problem("reaching-value:" + operand);
        }

        var values = new List<string?>();
        var assignments = new JsonArray();
        foreach (var statement in flow.Assignments)
        {
            switch (statement)
            {
                case MoveStatement { Source: LiteralExpr { Kind: TokenKind.AlphanumericLiteral } literal } move
                    when literal.Text.Length >= 2 && literal.Text[0] is '\'' or '"':
                    values.Add(Value(Exec.ExecArguments.LiteralValue(literal.Text)));
                    assignments.Add(new JsonObject { ["syntax"] = _data.Statement(move, "MoveStatement") });
                    break;
                case MoveStatement { Source: DataRef { Subscripts.Count: 0, RefStart: null, Name.Qualifiers.Count: 0 } source } move:
                {
                    var (value, proof) = Constant(unit, source.Name.Name, minimum, maximum);
                    if (value.Length > length)
                    {
                        throw new Problem("reaching-value:" + operand);
                    }

                    values.Add(value);
                    assignments.Add(new JsonObject { ["syntax"] = _data.Statement(move, "MoveStatement"), ["sourceResolution"] = proof });
                    break;
                }

                case MoveStatement move when ReachingDefinitions.Blank(move.Source):
                    values.Add(null);
                    assignments.Add(new JsonObject { ["syntax"] = _data.Statement(move, "MoveStatement") });
                    break;
                case InitializeStatement initialize:
                    values.Add(null);
                    assignments.Add(new JsonObject { ["syntax"] = _data.Statement(initialize, "InitializeStatement") });
                    break;
                default:
                    throw new Problem("reaching-source:" + operand);
            }
        }

        if (flow.Initial)
        {
            values.Add(shape.Groups[4].Success ? Value(shape.Groups[4].Value) : null);
        }

        var reaching = values.Distinct().ToList();
        if (!reaching.Any(v => !string.IsNullOrEmpty(v)))
        {
            throw new Problem("reaching-empty:" + operand);
        }

        return (reaching, new JsonObject
        {
            ["kind"] = "structured-reaching-assignments",
            ["operand"] = operand,
            ["definitionSyntax"] = definition,
            ["ancestorSyntax"] = new JsonArray(ancestors.Select(a => (JsonNode)a).ToArray()),
            ["assignments"] = assignments,
            ["initialReaches"] = flow.Initial,
            ["reachingValues"] = new JsonArray(reaching.Select(v => (JsonNode?)v).ToArray()),
            ["usageProof"] = "single-native-program:structured-reaching-definitions",
        });
    }

    // A working- or local-storage PIC X item under plain groups with ascending levels from 01.
    private (DataItem Item, JsonObject Definition, List<JsonObject> Ancestors, Match Shape) ReachingItem(Command command, string operand)
    {
        var item = _data.DataOf(command.Unit)?.Symbols.Resolve(operand).Item ?? throw new Problem("reaching-name:" + operand);
        if (item.Section is not (DataSectionKind.WorkingStorage or DataSectionKind.LocalStorage) || item.External || item.Record.External
            || item.Occurs is not null || item.Ancestors().Any(a => a.Occurs is not null))
        {
            throw new Problem("reaching-section:" + operand);
        }

        var definition = _data.Entry(item) ?? throw new Problem("reaching-definition:" + operand);
        var shape = Regex.Match(
            string.Concat(Words(definition)),
            "^(0?[1-9]|[1-4][0-9]|77)" + Regex.Escape(operand) + """PIC(?:TURE)?(?:IS)?X\(0*([1-9][0-9]{0,3})\)(?:VALUE(?:IS)?(['"])([^'"]*)\3)?\.$""",
            RegexOptions.IgnoreCase);
        var ancestors = item.Ancestors().Reverse().Select(_data.Entry).ToList();
        var levels = item.Ancestors().Reverse().Select(a => a.Level).Append(item.Level).ToList();
        if (!shape.Success || ancestors.Any(a => a is null || !GroupR().IsMatch(string.Concat(Words(a))))
            || levels.Count > 1 && (levels[0] != 1 || levels.Zip(levels.Skip(1)).Any(p => p.First >= p.Second)))
        {
            throw new Problem("reaching-shape:" + operand);
        }

        return (item, definition, ancestors.Select(a => a!).ToList(), shape);
    }

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9-]*)\s*\(\s*([A-Za-z][A-Za-z0-9-]*)\s*\)$")]
    private static partial Regex TableR();

    [GeneratedRegex(@"^(0?[1-9]|[1-4][0-9])[A-Za-z][A-Za-z0-9-]*\.$")]
    private static partial Regex GroupR();

    [GeneratedRegex(@"^\d+[A-Za-z][A-Za-z0-9-]*PIC(?:TURE)?(?:IS)?[X9]\(0*[1-9][0-9]{0,3}\)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex MemberR();

    [GeneratedRegex("^[A-Za-z0-9@$#]+$")]
    private static partial Regex ProgramNameR();
}
