using Starforge.Cobol.Copy;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Semantics;

// Storage layout per IBM Enterprise COBOL rules. Programs are returned in CompilationUnitSyntax.AllPrograms() order.
public static class DataLayout
{
    public static IReadOnlyList<ProgramData> Compute(CompilationUnitSyntax syntax, IReadOnlyList<SourceUnit> units)
    {
        var result = new List<ProgramData>();
        foreach (var program in syntax.Programs)
        {
            Add(program, null, units, result);
        }

        return result;
    }

    private static void Add(ProgramUnit program, ProgramData? parent, IReadOnlyList<SourceUnit> units, List<ProgramData> into)
    {
        var data = new Builder(program, units).Build(parent);
        into.Add(data);
        foreach (var nested in program.Nested)
        {
            Add(nested, data, units, into);
        }
    }

    private sealed class Builder
    {
        private readonly ProgramUnit _program;
        private readonly IReadOnlyList<SourceUnit> _units;
        private readonly List<Diagnostic> _diagnostics = new();
        private int _index;

        public Builder(ProgramUnit program, IReadOnlyList<SourceUnit> units)
        {
            _program = program;
            _units = units;
        }

        public ProgramData Build(ProgramData? parent)
        {
            var records = new List<DataItem>();
            var files = new List<FileData>();
            var selects = _program.Environment?.InputOutput?.Files ?? Array.Empty<FileControlEntry>();
            var described = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in _program.Data?.Sections ?? Array.Empty<DataSection>())
            {
                foreach (var fd in section.Files)
                {
                    var fileRecords = new List<DataItem>();
                    foreach (var entry in fd.Records)
                    {
                        var record = BuildRecord(entry, section.Kind, fd.Name, fd.Global, fd.External);
                        fileRecords.Add(record);
                        records.Add(record);
                    }

                    var select = selects.FirstOrDefault(s => string.Equals(s.FileName, fd.Name, StringComparison.OrdinalIgnoreCase));
                    if (select is null)
                    {
                        Warn("fd-without-select", $"FD {fd.Name} has no SELECT entry", fd);
                    }

                    described.Add(fd.Name);
                    files.Add(new FileData(select, fd, fileRecords));
                }

                var sectionRecords = new List<DataItem>();
                foreach (var entry in section.Entries)
                {
                    var record = BuildRecord(entry, section.Kind, null, false, false);
                    if (record.RedefinesName is { } targetName)
                    {
                        // A record-level REDEFINES may be any size; it only names the storage it overlays.
                        record.Redefines = sectionRecords.LastOrDefault(r => r.Level == record.Level && string.Equals(r.Name, targetName, StringComparison.OrdinalIgnoreCase));
                        if (record.Redefines is null)
                        {
                            Error("redefines-target-not-found", $"{record.Path} REDEFINES {targetName}, which is not a preceding record in the section", record);
                        }
                    }

                    sectionRecords.Add(record);
                    records.Add(record);
                }
            }

            foreach (var select in selects.Where(s => !described.Contains(s.FileName)))
            {
                files.Add(new FileData(select, null, Array.Empty<DataItem>()));
            }

            return new ProgramData(_program, parent, records, files, _diagnostics);
        }

        private DataItem BuildRecord(DataEntry entry, DataSectionKind section, string? file, bool fdGlobal, bool fdExternal)
        {
            var record = Create(entry, null, section, file);
            record.Global = entry.Global || fdGlobal;
            record.External = entry.External || fdExternal;
            Resolve(record, inheritedUsage: null, inheritedSign: null, inheritedSync: false, groupNational: false);
            Layout(record, 0);
            ResolveRenames(record);
            return record;
        }

        private DataItem Create(DataEntry entry, DataItem? parent, DataSectionKind section, string? file)
        {
            var copybook = entry.Unit > 0 && entry.Unit < _units.Count ? _units[entry.Unit].Name : null;
            var item = new DataItem(entry, parent, _index++, section, file, copybook);
            if (parent is not null)
            {
                item.Global = parent.Global;
                item.External = parent.External;
            }

            foreach (var child in entry.Children)
            {
                item.AddChild(Create(child, item, section, file));
            }

            return item;
        }

        // Attributes declared on a group (USAGE, SIGN, SYNC, GROUP-USAGE NATIONAL) apply to its elementary items.
        private void Resolve(DataItem item, DataUsage? inheritedUsage, SignClause? inheritedSign, bool inheritedSync, bool groupNational)
        {
            var entry = item.Entry;
            if (item.IsCondition || item.IsRenames)
            {
                item.Category = item.IsCondition ? DataCategory.Condition : DataCategory.Renames;
                item.Usage = item.Parent?.Usage ?? DataUsage.Display;
                return;
            }

            var usage = entry.Usage ?? inheritedUsage;
            var sign = entry.Sign ?? inheritedSign;
            var sync = entry.Sync is not null || inheritedSync;
            var national = groupNational || entry.GroupUsageNational;
            var storageChildren = item.Children.Where(c => c.HasStorage).ToList();

            if (storageChildren.Count > 0 && entry.Picture is null)
            {
                item.Category = DataCategory.Group;
                item.Usage = national ? DataUsage.National : usage ?? DataUsage.Display;
                foreach (var child in item.Children)
                {
                    Resolve(child, entry.Usage ?? inheritedUsage, sign, sync, national);
                }

                return;
            }

            if (storageChildren.Count > 0)
            {
                Error("picture-on-group", $"Group item {item.Path} has a PICTURE clause", item);
            }

            foreach (var child in item.Children)
            {
                Resolve(child, usage, sign, sync, national);
            }

            var pic = entry.Picture?.Info;
            var effective = usage ?? DefaultUsage(pic, national);
            item.Usage = effective;
            item.Synchronized = sync;
            if (pic is not null)
            {
                item.Digits = pic.Digits;
                item.Scale = pic.Scale;
                item.Signed = pic.Signed;
            }

            if (item.Signed && pic?.Category == PictureCategory.Numeric && effective is DataUsage.Display or DataUsage.National && sign is not null)
            {
                item.SignLeading = sign.Leading;
                item.SignSeparate = sign.Separate;
            }

            item.Category = effective switch
            {
                DataUsage.Index => DataCategory.Index,
                DataUsage.Pointer or DataUsage.Pointer32 or DataUsage.ProcedurePointer or DataUsage.FunctionPointer => DataCategory.Pointer,
                DataUsage.ObjectReference => DataCategory.ObjectReference,
                DataUsage.Comp1 or DataUsage.Comp2 => DataCategory.InternalFloatingPoint,
                _ => pic is null ? DataCategory.Unknown : CategoryOf(pic.Category),
            };

            if (pic is null && item.Category == DataCategory.Unknown)
            {
                Error("missing-picture", $"Elementary item {item.Path} has no PICTURE clause", item);
            }

            if (pic is not null && effective is DataUsage.Binary or DataUsage.Comp5 or DataUsage.PackedDecimal && pic.Category != PictureCategory.Numeric)
            {
                Error("usage-picture-mismatch", $"USAGE {effective} requires a numeric PICTURE on {item.Path}", item);
            }
        }

        private static DataUsage DefaultUsage(PictureInfo? pic, bool groupNational)
            => pic?.Category switch
            {
                PictureCategory.National or PictureCategory.NationalEdited => DataUsage.National,
                PictureCategory.Dbcs => DataUsage.Display1,
                PictureCategory.Utf8 => DataUsage.Utf8,
                _ => groupNational ? DataUsage.National : DataUsage.Display,
            };

        private static DataCategory CategoryOf(PictureCategory category) => category switch
        {
            PictureCategory.Alphabetic => DataCategory.Alphabetic,
            PictureCategory.Alphanumeric => DataCategory.Alphanumeric,
            PictureCategory.AlphanumericEdited => DataCategory.AlphanumericEdited,
            PictureCategory.Numeric => DataCategory.Numeric,
            PictureCategory.NumericEdited => DataCategory.NumericEdited,
            PictureCategory.ExternalFloatingPoint => DataCategory.ExternalFloatingPoint,
            PictureCategory.National => DataCategory.National,
            PictureCategory.NationalEdited => DataCategory.NationalEdited,
            PictureCategory.Dbcs => DataCategory.Dbcs,
            PictureCategory.Utf8 => DataCategory.Utf8,
            PictureCategory.Boolean => DataCategory.Boolean,
            _ => DataCategory.Unknown,
        };

        // Lays the item out at offset (relative to its record) and returns the bytes it occupies (all occurrences).
        private int Layout(DataItem item, int offset)
        {
            item.Offset = offset;
            if (!item.IsGroup)
            {
                item.Length = ElementaryLength(item);
                item.Alignment = item.Synchronized ? AlignmentOf(item) : 1;
                foreach (var condition in item.Children.Where(c => c.IsCondition))
                {
                    condition.Offset = offset;
                }

                return item.TotalLength;
            }

            var cursor = offset;
            var end = offset;
            var alignment = 1;
            DataItem? previous = null;
            foreach (var child in item.Children)
            {
                if (child.IsCondition)
                {
                    child.Offset = offset;
                    continue;
                }

                if (child.IsRenames)
                {
                    continue;
                }

                int start;
                DataItem? target = null;
                if (child.RedefinesName is { } targetName)
                {
                    target = FindRedefinesTarget(item.Children, child, targetName, previous);
                    child.Redefines = target;
                    start = target?.Offset ?? cursor;
                }
                else
                {
                    start = cursor;
                }

                var childEnd = LayoutAligned(child, start, target is null);
                alignment = Math.Max(alignment, child.Alignment);
                if (target is not null && childEnd > target.Offset + target.TotalLength)
                {
                    // IBM grows the redefined storage to the largest redefinition, so later siblings move too.
                    Warn("redefines-larger", $"{child.Path} ({childEnd - start} bytes) is larger than the item it redefines, {target.Path} ({target.TotalLength} bytes)", child);
                }

                cursor = Math.Max(cursor, childEnd);

                end = Math.Max(end, childEnd);
                previous = child;
            }

            item.Alignment = alignment;
            var length = end - offset;
            if (item.Occurs is not null && alignment > 1 && length % alignment != 0)
            {
                item.OccurrenceSlack = alignment - length % alignment;
                length += item.OccurrenceSlack;
            }

            item.Length = length;
            return item.TotalLength;
        }

        // Slack bytes go directly before a misaligned SYNCHRONIZED elementary item, measured from the record start,
        // which IBM aligns on a doubleword; the containing group grows by the slack.
        private int LayoutAligned(DataItem child, int start, bool canSlack)
        {
            child.SlackBefore = 0;
            if (canSlack && !child.IsGroup && child.Synchronized)
            {
                var alignment = AlignmentOf(child);
                var misalignment = start % alignment;
                if (misalignment != 0)
                {
                    child.SlackBefore = alignment - misalignment;
                    start += child.SlackBefore;
                }
            }

            return start + Layout(child, start);
        }

        private DataItem? FindRedefinesTarget(IReadOnlyList<DataItem> siblings, DataItem child, string name, DataItem? previous)
        {
            DataItem? target = null;
            foreach (var s in siblings)
            {
                if (ReferenceEquals(s, child))
                {
                    break;
                }

                if (s.HasStorage && s.Level == child.Level && s.Name is not null && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    target = s;
                }
            }

            if (target is null)
            {
                Error("redefines-target-not-found", $"{child.Path} REDEFINES {name}, which is not a preceding item at the same level", child);
            }
            else if (previous is not null && !ReferenceEquals(previous, target) && !ReferenceEquals(previous.Redefines, target) && previous.Redefines is null)
            {
                Warn("redefines-not-adjacent", $"{child.Path} REDEFINES {name}, which is not the immediately preceding entry", child);
            }

            return target;
        }

        private int ElementaryLength(DataItem item)
        {
            var pic = item.Entry.Picture?.Info;
            switch (item.Usage)
            {
                // Sizes assume AMODE 31 / LP(32): addresses and indexes are fullwords.
                case DataUsage.Index:
                case DataUsage.Pointer:
                case DataUsage.Pointer32:
                case DataUsage.FunctionPointer:
                case DataUsage.ObjectReference:
                    return 4;
                case DataUsage.ProcedurePointer:
                    return 8;
                case DataUsage.Comp1:
                    return 4;
                case DataUsage.Comp2:
                    return 8;
            }

            if (pic is null)
            {
                return 0;
            }

            switch (item.Usage)
            {
                case DataUsage.Binary:
                case DataUsage.Comp5:
                    if (pic.Digits > 18)
                    {
                        Error("binary-too-many-digits", $"Binary item {item.Path} has {pic.Digits} digits; the maximum is 18", item);
                    }

                    return pic.Digits <= 4 ? 2 : pic.Digits <= 9 ? 4 : 8;
                case DataUsage.PackedDecimal:
                    return pic.Digits / 2 + 1;
                case DataUsage.National:
                    return 2 * (pic.Size + (item.SignSeparate ? 1 : 0));
                case DataUsage.Display1:
                    return 2 * pic.Size;
                case DataUsage.Utf8:
                    return 4 * pic.Size;
                default:
                    return pic.Category is PictureCategory.National or PictureCategory.NationalEdited or PictureCategory.Dbcs
                        ? 2 * pic.Size
                        : pic.Size + (item.SignSeparate ? 1 : 0);
            }
        }

        private static int AlignmentOf(DataItem item) => item.Usage switch
        {
            DataUsage.Binary or DataUsage.Comp5 => item.Digits <= 4 ? 2 : 4,
            DataUsage.Index or DataUsage.Pointer or DataUsage.Pointer32 or DataUsage.FunctionPointer or DataUsage.ProcedurePointer or DataUsage.ObjectReference => 4,
            DataUsage.Comp1 => 4,
            DataUsage.Comp2 => 8,
            _ => 1,
        };

        private void ResolveRenames(DataItem record)
        {
            foreach (var item in record.Children.Where(c => c.IsRenames))
            {
                var renames = item.Entry.Renames;
                if (renames is null)
                {
                    Error("missing-renames", $"Level-66 entry {item.Path} has no RENAMES clause", item);
                    continue;
                }

                var from = FindInRecord(record, renames.From);
                var thru = renames.Thru is null ? null : FindInRecord(record, renames.Thru);
                if (from is null || (renames.Thru is not null && thru is null))
                {
                    Error("renames-target-not-found", $"RENAMES target of {item.Path} was not found in record {record.Path}", item);
                    continue;
                }

                item.RenamesFrom = from;
                item.RenamesThru = thru;
                item.Offset = from.Offset;
                var end = thru is null ? from.Offset + from.TotalLength : Math.Max(thru.Offset + thru.TotalLength, from.Offset + from.TotalLength);
                if (thru is not null && thru.Offset < from.Offset)
                {
                    Error("renames-order", $"RENAMES THRU item of {item.Path} precedes its first item", item);
                }

                item.Length = end - from.Offset;
                item.Usage = from.Usage;
            }
        }

        private static DataItem? FindInRecord(DataItem record, QualifiedName name)
            => record.SelfAndDescendants()
                .Where(i => i.HasStorage && i.Level is not 1 && i.Name is not null && string.Equals(i.Name, name.Name, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(i => name.Qualifiers.All(q => i.Ancestors().Any(a => string.Equals(a.Name, q, StringComparison.OrdinalIgnoreCase))));

        private void Error(string code, string message, DataItem item)
            => _diagnostics.Add(Diagnostic.Create(code, DiagnosticSeverity.Error, message, item.Range, code));

        private void Warn(string code, string message, DataItem item)
            => _diagnostics.Add(Diagnostic.Create(code, DiagnosticSeverity.Warning, message, item.Range, code));

        private void Warn(string code, string message, FileDescription fd)
            => _diagnostics.Add(Diagnostic.Create(code, DiagnosticSeverity.Warning, message, fd.Range, code));
    }
}
