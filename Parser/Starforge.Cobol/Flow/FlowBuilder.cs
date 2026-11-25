using System.Text;
using Starforge.Cobol.Exec;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Flow;

// Builds the procedure tree and its control-flow edges: STARTS_WITH enters a block or branch, FOLLOWED_BY is
// sequential flow (including paragraph and section fall-through), JUMPS_TO is PERFORM, GO TO, ALTER and NEXT SENTENCE.
public static class FlowBuilder
{
    public static ProcedureFlow Build(ProcedureDivision procedure, TokenStream tokens) => new Builder(tokens).Run(procedure);

    private sealed class Builder
    {
        private readonly TokenStream _tokens;
        private readonly Dictionary<string, FlowNode> _sections = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Section, string Paragraph), FlowNode> _paragraphs = new();
        private readonly Dictionary<string, List<FlowNode>> _paragraphsByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(FlowNode From, ProcedureRef Target, string Statement)> _jumps = new();
        private readonly List<(ProcedureRef From, ProcedureRef To)> _alters = new();
        private readonly List<FlowNode> _nextSentences = new();
        private readonly List<FlowEdge> _edges = new();
        private readonly List<UnresolvedProcedure> _unresolved = new();
        private int _nextId;

        public Builder(TokenStream tokens) => _tokens = tokens;

        public ProcedureFlow Run(ProcedureDivision procedure)
        {
            var root = Node(null, "PROCEDURE_DIVISION_BODY", "PROCEDURE DIVISION", "PROCEDURE DIVISION", procedure.Range.StartLine, procedure.Range.EndLine);
            // The contract's procedure body always opens with a paragraph group, empty when the code starts with a section.
            if (!procedure.Sections.Any(s => s.Name is null))
            {
                Node(root, "PARAGRAPHS", "para-group:", string.Empty, procedure.Range.StartLine, procedure.Range.StartLine);
            }

            foreach (var section in procedure.AllSections())
            {
                AddSection(root, section);
            }

            ResolveJumps();
            MarkTerminators(root);
            AddEdges(root);
            return new ProcedureFlow(root, _edges, _unresolved);
        }

        private FlowNode Node(FlowNode? parent, string type, string name, string text, int startLine, int endLine,
            Statement? statement = null, string? target = null, string? thru = null, bool conditional = false, bool dynamic = false, string? sql = null)
        {
            var node = new FlowNode(_nextId++, type, name, text, startLine, endLine, parent)
            {
                Statement = statement,
                Target = target,
                Thru = thru,
                Conditional = conditional,
                Dynamic = dynamic,
                SqlText = sql,
            };
            parent?.Add(node);
            return node;
        }

        private void AddSection(FlowNode root, ProcedureSection section)
        {
            var key = section.Name?.ToUpperInvariant() ?? string.Empty;
            FlowNode container = root;
            if (section.Name is { } name)
            {
                container = Node(root, "SECTION", name, name + " SECTION.", section.StartLine, section.EndLine);
                Node(container, "SECTION_HEADER", name, name + " SECTION", section.StartLine, section.StartLine);
                _sections.TryAdd(name, container);
            }

            // "para-group:" is the contract's name for the container of a section's sentences and paragraphs; graph queries exclude it as a caller.
            var start = section.Leading.Count > 0 ? StartLine(section.Leading[0].Span)
                : section.Paragraphs.Count > 0 ? section.Paragraphs[0].StartLine : section.StartLine;
            var end = section.Paragraphs.Count > 0 ? section.Paragraphs[^1].EndLine
                : section.Leading.Count > 0 ? EndLine(section.Leading[^1].Span) : section.EndLine;
            var group = Node(container, "PARAGRAPHS", "para-group:", string.Empty, start, end);
            foreach (var sentence in section.Leading)
            {
                AddSentence(group, sentence);
            }

            foreach (var paragraph in section.Paragraphs)
            {
                var node = Node(group, "PARAGRAPH", paragraph.Name, paragraph.Name + ".", paragraph.StartLine, paragraph.EndLine);
                Node(node, "PARAGRAPH_NAME", paragraph.Name, paragraph.Name, paragraph.StartLine, paragraph.StartLine);
                _paragraphs.TryAdd((key, paragraph.Name.ToUpperInvariant()), node);
                if (!_paragraphsByName.TryGetValue(paragraph.Name, out var list))
                {
                    _paragraphsByName[paragraph.Name] = list = new List<FlowNode>();
                }

                list.Add(node);
                foreach (var sentence in paragraph.Sentences)
                {
                    AddSentence(node, sentence);
                }
            }
        }

        private void AddSentence(FlowNode parent, Sentence sentence)
        {
            var node = Node(parent, "SENTENCE", string.Empty, Text(sentence.Span), StartLine(sentence.Span), EndLine(sentence.Span));
            foreach (var statement in sentence.Statements)
            {
                AddStatement(node, statement);
            }
        }

        private void AddStatements(FlowNode parent, IEnumerable<Statement> statements)
        {
            foreach (var statement in statements)
            {
                AddStatement(parent, statement);
            }
        }

        private void AddStatement(FlowNode parent, Statement s)
        {
            var text = Text(s.Span);
            var start = StartLine(s.Span);
            var end = EndLine(s.Span);
            switch (s)
            {
                case IfStatement i:
                {
                    var node = Node(parent, "IF_BRANCH", "IF", text, start, end, s);
                    Branch(node, "IF_YES", "Yes", i.Then);
                    if (i.Else is { } otherwise)
                    {
                        Branch(node, "IF_NO", "No", otherwise);
                    }

                    break;
                }

                case EvaluateStatement e:
                {
                    var node = Node(parent, "EVALUATE", "EVALUATE", text, start, end, s);
                    foreach (var when in e.Whens)
                    {
                        Branch(node, "WHEN", "WHEN", when.Statements);
                    }

                    if (e.Other is { } other)
                    {
                        Branch(node, "WHEN_OTHER", "WHEN OTHER", other);
                    }

                    break;
                }

                case SearchStatement search:
                {
                    var node = Node(parent, "SEARCH", "SEARCH", text, start, end, s);
                    if (search.AtEnd is { } atEnd)
                    {
                        Branch(node, "HANDLER", "AT END", atEnd);
                    }

                    foreach (var when in search.Whens)
                    {
                        Branch(node, "WHEN", "WHEN", when.Statements);
                    }

                    break;
                }

                case PerformStatement p:
                {
                    var node = Node(parent, "PERFORM", p.Target?.Name ?? string.Empty, text, start, end, s, p.Target?.Name, p.Thru?.Name,
                        p.Kind is PerformKind.Until or PerformKind.Varying);
                    if (p.Target is { } target)
                    {
                        _jumps.Add((node, target, "PERFORM"));
                    }

                    if (p.Thru is { } thru)
                    {
                        _jumps.Add((node, thru, "PERFORM THRU"));
                    }

                    if (p.Inline is { } body)
                    {
                        AddStatements(node, body);
                    }

                    break;
                }

                case GoToStatement go:
                {
                    var node = Node(parent, "GOTO", go.Targets.Count > 0 ? go.Targets[0].Name : string.Empty, text, start, end, s);
                    node.Terminates = go.DependingOn is null;
                    foreach (var target in go.Targets)
                    {
                        _jumps.Add((node, target, go.DependingOn is null ? "GO TO" : "GO TO DEPENDING"));
                    }

                    break;
                }

                case AlterStatement alter:
                    Node(parent, "ALTER", "ALTER", text, start, end, s);
                    _alters.AddRange(alter.Alterations);
                    break;

                case CallStatement call:
                {
                    var target = call.Program ?? ProcedureText.Of(call.Target);
                    var node = Node(parent, "CALL", target, text, start, end, s, target, dynamic: !call.IsStatic);
                    Handlers(node, call.Handlers);
                    break;
                }

                case SortStatement sort:
                {
                    Node(parent, "GENERIC_STATEMENT", sort.Verb, text, start, end, s);
                    var node = parent.Children[^1];
                    foreach (var procedure in new[] { sort.InputProcedure, sort.OutputProcedure })
                    {
                        if (procedure is { } range)
                        {
                            _jumps.Add((node, range.From, sort.Verb));
                            if (range.Thru is { } thru)
                            {
                                _jumps.Add((node, thru, sort.Verb));
                            }
                        }
                    }

                    break;
                }

                case ExecStatementSyntax exec:
                {
                    var kind = exec.Kind.ToString().ToUpperInvariant();
                    var sql = exec.Kind == ExecKind.Sql ? SqlText(exec.Parsed) : null;
                    var node = Node(parent, "DIALECT", "EXEC " + kind, text, start, end, s, sql: sql);
                    node.Terminates = exec.Parsed is CicsCommand { Verb: "RETURN" or "XCTL" };
                    Node(node, "DIALECT_CONTAINER", exec.Parsed.Operation, text, start, end);
                    break;
                }

                case IdmsStatement dml:
                    AddIdms(parent, dml, start);
                    break;

                case NextSentenceStatement:
                    _nextSentences.Add(Node(parent, "NEXT_SENTENCE", "NEXT SENTENCE", text, start, end, s));
                    break;

                default:
                {
                    var (type, name, terminates) = Classify(s);
                    var node = Node(parent, type, name, text, start, end, s);
                    node.Terminates = terminates;
                    Handlers(node, HandlersOf(s));
                    break;
                }
            }
        }

        // REKT's IDMS vocabulary: BIND RUN-UNIT and FINISH open and close the transaction; other DML is a DIALECT node
        // around one ATOMIC (or TRANSFER) child. The ON clause follows as a decision whose action holds its statements.
        private void AddIdms(FlowNode parent, IdmsStatement dml, int start)
        {
            var command = Text(dml.Command);
            var end = EndLine(dml.Command);
            FlowNode node;
            switch (dml.Kind)
            {
                case IdmsKind.BindRunUnit:
                    node = Node(parent, "BIND_RUN_UNIT", dml.Form, command, start, end, dml);
                    break;
                case IdmsKind.Finish:
                    node = Node(parent, "FINISH", dml.Form, command, start, end, dml);
                    break;
                default:
                    node = Node(parent, "DIALECT", "IDMS " + dml.Form, command, start, end, dml);
                    if (dml.Transfer is { } transfer)
                    {
                        Node(node, "TRANSFER", transfer.Target, command, start, end, target: transfer.Target, dynamic: !transfer.IsLiteral);
                    }
                    else
                    {
                        Node(node, "ATOMIC", dml.Form, command, start, end);
                    }

                    break;
            }

            if (dml.On is { } on)
            {
                var clause = Node(parent, "ON_CLAUSE", "ON " + on.Condition, Text(on.Span), StartLine(on.Span), EndLine(on.Span));
                Branch(clause, "ON_CLAUSE_ACTION", on.Condition, on.Statements);
                return;
            }

            node.Terminates = dml.Kind == IdmsKind.DcReturn || dml.Transfer is { Mode: "NORETURN" };
        }

        private void Branch(FlowNode parent, string type, string name, IReadOnlyList<Statement> statements)
        {
            var node = statements.Count == 0
                ? Node(parent, type, name, string.Empty, parent.StartLine, parent.EndLine)
                : Node(parent, type, name, Text(new NodeSpan(statements[0].Span.FirstToken, statements[^1].Span.EndToken, statements[0].Span.Start, statements[^1].Span.End)),
                    StartLine(statements[0].Span), EndLine(statements[^1].Span));
            AddStatements(node, statements);
        }

        private void Handlers(FlowNode parent, IReadOnlyList<ConditionalPhrase> handlers)
        {
            foreach (var h in handlers)
            {
                Branch(parent, "HANDLER", (h.Not ? "NOT " : string.Empty) + HandlerName(h.Kind), h.Statements);
            }
        }

        private static string HandlerName(HandlerKind kind) => kind switch
        {
            HandlerKind.AtEnd => "AT END",
            HandlerKind.InvalidKey => "INVALID KEY",
            HandlerKind.SizeError => "ON SIZE ERROR",
            HandlerKind.Exception => "ON EXCEPTION",
            HandlerKind.Overflow => "ON OVERFLOW",
            _ => "AT END-OF-PAGE",
        };

        private static IReadOnlyList<ConditionalPhrase> HandlersOf(Statement s) => s switch
        {
            ComputeStatement c => c.Handlers,
            ArithmeticStatement c => c.Handlers,
            StringStatement c => c.Handlers,
            UnstringStatement c => c.Handlers,
            FileStatement c => c.Handlers,
            AcceptStatement c => c.Handlers,
            _ => Array.Empty<ConditionalPhrase>(),
        };

        // GOBACK is an EXIT node as in the REKT vocabulary; only EXIT PROGRAM, GOBACK and STOP RUN end the run unit or program.
        private static (string Type, string Name, bool Terminates) Classify(Statement s) => s switch
        {
            MoveStatement => ("MOVE", "MOVE", false),
            ComputeStatement => ("COMPUTE", "COMPUTE", false),
            ArithmeticStatement a => (a.Verb.ToString().ToUpperInvariant(), a.Verb.ToString().ToUpperInvariant(), false),
            DisplayStatement => ("DISPLAY", "DISPLAY", false),
            ExitStatement e => ("EXIT", e.Kind.Length == 0 ? "EXIT" : "EXIT " + e.Kind, e.Kind == "PROGRAM"),
            GobackStatement => ("EXIT", "GOBACK", true),
            StopStatement stop => ("STOP", stop.Run ? "STOP RUN" : "STOP", stop.Run),
            FileStatement f => ("GENERIC_STATEMENT", f.Verb, false),
            ErrorStatement => ("GENERIC_STATEMENT", "ERROR", false),
            _ => ("GENERIC_STATEMENT", s.GetType().Name[..^"Statement".Length].ToUpperInvariant(), false),
        };

        private void ResolveJumps()
        {
            foreach (var (from, target, statement) in _jumps)
            {
                Jump(from, target, statement);
            }

            // ALTER changes where the GO TO that is the first statement of the altered paragraph goes.
            foreach (var (from, to) in _alters)
            {
                var paragraph = Resolve(from);
                var go = paragraph?.Children.FirstOrDefault(c => c.Type == "SENTENCE")?.Children.FirstOrDefault();
                if (go is not { Type: "GOTO" })
                {
                    _unresolved.Add(new UnresolvedProcedure(from.Name, from.Qualifier, from.Line, "ALTER"));
                    continue;
                }

                Jump(go, to, "ALTER");
            }

            foreach (var node in _nextSentences)
            {
                var sentence = node.Parent;
                while (sentence is not null && sentence.Type != "SENTENCE")
                {
                    sentence = sentence.Parent;
                }

                if (sentence?.Parent is { } container)
                {
                    var siblings = container.Children;
                    var at = IndexOf(siblings, sentence);
                    if (at + 1 < siblings.Count && siblings[at + 1].Type == "SENTENCE")
                    {
                        _edges.Add(new FlowEdge(node.Id, siblings[at + 1].Id, FlowEdgeKind.JumpsTo));
                    }
                }
            }
        }

        private void Jump(FlowNode from, ProcedureRef target, string statement)
        {
            if (Resolve(target) is { } node)
            {
                _edges.Add(new FlowEdge(from.Id, node.Id, FlowEdgeKind.JumpsTo));
            }
            else
            {
                _unresolved.Add(new UnresolvedProcedure(target.Name, target.Qualifier, target.Line, statement));
            }
        }

        // An unqualified paragraph name is looked up in the referencing section first, then as a section, then program-wide.
        private FlowNode? Resolve(ProcedureRef reference)
        {
            var name = reference.Name.ToUpperInvariant();
            if (reference.Qualifier is { } qualifier)
            {
                return _paragraphs.GetValueOrDefault((qualifier.ToUpperInvariant(), name));
            }

            if (_paragraphs.TryGetValue((reference.FromSection?.ToUpperInvariant() ?? string.Empty, name), out var local))
            {
                return local;
            }

            if (_sections.TryGetValue(name, out var section))
            {
                return section;
            }

            return _paragraphsByName.TryGetValue(name, out var list) ? list[0] : null;
        }

        private static bool IsBranching(FlowNode node)
            => node.Type is "IF_BRANCH" or "EVALUATE" or "SEARCH" or "DIALECT" or "ON_CLAUSE"
                || (node.Statement is not null && node.Type != "PERFORM");

        private static void MarkTerminators(FlowNode root)
        {
            foreach (var node in root.SelfAndDescendants().Reverse())
            {
                if (node.Children.Count == 0 || node.Terminates)
                {
                    continue;
                }

                node.Terminates = node.Type switch
                {
                    "IF_BRANCH" => node.Children.Count == 2 && node.Children.All(c => c.Terminates),
                    "EVALUATE" => node.Children.Any(c => c.Type == "WHEN_OTHER") && node.Children.All(c => c.Terminates),
                    "PERFORM" or "SEARCH" or "DIALECT" => false,
                    _ when IsBranching(node) => false,
                    _ => node.Children[^1].Terminates,
                };
            }
        }

        private void AddEdges(FlowNode root)
        {
            foreach (var node in root.SelfAndDescendants())
            {
                var children = node.Children;
                if (children.Count == 0)
                {
                    continue;
                }

                if (IsBranching(node))
                {
                    foreach (var child in children)
                    {
                        _edges.Add(new FlowEdge(node.Id, child.Id, FlowEdgeKind.StartsWith));
                    }

                    continue;
                }

                _edges.Add(new FlowEdge(node.Id, children[0].Id, FlowEdgeKind.StartsWith));
                for (var i = 0; i + 1 < children.Count; i++)
                {
                    if (!children[i].Terminates)
                    {
                        _edges.Add(new FlowEdge(children[i].Id, children[i + 1].Id, FlowEdgeKind.FollowedBy));
                    }
                }
            }
        }

        private static int IndexOf(IReadOnlyList<FlowNode> nodes, FlowNode node)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                if (ReferenceEquals(nodes[i], node))
                {
                    return i;
                }
            }

            return -1;
        }

        private int StartLine(NodeSpan span) => span.IsEmpty ? 0 : _tokens.ProgramLine(span.FirstToken);

        private int EndLine(NodeSpan span) => span.IsEmpty ? 0 : _tokens.ProgramLine(span.EndToken - 1, end: true);

        private string Text(NodeSpan span) => SourceText.Of(_tokens.Source.Content, span.Start, span.End);

        // SQL comments are dropped so table names in them do not reach the loader's table scan.
        private string SqlText(Exec.ExecStatement exec)
        {
            var content = _tokens.Source.Content;
            var sb = new StringBuilder();
            var pos = exec.ExpandedStart;
            foreach (var comment in exec.Comments.OrderBy(c => c.Start))
            {
                var from = exec.ExpandedStart + comment.Start;
                if (from > pos)
                {
                    sb.Append(content, pos, Math.Min(from, exec.ExpandedEnd) - pos).Append(' ');
                }

                pos = Math.Max(pos, exec.ExpandedStart + comment.End);
            }

            if (exec.ExpandedEnd > pos)
            {
                sb.Append(content, pos, exec.ExpandedEnd - pos);
            }

            return SourceText.Collapse(sb.ToString());
        }
    }
}

public static class SourceText
{
    // Source text of an expanded-stream range with trailing blanks removed from each line.
    public static string Of(string content, int start, int end)
    {
        start = Math.Clamp(start, 0, content.Length);
        end = Math.Clamp(end, start, content.Length);
        var lines = content[start..end].Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        return string.Join('\n', lines.Select(l => l.TrimEnd())).Trim();
    }

    public static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
