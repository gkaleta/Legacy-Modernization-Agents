using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Starforge.Cobol.Exec;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Native;

// EXEC CICS commands as CicsCommand trees: the verb token, then one CicsOption per option word holding the
// option name and, when present, a CicsArgument "(" value ")". Each data, label and resource operand is bound
// to its definition: data entries and procedure names in the program, CICS resource definitions and IDCAMS
// clusters from the estate. A command or operand this cannot bind becomes a diagnostic.
internal sealed partial class NativeCics
{
    private static readonly HashSet<string> FileVerbs = ["READ", "READNEXT", "READPREV", "WRITE", "REWRITE", "DELETE", "STARTBR", "ENDBR", "UNLOCK"];

    private static readonly HashSet<string> LocalVerbs =
    [
        "ASSIGN", "ABEND", "SYNCPOINT", "ASKTIME", "FORMATTIME", "HANDLE", "LINK", "ENQ", "DEQ", "DELAY", "GET", "PUT",
        "FETCH", "RUN", "XCTL", "SEND", "RECEIVE", "RETURN", "INQUIRE", "BIF", "WRITEQ", "SET",
    ];

    private static readonly HashSet<string> FileUnsupported =
    [
        "SYSID", "RBA", "RRN", "RBN", "XRBA", "SET", "TOKEN", "MASSINSERT", "GENERIC", "REQID", "DEBKEY", "DEBREC",
        "UNCOMMITTED", "CONSISTENT", "REPEATABLE",
    ];

    // The resource-name options whose operand a command only reads.
    public static readonly Func<CicsCommand, ExecOption, bool> NameOptions =
        (_, option) => option.Name is "FILE" or "DATASET" or "QUEUE" or "TRANSID" or "MAP" or "MAPSET" or "PROGRAM";

    public static readonly Dictionary<string, string[]> ValueSymbols = new()
    {
        ["DFHRESP"] = ["NORMAL", "INVREQ", "MAPFAIL", "NOTFINISHED", "NOTFND", "ENDFILE", "DUPREC", "DUPKEY"],
        ["DFHVALUE"] = ["NORMAL", "ABEND", "SECERROR", "UCTRAN", "TRANIDONLY", "NOUCTRAN"],
    };

    private readonly NativeData _data;
    private readonly CicsCatalog _catalog;
    private readonly VsamCatalog _vsam;
    private readonly BmsLayouts _layouts;
    private readonly Func<string, string?> _programSource;
    private readonly string? _commareaClosure;
    private readonly string? _programSha256;
    private readonly Dictionary<(ProgramUnit, string, int, int), string?> _constantValues = new();

    // programSource finds a program's source in the dependency search view. commareaClosure is the estate's
    // COMMAREA closure, which bounds slots the program loads from DFHCOMMAREA; programSha256 keys it.
    public NativeCics(
        NativeData data, CicsCatalog catalog, VsamCatalog vsam, BmsLayouts layouts, Func<string, string?> programSource,
        string? commareaClosure = null, string? programSha256 = null)
    {
        _data = data;
        _catalog = catalog;
        _vsam = vsam;
        _layouts = layouts;
        _programSource = programSource;
        _commareaClosure = commareaClosure;
        _programSha256 = programSha256;
    }

    private sealed record Option(string Name, int NameToken, ExecArgument? Argument, int Open, int Close, JsonObject Node);

    private sealed record Reference(JsonObject Node, string Kind, string Name, IReadOnlyList<string> Words);

    // A raw problem is reported under its own name rather than as "starforge-unsupported:*".
    private sealed class Problem(string message, bool raw = false) : Exception(message)
    {
        public bool Raw => raw;
    }

    // The CICS statement for one EXEC CICS block plus the DFHRESP/DFHVALUE values inside it. Links receives the
    // programs a LINK or RUN TRANSID binds, which the caller exports as CICS_LINK or CICS_RUN dependencies.
    public List<JsonObject> Statements(ExecStatementSyntax exec, CicsCommand command, ProgramUnit unit, List<string> diagnostics, List<(string Name, string Kind)> links)
    {
        var tokens = _data.Roots.Tokens;
        var blockToken = -1;
        for (var i = exec.Span.FirstToken; i < exec.Span.EndToken && blockToken < 0; i++)
        {
            if (tokens[i].Kind == TokenKind.ExecBlock)
            {
                blockToken = i;
            }
        }

        var verb = command.Verb.Split(' ')[0].ToUpperInvariant();
        try
        {
            if (blockToken < 0)
            {
                throw new Problem("block");
            }

            var block = tokens.Text(blockToken);
            var x = CobolishTokenizer.Tokenize(block, 0, block.Length, out var unterminated);
            if (unterminated >= 0 || x.Count < 4 || !x[0].IsWord("EXEC") || !x[1].IsWord("CICS") || !x[^1].IsWord("END-EXEC")
                || !x[2].IsWord(verb))
            {
                throw new Problem("tokens");
            }

            var built = Build(exec, block, tokens[blockToken].Start, x, verb, unit, links);
            _built[exec] = built[0];
            return built;
        }
        catch (Problem problem)
        {
            diagnostics.Add(problem.Raw ? problem.Message : $"starforge-unsupported:cics:{verb}:{problem.Message}");
            return [];
        }
    }

    private List<JsonObject> Build(ExecStatementSyntax exec, string block, int blockStart, List<XToken> x, string verb, ProgramUnit unit, List<(string Name, string Kind)> links)
    {
        var end = x.Count - 1;
        var options = new List<Option>();
        var references = new List<Reference>();
        var values = new List<JsonObject>();
        var handleCondition = verb == "HANDLE" && x.Count > 4 && x[3].IsWord("CONDITION");
        var handleAbend = verb == "HANDLE" && x.Count > 4 && x[3].IsWord("ABEND");
        for (var j = 3; j < end; j++)
        {
            if (x[j].Kind != XKind.Word)
            {
                throw new Problem("option-syntax");
            }

            var name = x[j].Upper;
            var children = new List<JsonNode> { NativeSyntax.Token(x[j].Text) };
            if (j + 1 < end && x[j + 1].Kind == XKind.LeftParen)
            {
                var close = CobolishTokenizer.MatchParen(x, j + 1);
                if (close < 0 || close >= end)
                {
                    throw new Problem("unbalanced");
                }

                var argument = ExecArguments.Classify(block, x, j + 2, close);
                var content = new List<JsonNode>();
                var inside = x.Skip(j + 2).Take(close - j - 2).ToList();
                var label = handleCondition && name != "CONDITION" || handleAbend && name == "LABEL";
                if (label)
                {
                    if (inside.Count != 1 || inside[0].Kind != XKind.Word)
                    {
                        throw new Problem("label:" + name);
                    }

                    var node = NativeSyntax.Node("ProcedureReference", [NativeSyntax.Token(inside[0].Text)]);
                    references.Add(new Reference(node, "CICS_LABEL", inside[0].Upper, [inside[0].Upper]));
                    content.Add(node);
                }
                else
                {
                    switch (argument.Kind)
                    {
                        case ExecArgumentKind.Literal or ExecArgumentKind.Numeric or ExecArgumentKind.Figurative:
                            content.AddRange(inside.Select(t => (JsonNode)NativeSyntax.Token(t.Text)));
                            break;
                        case ExecArgumentKind.DataRef or ExecArgumentKind.LengthOf:
                        {
                            var node = NativeSyntax.Node("DataReference", [NativeSyntax.Node("Identifier", inside.Select(t => (JsonNode)NativeSyntax.Token(t.Text)))]);
                            var words = inside.Select(t => t.Upper).ToList();
                            var context = (verb, name, words.Count == 1 ? words[0] : null) switch
                            {
                                ("INQUIRE" or "SET", "TERMINAL", "EIBTRMID") => true,
                                ("INQUIRE", "ASSOCIATION", "EIBTASKN") => true,
                                _ => false,
                            };
                            references.Add(new Reference(node, context ? "CICS_CONTEXT" : "CICS_DATA", string.Concat(words), words));
                            content.Add(node);
                            break;
                        }

                        case ExecArgumentKind.Function when inside.Count == 4 && inside[0].Kind == XKind.Word
                            && ValueSymbols.TryGetValue(inside[0].Upper, out var symbols) && symbols.Contains(inside[2].Upper):
                        {
                            var node = NativeSyntax.Node("CicsValue", inside.Select(t => (JsonNode)NativeSyntax.Token(t.Text)));
                            content.Add(node);
                            values.Add(Value(
                                (JsonObject)node.DeepClone(),
                                inside[0].Upper,
                                block[inside[0].Start..inside[3].End],
                                blockStart + inside[0].Start,
                                blockStart + inside[3].End));
                            break;
                        }

                        default:
                            throw new Problem("operand:" + name);
                    }
                }

                var argumentNode = NativeSyntax.Node("CicsArgument", [NativeSyntax.Token("("), .. content, NativeSyntax.Token(")")]);
                children.Add(argumentNode);
                options.Add(new Option(name, j, argument, j + 1, close, NativeSyntax.Node("CicsOption", children)));
                j = close;
            }
            else
            {
                options.Add(new Option(name, j, null, -1, -1, NativeSyntax.Node("CicsOption", children)));
            }
        }

        var root = NativeSyntax.Node("CicsCommand", [NativeSyntax.Token(x[2].Text), .. options.Select(o => (JsonNode)o.Node)]);
        var names = options.Select(o => o.Name).ToList();
        var form = Form(verb, names);
        var command = new Command(verb, names, options, unit, exec);

        var bindings = new List<JsonNode>();
        JsonObject? linkOperand = verb == "LINK" ? options.FirstOrDefault(o => o.Name == "PROGRAM")?.Node : null;
        var linkNodes = linkOperand is null ? new HashSet<JsonObject>(ReferenceEqualityComparer.Instance) : Descendants(linkOperand);
        var exported = new JsonArray();
        foreach (var reference in references)
        {
            if (linkNodes.Contains(reference.Node))
            {
                continue;
            }

            exported.Add(new JsonObject { ["kind"] = reference.Kind, ["name"] = reference.Name, ["grammarRule"] = reference.Node["node"]!.GetValue<string>() });
            bindings.Add(Local(reference, unit));
        }

        foreach (var resource in Resources(command, form))
        {
            bindings.Add(resource);
        }

        if (command.Link is { } linked)
        {
            links.Add((linked, "CICS_LINK"));
        }

        links.AddRange(command.Started.Select(started => (started, "CICS_RUN")));

        var last = x[end - 1];
        var statementStart = blockStart + x[2].Start;
        var syntax = _data.Roots.Root(root, statementStart, blockStart + last.End, "cics:" + verb);
        var (line, column) = _data.Roots.Position(statementStart);
        var statement = new JsonObject
        {
            ["kind"] = "EXEC_CICS",
            ["operation"] = verb,
            ["grammarRule"] = "CicsCommand",
            ["sourceText"] = block[x[2].Start..last.End],
            ["coordinateSpace"] = "expanded-parser-input",
            ["line"] = line,
            ["column"] = column,
            ["runtimeMapping"] = "unverified",
            ["syntax"] = syntax,
            ["resourceReferences"] = exported,
            ["resourceBindings"] = new JsonArray(bindings.ToArray()),
            ["bindingStatus"] = "complete",
            ["bindingBasis"] = Basis(verb, names),
        };
        return [statement, .. values];
    }

    // A DFHRESP/DFHVALUE condition value as its own statement; root is a CicsValue node over the four tokens.
    public JsonObject Value(JsonObject node, string operation, string sourceText, int start, int end)
    {
        var syntax = _data.Roots.Root(node, start, end, "cics:" + operation);
        var (line, column) = _data.Roots.Position(start);
        return new JsonObject
        {
            ["kind"] = "EXEC_CICS",
            ["operation"] = operation,
            ["grammarRule"] = "CicsValue",
            ["sourceText"] = sourceText,
            ["coordinateSpace"] = "expanded-parser-input",
            ["line"] = line,
            ["column"] = column,
            ["runtimeMapping"] = "unverified",
            ["syntax"] = syntax,
            ["bindingStatus"] = "complete",
        };
    }

    private sealed record Command(string Verb, List<string> Names, List<Option> Options, ProgramUnit Unit, ExecStatementSyntax Exec)
    {
        public string? Link { get; set; }

        // Programs a RUN TRANSID starts. CICS runs them as separate asynchronous tasks, so they export as CICS_RUN.
        public List<string> Started { get; } = [];

        public List<Option> All(string name) => Options.Where(o => o.Name == name).ToList();

        public Option Single(string name) =>
            All(name) is { Count: 1 } found && found[0].Argument is not null ? found[0] : throw new Problem("option:" + name);
    }

    private static string Form(string verb, List<string> names) => verb switch
    {
        "HANDLE" when names.FirstOrDefault() is "CONDITION" or "ABEND" => verb + " " + names[0],
        "SEND" or "RECEIVE" when names.Contains("MAP") => verb + " MAP",
        "WRITEQ" when names.Contains("TD") => "WRITEQ TD",
        "INQUIRE" when names.Contains("PROGRAM") => "INQUIRE PROGRAM",
        "SET" when names.Contains("TERMINAL") => "SET TERMINAL",
        _ => verb,
    };

    private static string Basis(string verb, List<string> names) => verb switch
    {
        "SEND" or "RECEIVE" or "RETURN" or "INQUIRE" or "BIF" => "parsed-cics-terminal-operation",
        "SET" when names.Contains("TERMINAL") => "parsed-cics-terminal-operation",
        _ when FileVerbs.Contains(verb) => "parsed-cics-file-operation",
        "RUN" => "parsed-cics-async-transaction",
        "WRITEQ" => "parsed-cics-queue-operation",
        "HANDLE" or "LINK" or "XCTL" => "parsed-cics-control-binding",
        "ENQ" or "DEQ" => "parsed-cics-lock-operation",
        _ => "parsed-current-task-operation",
    };

    private IEnumerable<JsonObject> Resources(Command command, string form)
    {
        var verb = command.Verb;
        if (FileVerbs.Contains(verb))
        {
            if (command.Names.Any(FileUnsupported.Contains))
            {
                throw new Problem("file-option");
            }

            var files = command.Options.Where(o => o.Name is "FILE" or "DATASET").ToList();
            if (files.Count != 1 || files[0].Argument is null)
            {
                throw new Problem("file-name");
            }

            var service = verb switch
            {
                "READ" => command.Names.Contains("UPDATE") ? "UPDATE" : "READ",
                "WRITE" => "ADD",
                "REWRITE" or "UNLOCK" => "UPDATE",
                "DELETE" => "DELETE",
                _ => "BROWSE",
            };
            yield return File(command, files[0], service);
            yield break;
        }

        if (!LocalVerbs.Contains(verb) || verb == "SET" && form != "SET TERMINAL")
        {
            throw new Problem("verb");
        }

        switch (form)
        {
            case "SEND MAP" or "RECEIVE MAP":
                yield return Map(command, verb);
                yield break;
            case "INQUIRE PROGRAM":
                foreach (var binding in ProgramTargets(command, "program-definition-inquiry"))
                {
                    yield return binding;
                }

                yield break;
            case "WRITEQ TD":
                yield return Queue(command);
                yield break;
            case "WRITEQ":
                throw new Problem("queue");
        }

        switch (verb)
        {
            case "LINK" when command.Names.Any(n => n is "SYSID" or "CHANNEL" or "INPUTEVENT" or "ACTIVITY"):
                throw new Problem("link-option");
            case "LINK":
                yield return Link(command);
                break;
            case "XCTL" when command.Names.Any(n => n is "CHANNEL" or "INPUTMSG" or "INPUTMSGLEN"):
                throw new Problem("xctl-option");
            case "XCTL":
                foreach (var binding in ProgramTargets(command, "transfer-control"))
                {
                    yield return binding;
                }

                break;
            case "RETURN" when command.Names.Contains("TRANSID"):
                yield return Transaction(command);
                break;
            case "RUN":
                foreach (var binding in Started(command))
                {
                    yield return binding;
                }

                break;
        }
    }

    private JsonObject Local(Reference reference, ProgramUnit unit)
    {
        if (reference.Kind == "CICS_CONTEXT")
        {
            return new JsonObject
            {
                ["kind"] = "CICS_CONTEXT",
                ["name"] = reference.Name,
                ["role"] = reference.Name == "EIBTRMID" ? "terminal-id" : "task-number",
                ["definitionScope"] = "cics-execution-interface",
            };
        }

        if (reference.Kind == "CICS_LABEL")
        {
            return new JsonObject
            {
                ["kind"] = "CICS_LABEL",
                ["name"] = reference.Name,
                ["definitionScope"] = "expanded-parser-input",
                ["definitionSyntax"] = _data.Procedure(unit, reference.Name) ?? throw new Problem("label:" + reference.Name),
            };
        }

        var symbols = _data.DataOf(unit)?.Symbols ?? throw new Problem("data");
        var words = reference.Words.ToList();
        if (words.Count > 2 && words[0] == "LENGTH" && words[1] == "OF")
        {
            words = words[2..];
        }

        var binding = new JsonObject { ["kind"] = "CICS_DATA", ["name"] = reference.Name, ["definitionScope"] = "expanded-parser-input" };
        DataItem Resolve(string name, IReadOnlyList<string>? qualifiers = null) =>
            symbols.Resolve(name, qualifiers).Item is { Level: not (66 or 88) } found ? found : throw new Problem("data:" + reference.Name);

        if (words.Count == 4 && words[1] == "(" && words[3] == ")")
        {
            var item = Resolve(words[0]);
            var path = _data.Path(item) ?? throw new Problem("data:" + reference.Name);
            binding["definitionSyntax"] = _data.Entry(item);
            binding["qualificationPath"] = path;
            if (!Regex.IsMatch(words[2], "^[0-9]+$"))
            {
                binding["subscriptSyntax"] = _data.Entry(Resolve(words[2])) ?? throw new Problem("data:" + reference.Name);
            }

            return binding;
        }

        if (words.Count == 0 || words.Count % 2 == 0 || words.Where((w, i) => i % 2 == 1 && w is not ("OF" or "IN")).Any())
        {
            throw new Problem("data-shape:" + reference.Name);
        }

        var names = words.Where((_, i) => i % 2 == 0).ToList();
        var resolved = Resolve(names[0], names.Count > 1 ? names.Skip(1).ToList() : null);
        binding["definitionSyntax"] = _data.Entry(resolved) ?? throw new Problem("data:" + reference.Name);
        if (names.Count > 1)
        {
            binding["qualificationPath"] = _data.Path(resolved) ?? throw new Problem("data:" + reference.Name);
        }

        return binding;
    }

    // The value a resource-name operand denotes with its proof: a literal, or a read-only constant VALUE.
    private (string Value, JsonObject Proof) Name(Command command, Option option, int minimum, int maximum)
    {
        var argument = option.Argument ?? throw new Problem("operand:" + option.Name);
        if (argument.Kind == ExecArgumentKind.Literal)
        {
            var value = argument.Value ?? string.Empty;
            if (argument.Text.Length != value.Length + 2 || !NameR().IsMatch(value) || value.Length < minimum || value.Length > maximum)
            {
                throw new Problem("literal:" + option.Name);
            }

            return (value, new JsonObject { ["kind"] = "literal" });
        }

        if (argument.Kind != ExecArgumentKind.DataRef || !NameR().IsMatch(argument.Text))
        {
            throw new Problem("operand:" + option.Name);
        }

        return Constant(command.Unit, argument.Text, minimum, maximum);
    }

    // A data name whose only value is its read-only VALUE literal, with the proof the gate re-derives.
    private (string Value, JsonObject Proof) Constant(ProgramUnit unit, string operand, int minimum, int maximum)
    {
        var (constant, item) = _data.Constant(unit, operand, callTargets: true, NameOptions, readStatements: true)
            ?? throw new Problem("not-constant:" + operand);
        var kind = item.Section switch
        {
            DataSectionKind.WorkingStorage => "immutable-working-storage-literal",
            DataSectionKind.LocalStorage => "immutable-local-storage-literal",
            _ => throw new Problem("constant-section:" + operand),
        };
        var definition = _data.Entry(item) ?? throw new Problem("constant:" + operand);
        var ancestors = item.Ancestors().Reverse().Select(_data.Entry).ToList();
        if (!ConstantShape(string.Concat(Words(definition)), ancestors.Select(a => a is null ? null : string.Concat(Words(a))), operand, constant, minimum, maximum))
        {
            throw new Problem("constant-shape:" + operand);
        }

        return (constant.TrimEnd(' '), new JsonObject
        {
            ["kind"] = kind,
            ["operand"] = operand,
            ["definitionSyntax"] = definition,
            ["ancestorSyntax"] = new JsonArray(ancestors.Select(a => (JsonNode)a!).ToArray()),
            ["usageProof"] = "single-native-program:read-only-constant-references",
        });
    }

    // CALL identifier: the same read-only constant proof the gate re-derives for CICS program operands.
    public (string Value, JsonObject Proof)? CallConstant(ProgramUnit unit, string operand)
    {
        try
        {
            return Constant(unit, operand, 1, 8);
        }
        catch (Problem)
        {
            return null;
        }
    }

    // The value Constant proves, checked on the entry tokens alone so no syntax root is built.
    private string? ConstantValue(ProgramUnit unit, string operand, int minimum, int maximum)
    {
        var key = (unit, operand.ToUpperInvariant(), minimum, maximum);
        if (_constantValues.TryGetValue(key, out var cached))
        {
            return cached;
        }

        _constantValues[key] = null;
        if (_data.Constant(unit, operand, callTargets: true, NameOptions, readStatements: true) is not { } found
            || found.Item is not { } item
            || item.Section is not (DataSectionKind.WorkingStorage or DataSectionKind.LocalStorage)
            || _data.EntryText(item) is not { } text
            || !ConstantShape(text, item.Ancestors().Reverse().Select(_data.EntryText), operand, found.Value, minimum, maximum))
        {
            return null;
        }

        return _constantValues[key] = found.Value.TrimEnd(' ');
    }

    private static bool ConstantShape(string text, IEnumerable<string?> ancestors, string operand, string constant, int minimum, int maximum)
    {
        var declaration = Regex.Match(
            text,
            "^(0?[1-9]|[1-4][0-9]|77)" + Regex.Escape(operand) + """PIC(?:TURE)?(?:IS)?X\(0*([1-9][0-9]?)\)VALUE(?:IS)?(['"])([A-Za-z0-9@$#_-]+)( *)\3\.$""",
            RegexOptions.IgnoreCase);
        return declaration.Success && declaration.Groups[4].Value == constant.TrimEnd(' ')
            && declaration.Groups[4].Length >= minimum && declaration.Groups[4].Length <= maximum
            && declaration.Groups[4].Length + declaration.Groups[5].Length <= int.Parse(declaration.Groups[2].Value)
            && ancestors.All(a => a is not null && Regex.IsMatch(a, @"^(0?[1-9]|[1-4][0-9])[A-Za-z][A-Za-z0-9-]*\.$"));
    }

    private static IEnumerable<string> Words(JsonNode? node)
    {
        if (node is JsonObject obj && obj["token"] is JsonValue token)
        {
            yield return token.GetValue<string>();
        }
        else if (node is JsonObject parent && parent["children"] is JsonArray children)
        {
            foreach (var child in children)
            {
                foreach (var word in Words(child))
                {
                    yield return word;
                }
            }
        }
    }

    private static HashSet<JsonObject> Descendants(JsonObject node)
    {
        var set = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance) { node };
        if (node["children"] is JsonArray children)
        {
            foreach (var child in children.OfType<JsonObject>())
            {
                set.UnionWith(Descendants(child));
            }
        }

        return set;
    }

    private JsonObject File(Command command, Option option, string service)
    {
        var (name, proof) = Name(command, option, 1, 8);
        var definition = _catalog.Find("FILE", name) ?? throw new Problem("file-definition:" + name);
        var dsname = definition["DSNAME"] ?? throw new Problem("file-dsname:" + name);
        var binding = new JsonObject
        {
            ["kind"] = "CICS_FILE",
            ["name"] = name,
            ["sourcePath"] = definition.Path,
            ["sourceSha256"] = definition.Sha256,
        };
        if (Int(definition["RECORDSIZE"]) is { } recordSize && Int(definition["KEYLENGTH"]) is { } keyLength)
        {
            if (keyLength > recordSize || recordSize <= 0)
            {
                throw new Problem("file-lengths:" + name);
            }

            binding["attributes"] = new JsonObject { ["dsname"] = dsname, ["recordsize"] = recordSize, ["keylength"] = keyLength };
        }
        else
        {
            var services = new JsonObject();
            foreach (var key in new[] { "ADD", "BROWSE", "DELETE", "READ", "UPDATE" })
            {
                services[key.ToLowerInvariant()] = definition[key] is "YES" or "NO" ? definition[key] : throw new Problem("file-services:" + name);
            }

            if ((string?)services[service.ToLowerInvariant()] != "YES")
            {
                throw new Problem("file-service:" + name);
            }

            var (attributes, datasets) = Ksds(dsname, name);
            attributes["services"] = services;
            attributes["lengthProvenance"] = "idcams-define";
            binding["attributes"] = attributes;
            binding["datasetDefinitions"] = datasets;
        }

        binding["targetResolution"] = proof;
        return binding;
    }

    // KSDS key and record lengths from the IDCAMS cluster, or through PATH -> ALTERNATEINDEX -> base cluster.
    private (JsonObject Attributes, JsonArray Datasets) Ksds(string dsname, string file)
    {
        var datasets = new JsonArray();
        var paths = _vsam.FindAll("PATH", dsname);
        var clusters = _vsam.FindAll("CLUSTER", dsname);
        int keyLength, keyOffset, recordSize;
        bool unique;
        string access;
        if (paths.Count == 0 && clusters.Count > 0)
        {
            (keyLength, keyOffset) = Keys(clusters, file);
            recordSize = RecordSize(clusters, file);
            unique = true;
            access = "base-cluster";
        }
        else if (paths.Count == 1 && clusters.Count == 0 && paths[0]["PATHENTRY"] is { } entry
                 && _vsam.FindAll("ALTERNATEINDEX", entry) is { Count: 1 } indexes && indexes[0]["RELATE"] is { } relate
                 && _vsam.FindAll("CLUSTER", relate) is { Count: > 0 } bases)
        {
            (keyLength, keyOffset) = Keys(indexes, file);
            recordSize = RecordSize(bases, file);
            unique = indexes[0].Has("UNIQUEKEY") && !indexes[0].Has("NONUNIQUEKEY");
            access = "alternate-index-path";
            datasets.Add(Dataset(paths[0]));
            datasets.Add(Dataset(indexes[0]));
            clusters = bases;
        }
        else
        {
            throw new Problem("file-dataset:" + file);
        }

        foreach (var cluster in clusters)
        {
            if (!cluster.Has("INDEXED"))
            {
                throw new Problem("file-organization:" + file);
            }

            datasets.Add(Dataset(cluster));
        }

        if (keyOffset + keyLength > recordSize)
        {
            throw new Problem("file-lengths:" + file);
        }

        return (new JsonObject
        {
            ["dsname"] = dsname,
            ["organization"] = "KSDS",
            ["accessPath"] = access,
            ["recordsize"] = recordSize,
            ["keylength"] = keyLength,
            ["keyOffset"] = keyOffset,
            ["uniqueKeys"] = unique,
        }, datasets);
    }

    private static (int Length, int Offset) Keys(List<VsamDefinition> definitions, string file)
    {
        var keys = definitions.Select(d => Numbers(d["KEYS"])).ToList();
        return keys.All(k => k is { Count: 2 } && k.SequenceEqual(keys[0]!)) ? (keys[0]![0], keys[0]![1]) : throw new Problem("file-keys:" + file);
    }

    private static int RecordSize(List<VsamDefinition> definitions, string file)
    {
        var sizes = definitions.Select(d => Numbers(d["RECORDSIZE"])).ToList();
        return sizes.All(s => s is { Count: 2 } && s.SequenceEqual(sizes[0]!)) ? sizes[0]![1] : throw new Problem("file-recordsize:" + file);
    }

    private static List<int>? Numbers(string? text) =>
        text is null ? null : text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(Int).ToList() is var list && list.All(n => n is not null)
            ? list.Select(n => n!.Value).ToList() : null;

    private static JsonObject Dataset(VsamDefinition definition)
    {
        if (!Regex.IsMatch(definition.Command, @"^DEFINE\s+(?:AIX|ALTERNATEINDEX|CLUSTER|PATH)\s*\(\s*NAME\s*\(" + Regex.Escape(definition.Name) + @"\)", RegexOptions.IgnoreCase))
        {
            throw new Problem("dataset-command:" + definition.Name);
        }

        return new JsonObject
        {
            ["type"] = definition.Type,
            ["name"] = definition.Name,
            ["sourcePath"] = definition.Path,
            ["sourceSha256"] = definition.Sha256,
            ["command"] = definition.Command,
        };
    }

    private JsonObject Queue(Command command)
    {
        if (command.Names.Contains("SYSID") || command.All("FROM").Count != 1 || command.All("LENGTH").Count > 1)
        {
            throw new Problem("queue-options");
        }

        var (name, proof) = Name(command, command.Single("QUEUE"), 1, 4);
        var definition = _catalog.Find("TDQUEUE", name) ?? throw new Problem("queue-definition:" + name);
        if (definition["TYPE"] != "EXTRA" || definition["TYPEFILE"] != "OUTPUT" || definition["RECORDFORMAT"] != "FIXED"
            || definition["DDNAME"] is not { } ddname || Int(definition["RECORDSIZE"]) is not { } size)
        {
            throw new Problem("queue-attributes:" + name);
        }

        return new JsonObject
        {
            ["kind"] = "CICS_TDQUEUE",
            ["name"] = name,
            ["sourcePath"] = definition.Path,
            ["sourceSha256"] = definition.Sha256,
            ["attributes"] = new JsonObject
            {
                ["type"] = "EXTRA",
                ["typefile"] = "OUTPUT",
                ["ddname"] = ddname,
                ["recordsize"] = size,
                ["recordformat"] = "FIXED",
            },
            ["targetResolution"] = proof,
        };
    }

    // A CICS PROGRAM definition bound to the program's source in the search view.
    private JsonObject Program(string name)
    {
        var definition = _catalog.Find("PROGRAM", name) ?? throw new Problem("program-definition:" + name);
        var language = definition["LANGUAGE"];
        var dynamic = definition["DYNAMIC"];
        if (language is not (null or "COBOL") || dynamic is not (null or "NO") || language is null && dynamic is null)
        {
            throw new Problem("program-attributes:" + name);
        }

        var attributes = new JsonObject { ["language"] = "COBOL", ["dynamic"] = "NO" };
        if (dynamic is null)
        {
            attributes["dynamicProvenance"] = "cics-default";
        }

        if (language is null)
        {
            attributes["languageProvenance"] = "cics-blank-language-environment";
        }

        var source = _programSource(name) ?? throw new Problem("program-source:" + name);
        return new JsonObject
        {
            ["kind"] = "CICS_PROGRAM",
            ["name"] = name,
            ["sourcePath"] = definition.Path,
            ["sourceSha256"] = definition.Sha256,
            ["attributes"] = attributes,
            ["programSourcePath"] = source,
            ["programSourceSha256"] = NativeSyntax.FileSha256(source),
        };
    }

    // LINK PROGRAM: a literal, or a constant whose every reference is a LINK/XCTL program operand.
    private JsonObject Link(Command command)
    {
        var option = command.Single("PROGRAM");
        var argument = option.Argument!;
        string name;
        JsonObject proof;
        if (argument.Kind == ExecArgumentKind.Literal)
        {
            name = (argument.Value ?? string.Empty).TrimEnd(' ');
            if (argument.Text.Length > 10 || !Regex.IsMatch(argument.Value ?? string.Empty, "^[A-Za-z0-9@$#_-]{1,8} *$"))
            {
                throw new Problem("literal:PROGRAM");
            }

            proof = new JsonObject { ["kind"] = "literal" };
        }
        else
        {
            if (argument.Kind != ExecArgumentKind.DataRef)
            {
                throw new Problem("operand:PROGRAM");
            }

            var constant = !NameR().IsMatch(argument.Text) ? null : _data.Constant(command.Unit, argument.Text, callTargets: false, readOnly: null, readStatements: false);
            var definition = constant is { } c ? _data.Entry(c.Item) : null;
            var match = Regex.Match(
                definition is null ? string.Empty : string.Concat(Words(definition)),
                "^01" + Regex.Escape(argument.Text) + """PIC(?:TURE)?(?:IS)?X\(0*8\)VALUE(?:IS)?(['"])([A-Z0-9@$#_-]{1,8})( *)\1\.$""",
                RegexOptions.IgnoreCase);
            if (constant is not var (value, item) || !match.Success || item.Section is not (DataSectionKind.WorkingStorage or DataSectionKind.LocalStorage)
                || match.Groups[2].Length + match.Groups[3].Length > 8 || match.Groups[2].Value != value.TrimEnd(' '))
            {
                // Otherwise the general proofs every gate operand rule accepts: a read-only constant or table entry.
                var bindings = ProgramTargets(command, "call").ToList();
                if (bindings.Count != 1)
                {
                    throw new Problem("link-targets:" + argument.Text);
                }

                command.Link = (string)bindings[0]["name"]!;
                return bindings[0];
            }

            name = match.Groups[2].Value;
            proof = new JsonObject
            {
                ["kind"] = item.Section == DataSectionKind.WorkingStorage ? "immutable-working-storage-literal" : "immutable-local-storage-literal",
                ["operand"] = argument.Text,
                ["definitionSyntax"] = definition,
                ["usageProof"] = "single-native-program:all-symbol-references-are-cics-program-operands",
            };
        }

        var binding = Program(name.ToUpperInvariant());
        binding["targetResolution"] = proof;
        command.Link = name.ToUpperInvariant();
        return binding;
    }

    // RETURN TRANSID: the transaction definition and the program it starts, bound to that program's source.
    private JsonObject Transaction(Command command)
    {
        var (name, proof) = Name(command, command.Single("TRANSID"), 4, 4);
        var binding = Transaction(name, proof);
        binding["controlEffect"] = "transaction-continuation";
        return binding;
    }

    private JsonObject Transaction(string name, JsonObject proof)
    {
        var definition = _catalog.Find("TRANSACTION", name) ?? throw new Problem("transaction-definition:" + name);
        if (definition["PROGRAM"] is not { } program || definition["DYNAMIC"] != "NO" || definition["ROUTABLE"] != "NO"
            || definition["STATUS"] != "ENABLED")
        {
            throw new Problem("transaction-attributes:" + name);
        }

        return new JsonObject
        {
            ["kind"] = "CICS_TRANSACTION",
            ["name"] = name,
            ["sourcePath"] = definition.Path,
            ["sourceSha256"] = definition.Sha256,
            ["attributes"] = new JsonObject
            {
                ["program"] = program.ToUpperInvariant(),
                ["dynamic"] = "NO",
                ["routable"] = "NO",
                ["status"] = "ENABLED",
            },
            ["programBinding"] = Program(program.ToUpperInvariant()),
            ["targetResolution"] = proof,
        };
    }

    private static int? Int(string? text) => int.TryParse(text, out var value) && value >= 0 ? value : null;

    [GeneratedRegex("^[A-Za-z0-9@$#_-]+$")]
    private static partial Regex NameR();
}
