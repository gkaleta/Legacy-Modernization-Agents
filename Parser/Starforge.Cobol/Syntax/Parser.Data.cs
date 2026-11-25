using Starforge.Cobol.Lexing;

namespace Starforge.Cobol.Syntax;

internal sealed partial class Parser
{
    private static readonly HashSet<string> EntryClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "REDEFINES", "RENAMES", "PIC", "PICTURE", "USAGE", "IS", "SIGN", "LEADING", "TRAILING", "OCCURS", "SYNC", "SYNCHRONIZED",
        "JUST", "JUSTIFIED", "BLANK", "VALUE", "VALUES", "EXTERNAL", "GLOBAL", "GROUP-USAGE", "FILLER",
        "DISPLAY", "DISPLAY-1", "NATIONAL", "UTF-8", "BINARY", "PACKED-DECIMAL", "INDEX", "POINTER", "POINTER-32",
        "PROCEDURE-POINTER", "FUNCTION-POINTER", "OBJECT",
        "COMP", "COMP-1", "COMP-2", "COMP-3", "COMP-4", "COMP-5",
        "COMPUTATIONAL", "COMPUTATIONAL-1", "COMPUTATIONAL-2", "COMPUTATIONAL-3", "COMPUTATIONAL-4", "COMPUTATIONAL-5",
    };

    private static readonly HashSet<string> FdClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "IS", "EXTERNAL", "GLOBAL", "BLOCK", "RECORD", "LABEL", "VALUE", "DATA", "RECORDING", "CODE-SET", "LINAGE",
    };

    private DataDivision ParseData(bool hasHeader)
    {
        var first = _pos;
        var sections = new List<DataSection>();
        var errors = new List<ErrorNode>();
        IdmsSchemaSection? schema = null;
        var maps = new List<IdmsMapDeclaration>();
        if (hasHeader)
        {
            Next();
            Next();
            ExpectPeriod("DATA DIVISION");
        }
        else if (IsLevelAt(_pos, out _))
        {
            sections.Add(ParseDataSection(DataSectionKind.WorkingStorage, hasHeader: false));
        }

        while (!AtEnd && !IsDivisionHeader(_pos) && !IsEndProgram(_pos))
        {
            if (IsSectionHeader(_pos))
            {
                var kind = Text(_pos).ToUpperInvariant() switch
                {
                    "FILE" => DataSectionKind.File,
                    "WORKING-STORAGE" => DataSectionKind.WorkingStorage,
                    "LOCAL-STORAGE" => DataSectionKind.LocalStorage,
                    "LINKAGE" => DataSectionKind.Linkage,
                    _ => (DataSectionKind?)null,
                };
                if (kind is null && IsWord(_pos, "SCHEMA"))
                {
                    schema = ParseIdmsSchema(errors);
                    continue;
                }

                if (kind is null && IsWord(_pos, "MAP"))
                {
                    ParseIdmsMaps(maps, errors);
                    continue;
                }

                if (kind is null)
                {
                    errors.Add(SkipError("unsupported-data-section", $"{Text(_pos).ToUpperInvariant()} SECTION is not supported", i => IsSectionHeader(i) || IsDivisionHeader(i) || IsEndProgram(i)));
                    continue;
                }

                sections.Add(ParseDataSection(kind.Value, hasHeader: true));
                continue;
            }

            if (AcceptPeriod())
            {
                continue;
            }

            errors.Add(SkipError("expected-data-section", "Expected a DATA DIVISION section header", i => IsSectionHeader(i) || IsDivisionHeader(i) || IsEndProgram(i)));
        }

        var span = SpanFrom(first);
        return new DataDivision(span, RangeOf(span), sections, errors, schema, maps);
    }

    private bool DataSectionStop(int i) => IsSectionHeader(i) || IsDivisionHeader(i) || IsEndProgram(i);

    // Broadcom, DML Reference for COBOL, Precompiler-Directive Statements, DATA DIVISION: SCHEMA SECTION.
    private IdmsSchemaSection ParseIdmsSchema(List<ErrorNode> errors)
    {
        var first = _pos;
        Next();
        Next();
        ExpectPeriod("SCHEMA SECTION");
        string? subschema = null;
        string? schemaName = null;
        int? version = null;
        if (AcceptWord("DB") && IsWordToken(_pos))
        {
            subschema = Up(_pos);
            Next();
            if (AcceptWord("WITHIN") && IsWordToken(_pos))
            {
                schemaName = Up(_pos);
                Next();
                if (AcceptWord("VERSION") && TryInteger(out var v))
                {
                    version = v;
                }
            }

            ExpectPeriod("DB");
        }

        if (!DataSectionStop(_pos) && !AtEnd)
        {
            errors.Add(SkipError("unexpected-schema-section-text", "Expected DB subschema WITHIN schema in the SCHEMA SECTION", DataSectionStop));
        }

        var span = SpanFrom(first);
        return new IdmsSchemaSection(span, RangeOf(span), subschema, schemaName, version);
    }

    // MAP SECTION. [MAX FIELD LIST IS n.] {MAP map [VERSION n] [TYPE IS {STANDARD|EXTENDED} [PAGING]].}...
    private void ParseIdmsMaps(List<IdmsMapDeclaration> maps, List<ErrorNode> errors)
    {
        Next();
        Next();
        ExpectPeriod("MAP SECTION");
        while (!AtEnd && !DataSectionStop(_pos))
        {
            var first = _pos;
            if (AcceptWord("MAX"))
            {
                AcceptWord("FIELD");
                AcceptWord("LIST");
                AcceptWord("IS");
                TryInteger(out _);
                ExpectPeriod("MAX FIELD LIST");
                continue;
            }

            if (AcceptWord("MAP") && IsWordToken(_pos))
            {
                var name = Up(_pos);
                Next();
                int? version = null;
                string? type = null;
                if (AcceptWord("VERSION") && TryInteger(out var v))
                {
                    version = v;
                }

                if (AcceptWord("TYPE"))
                {
                    AcceptWord("IS");
                    if (IsWordToken(_pos))
                    {
                        type = Up(_pos);
                        Next();
                    }

                    AcceptWord("PAGING");
                }

                ExpectPeriod("MAP");
                var span = SpanFrom(first);
                maps.Add(new IdmsMapDeclaration(span, RangeOf(span), name, version, type));
                continue;
            }

            errors.Add(SkipError("unexpected-map-section-text", "Expected MAX FIELD LIST or MAP in the MAP SECTION", i => IsPeriod(i) || DataSectionStop(i)));
            AcceptPeriod();
        }
    }

    private DataSection ParseDataSection(DataSectionKind kind, bool hasHeader)
    {
        var first = _pos;
        if (hasHeader)
        {
            Next();
            Next();
            ExpectPeriod("SECTION header");
        }

        var files = new List<FileDescription>();
        var errors = new List<ErrorNode>();
        var execs = new List<ExecStatement>();
        var roots = new List<EntryBuilder>();

        // Entries before the first FD in the FILE SECTION have no file; they are reported but kept as records.
        FdBuilder? fd = null;
        var fds = new List<FdBuilder>();
        var stack = new List<EntryBuilder>();
        EntryBuilder? lastNonCondition = null;

        while (!AtEnd && !IsDivisionHeader(_pos) && !IsEndProgram(_pos) && !IsSectionHeader(_pos))
        {
            if (AcceptPeriod())
            {
                continue;
            }

            if (Cur.Kind == TokenKind.ExecBlock)
            {
                var eFirst = _pos;
                var execKind = Cur.Exec;

                // EXEC content is parsed by the Exec stage; until then it stays Lexed.
                _marks[_pos++] = Opaque;
                AcceptPeriod();
                var eSpan = SpanFrom(eFirst);
                execs.Add(new ExecStatement(eSpan, RangeOf(eSpan), execKind));
                continue;
            }

            if (Cur.Kind == TokenKind.CompilerDirective)
            {
                Next();
                continue;
            }

            if (IsFdStart(_pos))
            {
                if (kind != DataSectionKind.File)
                {
                    errors.Add(SkipError("fd-outside-file-section", "FD/SD is only allowed in the FILE SECTION", DataBoundary));
                    continue;
                }

                fd = ParseFileDescription();
                fds.Add(fd);
                stack.Clear();
                lastNonCondition = null;
                continue;
            }

            if (IsLevelAt(_pos, out var level))
            {
                var entry = ParseEntry(level);
                var builder = new EntryBuilder(entry);
                var target = fd?.Records ?? roots;
                switch (level)
                {
                    case 88:
                        if (lastNonCondition is null)
                        {
                            AddDiagnostic("orphan-condition", DiagnosticSeverity.Error, "Level-88 entry has no conditional variable", entry.Span.FirstToken, entry.Span.FirstToken + 1, "88");
                            target.Add(builder);
                        }
                        else
                        {
                            lastNonCondition.Children.Add(builder);
                        }

                        break;
                    case 66:
                        if (stack.Count == 0)
                        {
                            target.Add(builder);
                        }
                        else
                        {
                            stack[0].Children.Add(builder);
                        }

                        lastNonCondition = builder;
                        break;
                    case 1:
                    case 77:
                        stack.Clear();
                        stack.Add(builder);
                        target.Add(builder);
                        lastNonCondition = builder;
                        break;
                    default:
                        while (stack.Count > 0 && stack[^1].Entry.Level >= level)
                        {
                            stack.RemoveAt(stack.Count - 1);
                        }

                        if (stack.Count == 0 || stack[^1].Entry.Level == 77)
                        {
                            AddDiagnostic("entry-without-record", DiagnosticSeverity.Warning, $"Level {level:00} entry is not inside an 01 record", entry.Span.FirstToken, entry.Span.FirstToken + 1, "LEVEL");
                            stack.Clear();
                            target.Add(builder);
                        }
                        else
                        {
                            stack[^1].Children.Add(builder);
                        }

                        stack.Add(builder);
                        lastNonCondition = builder;
                        break;
                }

                continue;
            }

            errors.Add(SkipError("unexpected-data-text", "Expected a level number", DataBoundary));
            AcceptPeriod();
        }

        var frozenFiles = fds.Select(f => f.Freeze()).ToList();
        var span = SpanFrom(first);
        return new DataSection(span, RangeOf(span), kind, frozenFiles, roots.Select(r => r.Freeze()).ToList(), execs, errors);
    }

    private bool IsFdStart(int i) => (IsWord(i, "FD") || IsWord(i, "SD")) && i + 1 < _ts.Count && _ts[i + 1].Kind == TokenKind.Word;

    private bool IsLevelAt(int i, out int level)
    {
        level = 0;
        if (i >= _ts.Count)
        {
            return false;
        }

        var t = _ts[i];
        if (t.Kind == TokenKind.LevelNumber)
        {
            level = t.Detail;
            return true;
        }

        if (t.Kind == TokenKind.IntegerLiteral && t.Length <= 2
            && int.TryParse(_ts.Span(t), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v)
            && v is (>= 1 and <= 49) or 66 or 77 or 88)
        {
            level = v;
            return true;
        }

        return false;
    }

    private bool DataBoundary(int i)
        => IsPeriod(i) || _ts[i].Kind is TokenKind.LevelNumber or TokenKind.ExecBlock
            || IsDivisionHeader(i) || IsSectionHeader(i) || IsEndProgram(i) || (IsFdStart(i) && i > 0 && IsPeriod(i - 1));

    private FdBuilder ParseFileDescription()
    {
        var first = _pos;
        var isSort = IsWord(_pos, "SD");
        Next();
        var name = Text(_pos);
        Next();
        var errors = new List<ErrorNode>();
        var external = false;
        var global = false;
        BlockContains? block = null;
        RecordContains? record = null;
        var labels = new List<string>();
        var dataRecords = new List<string>();
        var valueOf = new List<KeyValuePair<string, string>>();
        string? recordingMode = null;
        string? codeSet = null;

        while (!AtEnd && !IsPeriod(_pos) && !DataBoundary(_pos))
        {
            if (AcceptWord("IS"))
            {
                continue;
            }

            if (AcceptWord("EXTERNAL"))
            {
                external = true;
                continue;
            }

            if (AcceptWord("GLOBAL"))
            {
                global = true;
                continue;
            }

            if (AcceptWord("BLOCK"))
            {
                AcceptWord("CONTAINS");
                if (TryInteger(out var a))
                {
                    int? min = null;
                    var max = a;
                    if (AcceptWord("TO") && TryInteger(out var b))
                    {
                        min = a;
                        max = b;
                    }

                    var records = AcceptWord("RECORDS");
                    if (!records)
                    {
                        AcceptWord("CHARACTERS");
                    }

                    block = new BlockContains(min, max, records);
                    continue;
                }

                errors.Add(SkipError("invalid-block-contains", "BLOCK CONTAINS needs an integer", FdClauseBoundary));
                continue;
            }

            if (AcceptWord("RECORD"))
            {
                if (IsWord(_pos, "IS") && IsWord(_pos + 1, "VARYING"))
                {
                    Next();
                }

                if (AcceptWord("VARYING"))
                {
                    AcceptWord("IN");
                    AcceptWord("SIZE");
                    AcceptWord("FROM");
                    int? min = TryInteger(out var a) ? a : null;
                    int? max = null;
                    if (AcceptWord("TO") && TryInteger(out var b))
                    {
                        max = b;
                    }

                    AcceptWord("CHARACTERS");
                    QualifiedName? depending = null;
                    if (AcceptWord("DEPENDING"))
                    {
                        AcceptWord("ON");
                        if (!AtEnd && Cur.Kind == TokenKind.Word)
                        {
                            depending = ParseQualifiedName();
                        }
                    }

                    record = new RecordContains(min, max, true, depending);
                    continue;
                }

                AcceptWord("CONTAINS");
                if (TryInteger(out var c))
                {
                    int? min = null;
                    var max = c;
                    if (AcceptWord("TO") && TryInteger(out var d))
                    {
                        min = c;
                        max = d;
                    }

                    AcceptWord("CHARACTERS");
                    record = new RecordContains(min, max, false, null);
                    continue;
                }

                errors.Add(SkipError("invalid-record-contains", "RECORD CONTAINS needs an integer", FdClauseBoundary));
                continue;
            }

            if (AcceptWord("LABEL"))
            {
                AcceptRecordIsAre();
                while (!AtEnd && Cur.Kind == TokenKind.Word && !FdClauseWords.Contains(Text(_pos)))
                {
                    labels.Add(Text(_pos).ToUpperInvariant());
                    Next();
                }

                continue;
            }

            if (IsWord(_pos, "DATA") && (IsWord(_pos + 1, "RECORD") || IsWord(_pos + 1, "RECORDS")))
            {
                Next();
                AcceptRecordIsAre();
                while (!AtEnd && (Cur.Kind == TokenKind.Word && !FdClauseWords.Contains(Text(_pos)) || Cur.Kind == TokenKind.Comma))
                {
                    if (Cur.Kind == TokenKind.Word)
                    {
                        dataRecords.Add(Text(_pos));
                    }

                    Next();
                }

                continue;
            }

            if (IsWord(_pos, "VALUE") && IsWord(_pos + 1, "OF"))
            {
                Next();
                Next();
                while (!AtEnd && Cur.Kind == TokenKind.Word && !FdClauseWords.Contains(Text(_pos)))
                {
                    var key = Text(_pos);
                    Next();
                    AcceptWord("IS");
                    if (!AtEnd && (Cur.Kind == TokenKind.Word || Cur.IsLiteral))
                    {
                        valueOf.Add(new KeyValuePair<string, string>(key, Text(_pos)));
                        Next();
                    }
                }

                continue;
            }

            if (AcceptWord("RECORDING"))
            {
                AcceptWord("MODE");
                AcceptWord("IS");
                if (!AtEnd && Cur.Kind == TokenKind.Word)
                {
                    recordingMode = Text(_pos).ToUpperInvariant();
                    Next();
                    continue;
                }

                errors.Add(SkipError("invalid-recording-mode", "RECORDING MODE needs a mode", FdClauseBoundary));
                continue;
            }

            if (AcceptWord("CODE-SET"))
            {
                AcceptWord("IS");
                if (!AtEnd && Cur.Kind == TokenKind.Word)
                {
                    codeSet = Text(_pos);
                    Next();
                }

                continue;
            }

            errors.Add(SkipError(IsWord(_pos, "LINAGE") ? "unsupported-linage" : "unexpected-fd-clause", "Unexpected or unsupported FD clause", FdClauseBoundary));
        }

        AcceptPeriod();
        var span = SpanFrom(first);
        var description = new FileDescription(span, RangeOf(span), isSort, name)
        {
            External = external,
            Global = global,
            Block = block,
            Record = record,
            LabelRecords = labels,
            DataRecords = dataRecords,
            ValueOf = valueOf,
            RecordingMode = recordingMode,
            CodeSet = codeSet,
            Errors = errors,
        };
        return new FdBuilder(description);
    }

    private bool FdClauseBoundary(int i)
        => DataBoundary(i) || (_ts[i].Kind == TokenKind.Word && FdClauseWords.Contains(Text(i)) && !IsWord(i, "IS"));

    // IBM accepts the mixed forms RECORDS IS and RECORD ARE in production code.
    private void AcceptRecordIsAre()
    {
        if (AcceptWord("RECORD") || AcceptWord("RECORDS"))
        {
            if (!AcceptWord("IS"))
            {
                AcceptWord("ARE");
            }
        }
    }

    private DataEntry ParseEntry(int level)
    {
        var first = _pos;
        var unit = _ts.Origin(_pos).Unit;
        Next();
        string? name = null;
        var filler = false;
        if (AcceptWord("FILLER"))
        {
            filler = true;
        }
        else if (!AtEnd && Cur.Kind == TokenKind.Word && !EntryClauseWords.Contains(Text(_pos)))
        {
            name = Text(_pos);
            Next();
        }

        var errors = new List<ErrorNode>();
        string? redefines = null;
        RenamesClause? renames = null;
        PictureClause? picture = null;
        DataUsage? usage = null;
        SignClause? sign = null;
        OccursClause? occurs = null;
        SyncKind? sync = null;
        var justified = false;
        var blankWhenZero = false;
        ValueClause? value = null;
        var external = false;
        var global = false;
        var groupNational = false;

        while (!AtEnd)
        {
            if (AcceptPeriod())
            {
                break;
            }

            if (DataBoundary(_pos) || IsLevelAt(_pos, out _))
            {
                AddDiagnostic("data-entry-missing-period", DiagnosticSeverity.Warning, "Data description entry is not terminated by a period", first, _pos, "LEVEL");
                break;
            }

            if (AcceptWord("REDEFINES"))
            {
                if (!AtEnd && Cur.Kind == TokenKind.Word)
                {
                    redefines = Text(_pos);
                    Next();
                    continue;
                }

                errors.Add(SkipError("missing-redefines-target", "REDEFINES needs a data name", DataBoundary));
                continue;
            }

            if (AcceptWord("RENAMES"))
            {
                if (!AtEnd && Cur.Kind == TokenKind.Word)
                {
                    var from = ParseQualifiedName();
                    QualifiedName? thru = null;
                    if ((AcceptWord("THRU") || AcceptWord("THROUGH")) && !AtEnd && Cur.Kind == TokenKind.Word)
                    {
                        thru = ParseQualifiedName();
                    }

                    renames = new RenamesClause(from, thru);
                    continue;
                }

                errors.Add(SkipError("missing-renames-target", "RENAMES needs a data name", DataBoundary));
                continue;
            }

            if (IsWord(_pos, "PIC") || IsWord(_pos, "PICTURE"))
            {
                Next();
                AcceptWord("IS");
                if (!AtEnd && Cur.Kind == TokenKind.PictureString)
                {
                    var text = Text(_pos);
                    var info = PictureAnalyzer.Analyze(text, _decimalPointIsComma, _currencySymbols);
                    if (info.Errors.Count > 0)
                    {
                        AddDiagnostic("invalid-picture", DiagnosticSeverity.Error, $"PICTURE {text}: {string.Join("; ", info.Errors)}", _pos, _pos + 1, "PIC");
                    }

                    picture = new PictureClause(text, info);
                    Next();
                    continue;
                }

                errors.Add(SkipError("missing-picture-string", "PICTURE needs a character-string", DataBoundary));
                continue;
            }

            if (IsWord(_pos, "USAGE"))
            {
                Next();
                AcceptWord("IS");
                if (TryUsage(out var u))
                {
                    usage = u;
                    continue;
                }

                errors.Add(SkipError("invalid-usage", "Unknown USAGE", DataBoundary));
                continue;
            }

            if (TryUsage(out var bareUsage))
            {
                usage = bareUsage;
                continue;
            }

            if (IsWord(_pos, "SIGN") || IsWord(_pos, "LEADING") || IsWord(_pos, "TRAILING"))
            {
                AcceptWord("SIGN");
                AcceptWord("IS");
                var leading = IsWord(_pos, "LEADING");
                if (AcceptWord("LEADING") || AcceptWord("TRAILING"))
                {
                    var separate = AcceptWord("SEPARATE");
                    if (separate)
                    {
                        AcceptWord("CHARACTER");
                    }

                    sign = new SignClause(leading, separate);
                    continue;
                }

                errors.Add(SkipError("invalid-sign", "SIGN needs LEADING or TRAILING", DataBoundary));
                continue;
            }

            if (AcceptWord("OCCURS"))
            {
                if (TryParseOccurs(out var o))
                {
                    occurs = o;
                    continue;
                }

                errors.Add(SkipError("invalid-occurs", "OCCURS needs an integer", DataBoundary));
                continue;
            }

            if (AcceptWord("SYNC") || AcceptWord("SYNCHRONIZED"))
            {
                sync = AcceptWord("LEFT") ? SyncKind.Left : AcceptWord("RIGHT") ? SyncKind.Right : SyncKind.Unspecified;
                continue;
            }

            if (AcceptWord("JUST") || AcceptWord("JUSTIFIED"))
            {
                AcceptWord("RIGHT");
                justified = true;
                continue;
            }

            if (AcceptWord("BLANK"))
            {
                AcceptWord("WHEN");
                if (!AtEnd && Cur.Figurative == FigurativeKind.Zero)
                {
                    Next();
                    blankWhenZero = true;
                    continue;
                }

                errors.Add(SkipError("invalid-blank-when-zero", "BLANK WHEN needs ZERO", DataBoundary));
                continue;
            }

            if (IsWord(_pos, "VALUE") || IsWord(_pos, "VALUES"))
            {
                value = ParseValueClause(value, level == 88, errors);
                continue;
            }

            if (IsWord(_pos, "WHEN") || IsWord(_pos, "FALSE"))
            {
                value = ParseFalseValue(value, errors);
                continue;
            }

            if (IsWord(_pos, "IS") && (IsWord(_pos + 1, "EXTERNAL") || IsWord(_pos + 1, "GLOBAL")))
            {
                Next();
                continue;
            }

            if (AcceptWord("EXTERNAL"))
            {
                external = true;
                continue;
            }

            if (AcceptWord("GLOBAL"))
            {
                global = true;
                continue;
            }

            if (AcceptWord("GROUP-USAGE"))
            {
                AcceptWord("IS");
                if (AcceptWord("NATIONAL"))
                {
                    groupNational = true;
                    continue;
                }

                errors.Add(SkipError("invalid-group-usage", "GROUP-USAGE needs NATIONAL", DataBoundary));
                continue;
            }

            errors.Add(SkipError("unexpected-data-clause", "Unexpected clause in data description entry", DataBoundary));
        }

        var span = SpanFrom(first);
        return new DataEntry(span, RangeOf(span), level)
        {
            Name = name,
            IsFiller = filler,
            Unit = unit,
            Redefines = redefines,
            Renames = renames,
            Picture = picture,
            Usage = usage,
            Sign = sign,
            Occurs = occurs,
            Sync = sync,
            Justified = justified,
            BlankWhenZero = blankWhenZero,
            Value = value,
            External = external,
            Global = global,
            GroupUsageNational = groupNational,
            HeaderSpan = span,
            Errors = errors,
        };
    }

    private bool TryUsage(out DataUsage usage)
    {
        usage = DataUsage.Display;
        if (AtEnd || Cur.Kind != TokenKind.Word)
        {
            return false;
        }

        DataUsage? u = Text(_pos).ToUpperInvariant() switch
        {
            "DISPLAY" => DataUsage.Display,
            "DISPLAY-1" => DataUsage.Display1,
            "NATIONAL" => DataUsage.National,
            "UTF-8" => DataUsage.Utf8,
            "BINARY" or "COMP" or "COMPUTATIONAL" or "COMP-4" or "COMPUTATIONAL-4" => DataUsage.Binary,
            "COMP-5" or "COMPUTATIONAL-5" => DataUsage.Comp5,
            "PACKED-DECIMAL" or "COMP-3" or "COMPUTATIONAL-3" => DataUsage.PackedDecimal,
            "COMP-1" or "COMPUTATIONAL-1" => DataUsage.Comp1,
            "COMP-2" or "COMPUTATIONAL-2" => DataUsage.Comp2,
            "INDEX" => DataUsage.Index,
            "POINTER" => DataUsage.Pointer,
            "POINTER-32" => DataUsage.Pointer32,
            "PROCEDURE-POINTER" => DataUsage.ProcedurePointer,
            "FUNCTION-POINTER" => DataUsage.FunctionPointer,
            "OBJECT" when IsWord(_pos + 1, "REFERENCE") => DataUsage.ObjectReference,
            _ => null,
        };
        if (u is null)
        {
            return false;
        }

        usage = u.Value;
        Next();
        if (usage == DataUsage.ObjectReference)
        {
            Next();
            if (!AtEnd && Cur.Kind == TokenKind.Word && !EntryClauseWords.Contains(Text(_pos)))
            {
                Next();
            }
        }

        return true;
    }

    private bool TryParseOccurs(out OccursClause occurs)
    {
        occurs = new OccursClause(0, 0, false, null, Array.Empty<OccursKey>(), Array.Empty<string>());
        if (!TryInteger(out var a))
        {
            return false;
        }

        var min = a;
        var max = a;
        var hasTo = false;
        if (AcceptWord("TO"))
        {
            if (TryInteger(out var b))
            {
                max = b;
                hasTo = true;
            }
        }

        AcceptWord("TIMES");
        QualifiedName? depending = null;
        var keys = new List<OccursKey>();
        var indexes = new List<string>();
        while (!AtEnd)
        {
            if (AcceptWord("DEPENDING"))
            {
                AcceptWord("ON");
                if (!AtEnd && Cur.Kind == TokenKind.Word)
                {
                    depending = ParseQualifiedName();
                }

                continue;
            }

            if (IsWord(_pos, "ASCENDING") || IsWord(_pos, "DESCENDING"))
            {
                var ascending = IsWord(_pos, "ASCENDING");
                Next();
                AcceptWord("KEY");
                AcceptWord("IS");
                while (!AtEnd && Cur.Kind == TokenKind.Word && !IsOccursStop(_pos))
                {
                    keys.Add(new OccursKey(ascending, ParseQualifiedName()));
                    if (!AtEnd && Cur.Kind == TokenKind.Comma)
                    {
                        Next();
                    }
                }

                continue;
            }

            if (AcceptWord("INDEXED"))
            {
                AcceptWord("BY");
                while (!AtEnd && Cur.Kind == TokenKind.Word && !IsOccursStop(_pos))
                {
                    indexes.Add(Text(_pos));
                    Next();
                    if (!AtEnd && Cur.Kind == TokenKind.Comma)
                    {
                        Next();
                    }
                }

                continue;
            }

            break;
        }

        occurs = new OccursClause(hasTo ? min : max, max, hasTo, depending, keys, indexes);
        return true;
    }

    private bool IsOccursStop(int i)
        => EntryClauseWords.Contains(Text(i)) || IsWord(i, "ASCENDING") || IsWord(i, "DESCENDING") || IsWord(i, "INDEXED") || IsWord(i, "DEPENDING");

    // Only condition names take a list; elsewhere a following integer is more likely a level number after a missing period.
    private ValueClause ParseValueClause(ValueClause? existing, bool allowList, List<ErrorNode> errors)
    {
        var plural = IsWord(_pos, "VALUES");
        Next();
        if (plural)
        {
            AcceptWord("ARE");
        }
        else
        {
            AcceptWord("IS");
        }

        var rawFirst = _pos;
        var values = new List<ValueEntry>(existing?.Values ?? Array.Empty<ValueEntry>());
        while (!AtEnd)
        {
            if (Cur.Kind == TokenKind.Comma)
            {
                Next();
                continue;
            }

            if (!TryValueLiteral(out var from))
            {
                break;
            }

            ValueLiteral? thru = null;
            if (IsWord(_pos, "THRU") || IsWord(_pos, "THROUGH"))
            {
                Next();
                if (!TryValueLiteral(out var to))
                {
                    errors.Add(SkipError("invalid-value-range", "THRU needs a literal", DataBoundary));
                    break;
                }

                thru = to;
            }

            values.Add(new ValueEntry(from, thru));
            if (!allowList)
            {
                break;
            }
        }

        if (values.Count == (existing?.Values.Count ?? 0))
        {
            errors.Add(SkipError("missing-value-literal", "VALUE needs a literal", DataBoundary));
        }

        var raw = rawFirst < _pos ? RawText(rawFirst, _pos) : string.Empty;
        return new ValueClause(values, existing?.FalseValue, existing is null || existing.Raw.Length == 0 ? raw : existing.Raw + " " + raw);
    }

    private ValueClause? ParseFalseValue(ValueClause? existing, List<ErrorNode> errors)
    {
        if (AcceptWord("WHEN"))
        {
            AcceptWord("SET");
            AcceptWord("TO");
        }

        if (AcceptWord("FALSE"))
        {
            AcceptWord("IS");
            if (TryValueLiteral(out var literal))
            {
                return (existing ?? new ValueClause(Array.Empty<ValueEntry>(), null, string.Empty)) with { FalseValue = literal };
            }
        }

        errors.Add(SkipError("invalid-false-value", "WHEN SET TO FALSE needs a literal", DataBoundary));
        return existing;
    }

    private bool TryValueLiteral(out ValueLiteral literal)
    {
        literal = new ValueLiteral(TokenKind.Word, string.Empty, FigurativeKind.None, false);
        if (AtEnd)
        {
            return false;
        }

        var all = false;
        if (Cur.Figurative == FigurativeKind.All)
        {
            all = true;
            Next();
            if (AtEnd)
            {
                return false;
            }
        }

        if (Cur.Kind == TokenKind.Figurative && Cur.Figurative != FigurativeKind.All)
        {
            literal = new ValueLiteral(TokenKind.Figurative, Text(_pos).ToUpperInvariant(), Cur.Figurative, all);
            Next();
            return true;
        }

        if ((Cur.Kind == TokenKind.Plus || Cur.Kind == TokenKind.Minus) && _pos + 1 < _ts.Count
            && _ts[_pos + 1].Kind is TokenKind.IntegerLiteral or TokenKind.DecimalLiteral or TokenKind.FloatLiteral)
        {
            var sign = Text(_pos);
            Next();
            literal = new ValueLiteral(Cur.Kind, sign + Text(_pos), FigurativeKind.None, all);
            Next();
            return true;
        }

        if (!Cur.IsLiteral)
        {
            return false;
        }

        var kind = Cur.Kind;
        var text = Text(_pos);
        Next();
        while (!AtEnd && Cur.Kind == TokenKind.Ampersand && _pos + 1 < _ts.Count && _ts[_pos + 1].IsLiteral)
        {
            Next();
            text += " & " + Text(_pos);
            Next();
        }

        literal = new ValueLiteral(kind, text, FigurativeKind.None, all);
        return true;
    }

    // Original token text joined by single spaces; continuation lines are already merged by the reader.
    private string RawText(int from, int to) => JoinTokens(from, to);

    private sealed class EntryBuilder
    {
        public EntryBuilder(DataEntry entry) => Entry = entry;

        public DataEntry Entry { get; }

        public List<EntryBuilder> Children { get; } = new();

        public DataEntry Freeze()
        {
            if (Children.Count == 0)
            {
                return Entry;
            }

            var children = Children.Select(c => c.Freeze()).ToList();
            var last = children[^1].Span;
            var span = Entry.Span with { EndToken = Math.Max(Entry.Span.EndToken, last.EndToken), End = Math.Max(Entry.Span.End, last.End) };
            return Entry with { Children = children, Span = span };
        }
    }

    private sealed class FdBuilder
    {
        public FdBuilder(FileDescription description) => Description = description;

        public FileDescription Description { get; }

        public List<EntryBuilder> Records { get; } = new();

        public FileDescription Freeze() => Description with { Records = Records.Select(r => r.Freeze()).ToList() };
    }
}
