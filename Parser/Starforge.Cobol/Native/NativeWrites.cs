using Starforge.Cobol.Exec;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Native;

// The storage one procedure statement may write: every data reference NativeReads does not prove read-only
// (nested statements excluded, they are statements of their own), and every EXEC operand outside the positions
// the caller names as read-only. A reference that resolves ambiguously counts against every candidate.
internal sealed class NativeWrites
{
    private readonly ProcedureBindings _bindings;
    private readonly ProgramData _data;
    private readonly DataRef[] _references;

    public NativeWrites(ProcedureDivision procedure, ProcedureBindings bindings, ProgramData data)
    {
        _bindings = bindings;
        _data = data;
        _references = procedure.DataReferences.OrderBy(r => r.Span.FirstToken).ToArray();
    }

    public IReadOnlyList<DataRef> References => _references;

    public bool Writes(Statement statement, DataItem item, Func<CicsCommand, ExecOption, bool> readOnly) => statement is ExecStatementSyntax exec
        ? ExecWrites(exec.Parsed, item, readOnly)
        : Written(statement).Any(reference => Overlaps(reference, item));

    public IEnumerable<DataRef> Written(Statement statement)
    {
        var nested = statement.Children().Select(c => (c.Span.FirstToken, c.Span.EndToken)).ToList();
        var reads = NativeReads.Of([statement]);
        foreach (var reference in In(statement.Span))
        {
            var at = reference.Span.FirstToken;
            if (!reads.Contains(reference) && !nested.Any(n => n.FirstToken <= at && at < n.EndToken))
            {
                yield return reference;
            }
        }
    }

    public IEnumerable<DataRef> In(NodeSpan span)
    {
        var low = 0;
        var high = _references.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_references[mid].Span.FirstToken < span.FirstToken)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        for (var i = low; i < _references.Length && _references[i].Span.FirstToken < span.EndToken; i++)
        {
            yield return _references[i];
        }
    }

    public bool ExecWrites(Exec.ExecStatement exec, DataItem item, Func<CicsCommand, ExecOption, bool> readOnly)
    {
        IEnumerable<ExecOption> options = exec switch
        {
            CicsCommand c => c.Options.Where(o => !readOnly(c, o)),
            DliCommand d => d.Options.Concat(d.Segments.SelectMany(s => s.Options)),
            _ => [],
        };
        if (options.Any(o => o.Argument?.DataName is { } name && Overlaps(_data.Symbols.Resolve(name).Candidates, item)))
        {
            return true;
        }

        return exec is SqlStatement sql && sql.HostVariables.Any(h =>
            Overlaps(_data.Symbols.Resolve(h.Field, h.Qualifier is null ? null : [h.Qualifier]).Candidates, item)
            || h.Indicator is { } indicator && Overlaps(_data.Symbols.Resolve(indicator).Candidates, item));
    }

    public IReadOnlyList<DataItem> Candidates(DataRef reference) =>
        _bindings.ItemOf(reference) is { } item ? [item] : _data.Symbols.Resolve(reference.Name).Candidates;

    public bool Overlaps(DataRef reference, DataItem item) => Overlaps(Candidates(reference), item);

    public static bool Overlaps(IReadOnlyList<DataItem> candidates, DataItem item) => candidates.Any(c => c.IsRenames
        ? ReferenceEquals(Deps.ConstantResolver.Extent(c).Root, Deps.ConstantResolver.Extent(item).Root)
        : NativeData.Overlap(c.IsCondition && c.Parent is { } parent ? parent : c, item));
}
