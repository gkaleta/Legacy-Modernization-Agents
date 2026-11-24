using Starforge.Cobol.Source;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Semantics;

public enum DataCategory
{
    Group,
    Alphabetic,
    Alphanumeric,
    AlphanumericEdited,
    Numeric,
    NumericEdited,
    ExternalFloatingPoint,
    InternalFloatingPoint,
    National,
    NationalEdited,
    Dbcs,
    Utf8,
    Boolean,
    Index,
    Pointer,
    ObjectReference,
    Condition,
    Renames,
    Unknown,
}

// One data description entry with its resolved attributes and storage layout. Offsets are relative to the
// start of the item's 01/77 record (each FD record and each redefining 01 starts at 0).
public sealed class DataItem
{
    private readonly List<DataItem> _children = new();

    internal DataItem(DataEntry entry, DataItem? parent, int index, DataSectionKind section, string? fileName, string? copybook)
    {
        Entry = entry;
        Parent = parent;
        Index = index;
        Section = section;
        FileName = fileName;
        FromCopybook = copybook;
        Record = parent?.Record ?? this;
        Path = parent is null || entry.Level is 1 or 77 ? entry.DisplayName : parent.Path + "." + entry.DisplayName;
    }

    public DataEntry Entry { get; }

    // Preorder position within the program's data division.
    public int Index { get; }

    public int Level => Entry.Level;

    public string? Name => Entry.Name;

    public string DisplayName => Entry.DisplayName;

    public bool IsFiller => Entry.Name is null;

    // Names from the record down, e.g. "CUSTOMER-RECORD.CUST-ADDR.CITY".
    public string Path { get; }

    public DataItem? Parent { get; }

    public DataItem Record { get; }

    public IReadOnlyList<DataItem> Children => _children;

    public DataSectionKind Section { get; }

    public string? FileName { get; }

    public string? FromCopybook { get; }

    public SourceRange Range => Entry.Range;

    public DataUsage Usage { get; internal set; }

    public DataCategory Category { get; internal set; }

    public string? Picture => Entry.Picture?.Text;

    public int Digits { get; internal set; }

    public int Scale { get; internal set; }

    public bool Signed { get; internal set; }

    public bool SignLeading { get; internal set; }

    public bool SignSeparate { get; internal set; }

    public int Offset { get; internal set; }

    // Bytes of one occurrence, including any slack bytes inside it.
    public int Length { get; internal set; }

    // Bytes of all occurrences (maximum for OCCURS DEPENDING ON).
    public int TotalLength => Occurs is null ? Length : Length * Occurs.Max;

    public OccursClause? Occurs => Entry.Occurs;

    public string? RedefinesName => Entry.Redefines;

    public DataItem? Redefines { get; internal set; }

    public DataItem? RenamesFrom { get; internal set; }

    public DataItem? RenamesThru { get; internal set; }

    public bool IsCondition => Entry.Level == 88;

    public bool IsRenames => Entry.Level == 66;

    public bool HasStorage => !IsCondition && !IsRenames;

    public bool IsGroup => Category == DataCategory.Group;

    public bool Synchronized { get; internal set; }

    public int Alignment { get; internal set; } = 1;

    // Slack bytes inserted before this item to align a SYNCHRONIZED item it contains or is.
    public int SlackBefore { get; internal set; }

    // Slack bytes added at the end of each occurrence of an OCCURS group so every occurrence is aligned.
    public int OccurrenceSlack { get; internal set; }

    public bool Global { get; internal set; }

    public bool External { get; internal set; }

    public string? Value => Entry.Value?.Raw;

    internal void AddChild(DataItem child) => _children.Add(child);

    public IEnumerable<DataItem> SelfAndDescendants()
    {
        yield return this;
        foreach (var d in _children.SelectMany(c => c.SelfAndDescendants()))
        {
            yield return d;
        }
    }

    // Ancestors from the parent outward to the record.
    public IEnumerable<DataItem> Ancestors()
    {
        for (var p = Parent; p is not null; p = p.Parent)
        {
            yield return p;
        }
    }

    public override string ToString() => $"{Level:00} {Path} @{Offset}+{Length}";
}

public sealed record FileData(FileControlEntry? Select, FileDescription? Description, IReadOnlyList<DataItem> Records)
{
    public string Name => Description?.Name ?? Select?.FileName ?? string.Empty;
}

public sealed class ProgramData
{
    internal ProgramData(ProgramUnit program, ProgramData? parent, IReadOnlyList<DataItem> records, IReadOnlyList<FileData> files, IReadOnlyList<Diagnostic> diagnostics)
    {
        Program = program;
        Parent = parent;
        Records = records;
        Files = files;
        Diagnostics = diagnostics;
        Items = records.SelectMany(r => r.SelfAndDescendants()).OrderBy(i => i.Index).ToList();
        Symbols = new DataSymbols(this);
    }

    public ProgramUnit Program { get; }

    public string? Name => Program.Name;

    // The directly containing program, whose GLOBAL items are visible here.
    public ProgramData? Parent { get; }

    // 01/77 entries (and stray higher levels) in source order across all sections.
    public IReadOnlyList<DataItem> Records { get; }

    public IReadOnlyList<DataItem> Items { get; }

    public IReadOnlyList<FileData> Files { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public DataSymbols Symbols { get; }

    public bool SlackBytesInserted => Items.Any(i => i.SlackBefore > 0 || i.OccurrenceSlack > 0);
}
