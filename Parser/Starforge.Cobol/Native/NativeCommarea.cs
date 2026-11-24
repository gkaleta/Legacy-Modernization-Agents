using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Starforge.Cobol.Exec;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Native;

// COMMAREA records and their program-name slots. The inventory lists the records a program loads from
// DFHCOMMAREA or sends with XCTL/RETURN/LINK, every write to each short PIC X slot, the carriers that pass a
// record on, and every send; the estate closure joins the inventories. An XCTL PROGRAM operand that is such a
// slot binds to the values its writes (and, once the record arrives from DFHCOMMAREA, the closure) can hold.
internal sealed partial class NativeCics
{
    private const string SlotPattern = @"^(0?[2-9]|[1-4][0-9])([A-Z0-9-]+)PIC(?:TURE)?(?:IS)?(?:X\(0*([1-8])\)|(X{1,8}))"
        + """(?:VALUE(?:IS)?(?:(['"])([^'"]*)\5|(SPACES?|LOW-VALUES?)))?\.$""";

    private static readonly string[] SendVerbs = ["XCTL", "RETURN", "LINK"];

    private readonly Dictionary<ExecStatementSyntax, JsonObject> _built = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ProgramUnit, Commarea?> _commareas = new(ReferenceEqualityComparer.Instance);
    private JsonNode? _closure;

    private sealed record SlotWrite(Statement Statement, string Effect, string? Value = null, string? Source = null, bool Constant = false, string? Unsupported = null);

    private sealed class CommareaSlot(DataItem item, int length, string? initial)
    {
        public DataItem Item => item;

        public string Name => item.Name!.ToUpperInvariant();

        public int Length => length;

        public string? Initial => initial;

        public List<SlotWrite> Writes { get; } = [];
    }

    private sealed class CommareaRecord(DataItem item)
    {
        public DataItem Item => item;

        public string Name => item.Name!.ToUpperInvariant();

        public SortedSet<string> Issues { get; } = new(StringComparer.Ordinal);

        public List<string> Layout { get; } = [];

        public List<CommareaSlot> Slots { get; } = [];

        public bool Loaded { get; set; }
    }

    private sealed record CommareaSend(ExecStatementSyntax Exec, string Verb, string Operand, string Kind, DataItem? Item);

    // One program unit's view: its writes per statement, the entry moves, and the records analysed on demand.
    private sealed class Commarea(ProgramUnit unit, ProgramData data, ProcedureDivision procedure, NativeWrites writes)
    {
        public ProgramUnit Unit => unit;

        public ProgramData Data => data;

        public ProcedureDivision Procedure => procedure;

        public NativeWrites Writes => writes;

        public List<Statement> Statements { get; } = procedure.AllStatements().ToList();

        public Dictionary<DataItem, CommareaRecord> Records { get; } = new(ReferenceEqualityComparer.Instance);

        public List<CommareaSend> Sends { get; } = [];
    }

    // The commareaInventory of the outermost program; nested programs that send a COMMAREA are reported as such.
    public JsonObject Inventory()
    {
        var units = _data.Units();
        var records = new List<CommareaRecord>();
        var carriers = new JsonArray();
        var sends = new JsonArray();
        var main = units.Count > 0 ? CommareaOf(units[0]) : null;
        if (main is not null)
        {
            var wanted = new HashSet<DataItem>(ReferenceEqualityComparer.Instance);
            foreach (var send in main.Sends)
            {
                if (send.Kind == "record")
                {
                    wanted.Add(send.Item!);
                }
            }

            foreach (var statement in main.Statements)
            {
                if (statement is MoveStatement move && EntryRecord(main, move) is { } loaded)
                {
                    wanted.Add(loaded);
                }
            }

            var carrierRecords = new List<(CommareaSend Send, DataItem? Record, SortedSet<string> Issues)>();
            foreach (var send in main.Sends.Where(s => s.Kind == "carrier").DistinctBy(s => s.Item, ReferenceEqualityComparer.Instance))
            {
                var (record, issues) = Carrier(main, send);
                carrierRecords.Add((send, record, issues));
                if (record is not null)
                {
                    wanted.Add(record);
                }
            }

            records = wanted.OrderBy(r => r.Index).Select(r => RecordOf(main, r)).ToList();
            foreach (var (send, record, issues) in carrierRecords)
            {
                if (record is not null && RecordOf(main, record).Slots.Any(s => Supported(RecordOf(main, record), s) && !Values(s).Contains(null)))
                {
                    issues.Add("carrier-initial");
                }

                var carrier = new JsonObject { ["name"] = send.Item!.Name!.ToUpperInvariant() };
                if (record is not null)
                {
                    carrier["record"] = record.Name!.ToUpperInvariant();
                }

                carrier["issues"] = Strings(issues);
                carriers.Add(carrier);
            }
        }

        foreach (var unit in units)
        {
            if (CommareaOf(unit) is not { } commarea)
            {
                continue;
            }

            foreach (var send in commarea.Sends)
            {
                sends.Add(Send(send, ReferenceEquals(commarea, main) ? send.Kind : "nested-program"));
            }
        }

        return new JsonObject
        {
            ["schema"] = 1,
            ["records"] = new JsonArray(records.Select(r => (JsonNode)RecordJson(r)).ToArray()),
            ["carriers"] = carriers,
            ["sends"] = sends,
        };
    }

    private JsonObject Send(CommareaSend send, string kind)
    {
        var tokens = _data.Roots.Tokens;
        var (line, column) = _data.Roots.Position(tokens[send.Exec.Span.FirstToken].Start);
        var json = new JsonObject
        {
            ["command"] = send.Verb,
            ["operand"] = send.Operand,
            ["line"] = line,
            ["column"] = column,
            ["kind"] = kind,
        };
        if (kind is "record" or "carrier")
        {
            json[kind] = send.Item!.Name!.ToUpperInvariant();
        }

        if (send.Verb == "LINK")
        {
            var targets = _built.TryGetValue(send.Exec, out var built) && built["resourceBindings"] is JsonArray bindings
                ? bindings.OfType<JsonObject>().Where(b => (string?)b["kind"] == "CICS_PROGRAM").Select(b => (string)b["name"]!).ToList()
                : [];
            json["targets"] = Strings(targets);
            json["bindingStatus"] = targets.Count > 0 ? "complete" : "incomplete";
        }

        return json;
    }

    private static JsonObject RecordJson(CommareaRecord record)
    {
        var slots = new JsonObject();
        foreach (var slot in record.Slots)
        {
            slots[slot.Name] = new JsonObject
            {
                ["length"] = slot.Length,
                ["values"] = new JsonArray(Values(slot).Select(v => (JsonNode?)v).ToArray()),
                ["entry"] = slot.Writes.Any(w => w.Effect == "entry"),
                ["copies"] = Strings(slot.Writes.Where(w => w.Effect == "copy").Select(w => w.Source!).Distinct().Order(StringComparer.Ordinal)),
                ["unsupported"] = Strings(slot.Writes.Where(w => w.Unsupported is not null).Select(w => w.Unsupported!).Distinct().Order(StringComparer.Ordinal)),
                ["writeLines"] = new JsonArray(slot.Writes.Select(w => w.Statement.Range.Start.Line).Distinct().Order().Select(l => (JsonNode)l).ToArray()),
            };
        }

        return new JsonObject
        {
            ["name"] = record.Name,
            ["layout"] = Strings(record.Layout),
            ["loaded"] = record.Loaded,
            ["issues"] = Strings(record.Issues),
            ["slots"] = slots,
        };
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());

    // The initial value and every value write, null (SPACES) first, then in source order.
    private static List<string?> Values(CommareaSlot slot)
    {
        var values = new List<string?> { slot.Initial };
        values.AddRange(slot.Writes.Where(w => w.Effect is "null" or "value").Select(w => w.Value));
        var distinct = values.Distinct().ToList();
        return distinct.Contains(null) ? distinct.Where(v => v is null).Concat(distinct.Where(v => v is not null)).ToList() : distinct;
    }

    private static bool Supported(CommareaRecord record, CommareaSlot slot) =>
        record.Issues.Count == 0 && slot.Writes.All(w => w.Unsupported is null);

    private Commarea? CommareaOf(ProgramUnit unit)
    {
        if (_commareas.TryGetValue(unit, out var cached))
        {
            return cached;
        }

        Commarea? commarea = null;
        if (unit.Procedure is { } procedure && _data.DataOf(unit) is { } data)
        {
            commarea = new Commarea(unit, data, procedure, new NativeWrites(procedure, _data.BindingsOf(unit), data));
            foreach (var statement in commarea.Statements)
            {
                if (statement is ExecStatementSyntax { Parsed: CicsCommand command } exec && SendVerbs.Contains(command.Verb)
                    && command.Option("COMMAREA") is { Argument: { } argument })
                {
                    commarea.Sends.Add(Classify(commarea, exec, command.Verb, argument));
                }
            }
        }

        _commareas[unit] = commarea;
        return commarea;
    }

    private static CommareaSend Classify(Commarea commarea, ExecStatementSyntax exec, string verb, ExecArgument argument)
    {
        if (argument.Kind != ExecArgumentKind.DataRef || !NameR().IsMatch(argument.Text))
        {
            return new CommareaSend(exec, verb, argument.Text, "operand", null);
        }

        var operand = argument.Text.ToUpperInvariant();
        var item = commarea.Data.Symbols.Resolve(operand).Item;
        var kind = item switch
        {
            null => "unresolved",
            { Level: 1, Section: DataSectionKind.Linkage } when item.Name!.Equals("DFHCOMMAREA", StringComparison.OrdinalIgnoreCase) => "foreign",
            { Section: DataSectionKind.Linkage } => "linkage",
            { Level: not 1 } or { Section: not (DataSectionKind.WorkingStorage or DataSectionKind.LocalStorage) } => "operand",
            { IsGroup: false } => "carrier",
            _ => "record",
        };
        return new CommareaSend(exec, verb, operand, kind, item);
    }

    private static DataItem? ItemOf(Commarea commarea, DataRef reference) =>
        commarea.Writes.Candidates(reference) is [var only] ? only : null;

    private static bool Plain(Expr expr) => expr is DataRef { Subscripts.Count: 0, RefStart: null, RefLength: null };

    // MOVE DFHCOMMAREA(1:EIBCALEN) TO R, or (1:LENGTH OF R): the record the transaction's input loads.
    private static DataItem? EntryRecord(Commarea commarea, MoveStatement move)
    {
        if (move is not
            {
                Corresponding: false,
                Targets: [DataRef { Name.Qualifiers.Count: 0 } target],
                Source: DataRef { Subscripts.Count: 0, Name.Qualifiers.Count: 0, RefStart: LiteralExpr { Text: "1" }, RefLength: { } length } source,
            }
            || !Plain(target) || ItemOf(commarea, source) is not { Level: 1, Section: DataSectionKind.Linkage } linkage
            || !linkage.Name!.Equals("DFHCOMMAREA", StringComparison.OrdinalIgnoreCase)
            || ItemOf(commarea, target) is not { Level: 1 } record)
        {
            return null;
        }

        return length switch
        {
            DataRef { Name: { Qualifiers.Count: 0 } name } eib when Plain(eib) && name.Name.Equals("EIBCALEN", StringComparison.OrdinalIgnoreCase) => record,
            SpecialRef { Operator: SpecialOperator.LengthOf, Operand: DataRef { Name.Qualifiers.Count: 0 } of }
                when Plain(of) && ReferenceEquals(ItemOf(commarea, of), record) => record,
            _ => null,
        };
    }

    private CommareaRecord RecordOf(Commarea commarea, DataItem item)
    {
        if (commarea.Records.TryGetValue(item, out var cached))
        {
            return cached;
        }

        var record = new CommareaRecord(item);
        commarea.Records[item] = record;
        var tokens = _data.Roots.Tokens;
        if (item.Level != 1)
        {
            record.Issues.Add("record-level");
        }

        if (item.Section is not (DataSectionKind.WorkingStorage or DataSectionKind.LocalStorage))
        {
            record.Issues.Add("record-section");
        }

        if (item.External)
        {
            record.Issues.Add("external");
        }

        if (item.Global)
        {
            record.Issues.Add("global");
        }

        if (item.Redefines is not null || commarea.Data.Items.Any(x => x.Redefines is not null && !ReferenceEquals(x.Record, item) && NativeData.Overlap(x, item)))
        {
            record.Issues.Add("redefined");
        }

        if (commarea.Sends.Any(s => s.Verb == "LINK" && ReferenceEquals(s.Item, item)))
        {
            record.Issues.Add("linked-commarea");
        }

        foreach (var reference in commarea.Writes.References)
        {
            var at = reference.Span.FirstToken;
            if (at >= 2 && Word(tokens.Text(at - 1), "OF") && Word(tokens.Text(at - 2), "ADDRESS") && commarea.Writes.Overlaps(reference, item))
            {
                record.Issues.Add("address-of");
            }
        }

        foreach (var entry in item.SelfAndDescendants().Where(d => !d.IsCondition))
        {
            if (_data.EntryText(entry) is { } text)
            {
                record.Layout.Add(text);
            }
            else
            {
                record.Issues.Add("layout-unavailable");
            }
        }

        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<CommareaSlot>();
        var redefining = item.SelfAndDescendants().Where(x => x.Redefines is not null).ToList();
        foreach (var d in item.SelfAndDescendants().Skip(1))
        {
            if (d.Name is { } n)
            {
                names[n] = names.GetValueOrDefault(n) + 1;
            }

            if (d.IsGroup || !d.HasStorage || d.Name is null || d.Level is < 2 or > 49 || d.Occurs is not null
                || d.Ancestors().Any(a => a.Occurs is not null) || redefining.Any(x => NativeData.Overlap(x, d))
                || _data.EntryText(d) is not { } text
                || Regex.Match(text, SlotPattern, RegexOptions.IgnoreCase) is not { Success: true } shape
                || !shape.Groups[2].Value.Equals(d.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var length = shape.Groups[3].Success ? int.Parse(shape.Groups[3].Value) : shape.Groups[4].Length;
            var initial = shape.Groups[6].Success ? shape.Groups[6].Value.TrimEnd(' ') : "";
            candidates.Add(new CommareaSlot(d, length, initial.Length == 0 ? null : initial));
        }

        record.Slots.AddRange(candidates.Where(s => names[s.Item.Name!] == 1));
        foreach (var statement in commarea.Statements)
        {
            List<DataRef>? written = null;
            foreach (var slot in record.Slots)
            {
                if (statement is ExecStatementSyntax exec)
                {
                    if (commarea.Writes.ExecWrites(exec.Parsed, slot.Item, ExecReadOnly))
                    {
                        slot.Writes.Add(new SlotWrite(statement, "unsupported", Unsupported: exec.Parsed switch
                        {
                            CicsCommand c => "cics-" + c.Verb.Split(' ')[0],
                            SqlStatement => "sql-host-variable",
                            DliCommand => "dli",
                            _ => "exec",
                        }));
                    }

                    continue;
                }

                written ??= commarea.Writes.Written(statement).ToList();
                var overlapping = written.Where(r => commarea.Writes.Overlaps(r, slot.Item)).ToList();
                if (overlapping.Count > 0)
                {
                    slot.Writes.Add(Write(commarea, record, slot, statement, overlapping));
                }
            }
        }

        record.Loaded = commarea.Statements.Any(s => s is MoveStatement move && ReferenceEquals(EntryRecord(commarea, move), item));
        return record;
    }

    // XCTL and RETURN hand their COMMAREA to CICS without writing it, and LENGTH OF never writes its operand.
    private static readonly Func<CicsCommand, ExecOption, bool> ExecReadOnly = (command, option) =>
        NameOptions(command, option) || option.Name == "FROM" || option.Argument?.Kind == ExecArgumentKind.LengthOf
        || command.Verb is "XCTL" or "RETURN" && option.Name is "COMMAREA" or "LENGTH";

    private SlotWrite Write(Commarea commarea, CommareaRecord record, CommareaSlot slot, Statement statement, List<DataRef> overlapping)
    {
        SlotWrite Unsupported(string reason) => new(statement, "unsupported", Unsupported: reason);
        bool Holder(DataItem? target) => ReferenceEquals(target, slot.Item) || slot.Item.Ancestors().Any(a => ReferenceEquals(a, target));
        switch (statement)
        {
            case MoveStatement { Corresponding: true }:
                return Unsupported("move-corresponding");
            case MoveStatement move when overlapping is [var only] && Plain(only) && move.Targets.Contains(only):
            {
                var target = ItemOf(commarea, only);
                if (ReferenceEquals(target, slot.Item))
                {
                    return Source(commarea, record, slot, move);
                }

                if (!Holder(target))
                {
                    return Unsupported("overlapping-move");
                }

                return ReachingDefinitions.Blank(move.Source) ? new SlotWrite(statement, "null")
                    : ReferenceEquals(EntryRecord(commarea, move), record.Item) ? new SlotWrite(statement, "entry")
                    : Unsupported("group-move");
            }

            case MoveStatement:
                return Unsupported("overlapping-move");
            case InitializeStatement { WithFiller: false, AllOrCategory: null, ToValue: false, ToDefault: false, Replacing.Count: 0 } initialize
                when initialize.Targets.All(Plain) && overlapping.All(r => Holder(ItemOf(commarea, r))):
                return new SlotWrite(statement, "null");
            case InitializeStatement:
                return Unsupported("initialize-form");
            case SetStatement when overlapping.All(r => ItemOf(commarea, r) is { IsCondition: true }):
                return Unsupported("condition-name-write");
            default:
                return Unsupported("write:" + statement.GetType().Name);
        }
    }

    private SlotWrite Source(Commarea commarea, CommareaRecord record, CommareaSlot slot, MoveStatement move)
    {
        switch (move.Source)
        {
            case LiteralExpr { Kind: TokenKind.AlphanumericLiteral } literal when literal.Text.Length >= 2 && literal.Text[0] is '\'' or '"':
            {
                var value = ExecArguments.LiteralValue(literal.Text).TrimEnd(' ');
                return value.Contains('\'') || value.Contains('"') ? new SlotWrite(move, "unsupported", Unsupported: "literal-form")
                    : value.Length > slot.Length ? new SlotWrite(move, "unsupported", Unsupported: "literal-truncated")
                    : new SlotWrite(move, "value", value.Length == 0 ? null : value);
            }

            case var blank when ReachingDefinitions.Blank(blank):
                return new SlotWrite(move, "null");
            case DataRef { Name.Qualifiers.Count: 0 } source when Plain(source):
            {
                var name = source.Name.Name.ToUpperInvariant();
                if (record.Slots.FirstOrDefault(s => s.Name == name) is { } copied && ReferenceEquals(ItemOf(commarea, source), copied.Item))
                {
                    return new SlotWrite(move, "copy", Source: name);
                }

                return ConstantValue(commarea.Unit, name, 1, 8) is { } constant && constant.Length <= slot.Length
                    ? new SlotWrite(move, "value", constant, Constant: true)
                    : new SlotWrite(move, "unsupported", Unsupported: "sending-item-not-constant:" + name);
            }

            case DataRef source:
                return new SlotWrite(move, "unsupported", Unsupported: "sending-item-not-constant:" + source.Name.Name.ToUpperInvariant());
            default:
                return new SlotWrite(move, "unsupported", Unsupported: "sending-expression");
        }
    }

    // A carrier is an elementary 01 item filled only by MOVE R TO C from one record R, past R by reference
    // modification at LENGTH OF R + 1, or cleared; anything else, or a carrier LINK sends, is an issue.
    private (DataItem? Record, SortedSet<string> Issues) Carrier(Commarea commarea, CommareaSend send)
    {
        var carrier = send.Item!;
        var issues = new SortedSet<string>(StringComparer.Ordinal);
        var sources = new List<DataItem>();
        if (carrier.External || carrier.Global || carrier.Redefines is not null
            || commarea.Data.Items.Any(x => x.Redefines is not null && !ReferenceEquals(x.Record, carrier) && NativeData.Overlap(x, carrier)))
        {
            issues.Add("carrier-storage");
        }

        if (_data.EntryText(carrier) is not { } text || Regex.IsMatch(text, @"VALUE", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(text, @"VALUE(?:IS)?(?:SPACES?|LOW-VALUES?)\.$", RegexOptions.IgnoreCase))
        {
            issues.Add("carrier-value");
        }

        if (commarea.Sends.Any(s => s.Verb == "LINK" && ReferenceEquals(s.Item, carrier)))
        {
            issues.Add("linked-carrier");
        }

        foreach (var reference in commarea.Writes.References)
        {
            var at = reference.Span.FirstToken;
            var tokens = _data.Roots.Tokens;
            if (at >= 2 && Word(tokens.Text(at - 1), "OF") && Word(tokens.Text(at - 2), "ADDRESS") && commarea.Writes.Overlaps(reference, carrier))
            {
                issues.Add("address-of");
            }
        }

        var past = new List<(MoveStatement Move, DataRef Target)>();
        foreach (var statement in commarea.Statements)
        {
            if (statement is ExecStatementSyntax exec)
            {
                if (commarea.Writes.ExecWrites(exec.Parsed, carrier, ExecReadOnly))
                {
                    issues.Add("carrier-write:" + exec.Parsed.Operation);
                }

                continue;
            }

            var overlapping = commarea.Writes.Written(statement).Where(r => commarea.Writes.Overlaps(r, carrier)).ToList();
            if (overlapping.Count == 0)
            {
                continue;
            }

            switch (statement)
            {
                case MoveStatement { Corresponding: false } move when overlapping is [DataRef { Name.Qualifiers.Count: 0 } target]
                    && ReferenceEquals(ItemOf(commarea, target), carrier) && move.Targets.Contains(target):
                    if (Plain(target) && ReachingDefinitions.Blank(move.Source))
                    {
                        break;
                    }

                    if (Plain(target) && move.Source is DataRef { Name.Qualifiers.Count: 0 } source && Plain(source)
                        && ItemOf(commarea, source) is { Level: 1, IsGroup: true, Section: DataSectionKind.WorkingStorage or DataSectionKind.LocalStorage } record)
                    {
                        sources.Add(record);
                        break;
                    }

                    if (target is { Subscripts.Count: 0, RefStart: not null })
                    {
                        past.Add((move, target));
                        break;
                    }

                    issues.Add("carrier-write:MoveStatement");
                    break;
                case InitializeStatement { WithFiller: false, AllOrCategory: null, ToValue: false, ToDefault: false, Replacing.Count: 0 }
                    when overlapping.All(r => Plain(r) && ReferenceEquals(ItemOf(commarea, r), carrier)):
                    break;
                default:
                    issues.Add("carrier-write:" + statement.GetType().Name);
                    break;
            }
        }

        var distinct = sources.Distinct(ReferenceEqualityComparer.Instance).Cast<DataItem>().ToList();
        if (distinct.Count != 1)
        {
            issues.Add("carrier-records:" + distinct.Count);
            return (null, issues);
        }

        var filled = distinct[0];
        foreach (var (_, target) in past)
        {
            if (target.RefStart is not BinaryExpr { Operator: ArithmeticOperator.Add, Left: SpecialRef { Operator: SpecialOperator.LengthOf, Operand: DataRef of }, Right: LiteralExpr { Text: "1" } }
                || !Plain(of) || !ReferenceEquals(ItemOf(commarea, of), filled))
            {
                issues.Add("carrier-write:reference-modification");
            }
        }

        if (carrier.Length < filled.Length)
        {
            issues.Add("carrier-short");
        }

        return (filled, issues);
    }

    private static bool Word(string text, string word) => text.Equals(word, StringComparison.OrdinalIgnoreCase);

    // XCTL PROGRAM(slot): the values every write to the slot, and to the slots it copies, can store. Null when
    // the operand is no slot of a COMMAREA-shaped record.
    private (List<string> Names, JsonObject Proof)? SlotTargets(Command command, string operand, int minimum, int maximum)
    {
        if (CommareaOf(command.Unit) is not { } commarea || commarea.Data.Symbols.Resolve(operand).Item is not { } item
            || item.Record is not { Level: 1, Section: DataSectionKind.WorkingStorage or DataSectionKind.LocalStorage, IsGroup: true } recordItem)
        {
            return null;
        }

        var record = RecordOf(commarea, recordItem);
        if (record.Slots.FirstOrDefault(s => ReferenceEquals(s.Item, item)) is not { } slot)
        {
            return null;
        }

        if (record.Issues.Count > 0)
        {
            throw new Problem("slot-record:" + operand + ":" + record.Issues.Min);
        }

        var ancestors = item.Ancestors().Reverse().ToList();
        var ancestorText = ancestors.Select(_data.EntryText).ToList();
        if (ancestors.Count == 0 || ancestors[0].Level != 1 || ancestorText.Any(t => t is null || !GroupR().IsMatch(t))
            || ancestors.Zip(ancestors.Skip(1)).Any(p => p.First.Level >= p.Second.Level))
        {
            throw new Problem("slot-ancestors:" + operand);
        }

        var groups = ancestors.Select(a => a.Name!.ToUpperInvariant()).ToList();
        var closure = new List<CommareaSlot>();
        var pending = new Stack<CommareaSlot>([slot]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (closure.Contains(current))
            {
                continue;
            }

            closure.Add(current);
            foreach (var write in current.Writes)
            {
                if (write.Unsupported is not null)
                {
                    throw new Problem($"slot-write:{current.Name}:{write.Unsupported}");
                }

                if (write.Effect == "copy")
                {
                    pending.Push(record.Slots.First(s => s.Name == write.Source));
                }
            }
        }

        string? Checked(string? value) =>
            value is null || ProgramNameR().IsMatch(value) && value.Length >= minimum && value.Length <= maximum ? value
            : throw new Problem("slot-value:" + operand);
        var computed = new List<string?>();
        var entry = false;
        var slots = new JsonObject();
        var alternatives = string.Join("|", groups.Select(Regex.Escape));
        var recordName = Regex.Escape(groups[0]);
        foreach (var current in closure.OrderBy(s => s.Item.Index))
        {
            var own = Regex.Escape(current.Name) + "(?:(?:OF|IN)(?:" + alternatives + "))*";
            var holders = "(?:" + own + "|" + alternatives + ")";
            computed.Add(Checked(current.Initial));
            var writes = new JsonArray();
            foreach (var write in current.Writes)
            {
                var tokens = _data.StatementTokens(write.Statement);
                var body = string.Concat(tokens);
                bool Is(string pattern) => Regex.IsMatch(body, "^(?:" + pattern + ")$", RegexOptions.IgnoreCase);
                var json = new JsonObject { ["effect"] = write.Effect };
                var form = write.Effect switch
                {
                    "entry" => Is(@"MOVEDFHCOMMAREA\(1:(?:EIBCALEN|LENGTHOF" + recordName + @")\)TO" + recordName),
                    "null" when write.Statement is InitializeStatement => Initialized(tokens, groups, current.Name),
                    "null" => Is("MOVE(?:SPACES?|LOW-VALUES?)TO" + holders),
                    "value" when write.Constant => Is("MOVE" + Regex.Escape(((DataRef)((MoveStatement)write.Statement).Source).Name.Name) + "TO" + own),
                    "value" => Is("""MOVE(['"])([^'"]*)\1TO""" + own),
                    "copy" => Is("MOVE" + Regex.Escape(write.Source!) + "TO" + own),
                    _ => false,
                };
                if (!form)
                {
                    throw new Problem($"slot-form:{current.Name}:{write.Statement.Range.Start.Line}");
                }

                if (write.Value is not null)
                {
                    json["value"] = write.Value;
                }

                if (write.Source is not null)
                {
                    json["source"] = write.Source;
                }

                json["line"] = write.Statement.Range.Start.Line;
                json["syntax"] = _data.Statement(write.Statement, write.Statement is InitializeStatement ? "InitializeStatement" : "MoveStatement");
                if (write.Constant)
                {
                    json["sourceResolution"] = Constant(command.Unit, ((DataRef)((MoveStatement)write.Statement).Source).Name.Name, minimum, maximum).Proof;
                }

                if (write.Effect is "null" or "value")
                {
                    computed.Add(Checked(write.Value));
                }

                entry |= write.Effect == "entry";
                writes.Add(json);
            }

            var slotJson = new JsonObject { ["definitionSyntax"] = _data.Entry(current.Item) ?? throw new Problem("slot-definition:" + current.Name) };
            if (current.Initial is not null)
            {
                slotJson["initialValue"] = current.Initial;
            }

            slotJson["entry"] = current.Writes.Any(w => w.Effect == "entry");
            slotJson["writes"] = writes;
            slots[current.Name] = slotJson;
        }

        var proof = new JsonObject
        {
            ["kind"] = entry ? "closed-world-commarea-slot" : "flow-insensitive-slot-writes",
            ["operand"] = operand.ToUpperInvariant(),
            ["record"] = groups[0],
            ["definitionSyntax"] = _data.Entry(item) ?? throw new Problem("slot-definition:" + operand),
            ["ancestorSyntax"] = new JsonArray(ancestors.Select(a => (JsonNode)(_data.Entry(a) ?? throw new Problem("slot-ancestors:" + operand))).ToArray()),
            ["slots"] = slots,
        };
        List<string?> universe;
        if (!entry)
        {
            universe = Ordered(computed);
            proof["usageProof"] = "single-native-program:flow-insensitive-slot-writes";
        }
        else
        {
            if (_commareaClosure is null)
            {
                throw new Problem("cics-commarea-closure-pending:" + operand.ToUpperInvariant(), raw: true);
            }

            _closure ??= JsonNode.Parse(System.IO.File.ReadAllText(_commareaClosure));
            if (_closure?["programs"]?[_programSha256 ?? ""]?["slots"]?[operand.ToUpperInvariant()] is not JsonArray recorded)
            {
                throw new Problem("commarea-closure-unbounded:" + operand);
            }

            universe = recorded.Select(v => v is null ? null : Checked((string?)v)).ToList();
            if (computed.Any(v => !universe.Contains(v)) || universe.Distinct().Count() != universe.Count)
            {
                throw new Problem("commarea-closure-mismatch:" + operand);
            }

            proof["usageProof"] = "estate-closed-world:flow-insensitive-commarea-slot-closure";
            proof["closureSha256"] = NativeSyntax.FileSha256(_commareaClosure);
            proof["programSourceSha256"] = _programSha256;
        }

        proof["universe"] = new JsonArray(universe.Select(v => (JsonNode?)v).ToArray());
        var names = universe.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();
        if (names.Count == 0)
        {
            throw new Problem("slot-empty:" + operand);
        }

        return (names, proof);
    }

    private static List<string?> Ordered(IEnumerable<string?> values)
    {
        var distinct = values.Distinct().ToList();
        return distinct.Where(v => v is null).Concat(distinct.Where(v => v is not null).Order(StringComparer.Ordinal)).ToList();
    }

    // The gate's reading of an INITIALIZE: no phrases, and some operand naming the slot or one of its groups.
    private static bool Initialized(List<string> tokens, List<string> groups, string slot)
    {
        var upper = tokens.Select(t => t.ToUpperInvariant()).ToList();
        if (upper.Count == 0 || upper[0] != "INITIALIZE" || upper.Any(t => t is "REPLACING" or "VALUE" or "DEFAULT" or "FILLER" or "(" or ":"))
        {
            return false;
        }

        var index = 1;
        while (index < upper.Count)
        {
            if (upper[index] == ",")
            {
                index++;
                continue;
            }

            var path = new List<string> { upper[index++] };
            while (index + 1 < upper.Count && upper[index] is "OF" or "IN")
            {
                path.Add(upper[index + 1]);
                index += 2;
            }

            if ((groups.Contains(path[0]) || path[0] == slot) && path.Skip(1).All(groups.Contains))
            {
                return true;
            }
        }

        return false;
    }
}
