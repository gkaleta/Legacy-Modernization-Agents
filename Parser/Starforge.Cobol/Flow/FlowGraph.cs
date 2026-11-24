using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Flow;

public enum FlowEdgeKind
{
    StartsWith,
    FollowedBy,
    JumpsTo,
}

// Type uses the vocabulary of the REKT flow AST (SECTION, PARAGRAPH, SENTENCE, PERFORM, ...) so existing readers apply.
public sealed class FlowNode
{
    private readonly List<FlowNode> _children = new();

    internal FlowNode(int id, string type, string name, string text, int startLine, int endLine, FlowNode? parent)
    {
        Id = id;
        Type = type;
        Name = name;
        Text = text;
        StartLine = startLine;
        EndLine = endLine;
        Parent = parent;
    }

    public int Id { get; }

    public string Type { get; }

    public string Name { get; }

    public string Text { get; }

    public int StartLine { get; }

    public int EndLine { get; }

    public FlowNode? Parent { get; }

    public IReadOnlyList<FlowNode> Children => _children;

    public Statement? Statement { get; internal init; }

    // PERFORM: the procedure performed (and THRU end); CALL: the program literal or the data name holding it.
    public string? Target { get; internal init; }

    public string? Thru { get; internal init; }

    public bool Conditional { get; internal init; }

    public bool Dynamic { get; internal init; }

    // Whitespace-collapsed EXEC SQL text; the loader derives SQL operation and tables from it.
    public string? SqlText { get; internal init; }

    // True when control never reaches the next sibling: GO TO, GOBACK, STOP RUN, EXIT PROGRAM, CICS RETURN/XCTL.
    public bool Terminates { get; internal set; }

    internal void Add(FlowNode child) => _children.Add(child);

    public IEnumerable<FlowNode> SelfAndDescendants()
    {
        var stack = new Stack<FlowNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node._children.Count - 1; i >= 0; i--)
            {
                stack.Push(node._children[i]);
            }
        }
    }
}

public sealed record FlowEdge(int From, int To, FlowEdgeKind Kind);

// A procedure name the CFG could not bind to a paragraph or section of this program.
public sealed record UnresolvedProcedure(string Name, string? Qualifier, int Line, string Statement);

public sealed record ProcedureFlow(FlowNode Root, IReadOnlyList<FlowEdge> Edges, IReadOnlyList<UnresolvedProcedure> Unresolved);
