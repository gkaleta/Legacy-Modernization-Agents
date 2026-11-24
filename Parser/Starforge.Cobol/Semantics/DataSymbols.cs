using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Semantics;

public enum LookupStatus
{
    Found,
    NotFound,
    Ambiguous,
}

public sealed record SymbolLookup(LookupStatus Status, IReadOnlyList<DataItem> Candidates)
{
    public DataItem? Item => Status == LookupStatus.Found ? Candidates[0] : null;
}

// Name resolution for data names: qualifiers (OF/IN) must name ancestors in inner-to-outer order, gaps allowed;
// an FD file name may qualify its records. Unresolved names fall back to GLOBAL items of containing programs.
public sealed class DataSymbols
{
    private readonly ProgramData _program;
    private readonly Dictionary<string, List<DataItem>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DataItem> _indexNames = new(StringComparer.OrdinalIgnoreCase);

    internal DataSymbols(ProgramData program)
    {
        _program = program;
        foreach (var item in program.Items)
        {
            if (item.Name is { } name)
            {
                if (!_byName.TryGetValue(name, out var list))
                {
                    list = new List<DataItem>();
                    _byName[name] = list;
                }

                list.Add(item);
            }

            foreach (var index in item.Occurs?.IndexedBy ?? Array.Empty<string>())
            {
                _indexNames.TryAdd(index, item);
            }
        }
    }

    public IEnumerable<string> Names => _byName.Keys;

    // The table item an INDEXED BY name belongs to.
    public DataItem? IndexOwner(string indexName) => _indexNames.TryGetValue(indexName, out var item) ? item : null;

    public SymbolLookup Resolve(QualifiedName name) => Resolve(name.Name, name.Qualifiers);

    public SymbolLookup Resolve(string name, IReadOnlyList<string>? qualifiers = null)
    {
        qualifiers ??= Array.Empty<string>();
        var local = Candidates(name, qualifiers, globalOnly: false);
        if (local.Count > 0)
        {
            return Result(local);
        }

        for (var p = _program.Parent; p is not null; p = p.Parent)
        {
            var inherited = p.Symbols.Candidates(name, qualifiers, globalOnly: true);
            if (inherited.Count > 0)
            {
                return Result(inherited);
            }
        }

        return new SymbolLookup(LookupStatus.NotFound, Array.Empty<DataItem>());
    }

    private static SymbolLookup Result(List<DataItem> candidates)
    {
        if (candidates.Count == 1)
        {
            return new SymbolLookup(LookupStatus.Found, candidates);
        }

        // A reference that names a record itself is unique even when a subordinate item has the same name.
        var records = candidates.Where(c => c.Parent is null).ToList();
        return records.Count == 1
            ? new SymbolLookup(LookupStatus.Found, records)
            : new SymbolLookup(LookupStatus.Ambiguous, candidates);
    }

    private List<DataItem> Candidates(string name, IReadOnlyList<string> qualifiers, bool globalOnly)
    {
        if (!_byName.TryGetValue(name, out var list))
        {
            return new List<DataItem>();
        }

        return list.Where(i => (!globalOnly || i.Global) && Qualifies(i, qualifiers)).ToList();
    }

    private static bool Qualifies(DataItem item, IReadOnlyList<string> qualifiers)
    {
        var q = 0;
        foreach (var ancestor in item.Ancestors())
        {
            if (q < qualifiers.Count && ancestor.Name is { } n && string.Equals(n, qualifiers[q], StringComparison.OrdinalIgnoreCase))
            {
                q++;
            }
        }

        if (q < qualifiers.Count && item.FileName is { } file && string.Equals(file, qualifiers[q], StringComparison.OrdinalIgnoreCase))
        {
            q++;
        }

        return q == qualifiers.Count;
    }
}
