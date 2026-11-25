using Starforge.Cobol.Lexing;

namespace Starforge.Cobol.Syntax;

internal sealed partial class Parser
{
    private static readonly HashSet<string> SelectClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ORGANIZATION", "ACCESS", "RECORD", "ALTERNATE", "RELATIVE", "FILE", "STATUS", "RESERVE", "PADDING", "PASSWORD",
        "SEQUENTIAL", "INDEXED", "LINE", "LOCK", "SHARING", "COLLATING", "SORT", "USING",
    };

    private static readonly HashSet<string> EnvironmentParagraphs = new(StringComparer.OrdinalIgnoreCase)
    {
        "SOURCE-COMPUTER", "OBJECT-COMPUTER", "SPECIAL-NAMES", "REPOSITORY", "FILE-CONTROL", "I-O-CONTROL",
    };

    private static readonly HashSet<string> IoControlKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "RERUN", "SAME", "MULTIPLE", "APPLY",
    };

    private EnvironmentDivision ParseEnvironment()
    {
        var first = _pos;
        Next();
        Next();
        ExpectPeriod("ENVIRONMENT DIVISION");
        var errors = new List<ErrorNode>();

        int? configFirst = null;
        var configEnd = -1;
        string? sourceComputer = null;
        var debugging = false;
        string? objectComputer = null;
        SpecialNamesParagraph? specialNames = null;

        int? ioFirst = null;
        var ioEnd = -1;
        var files = new List<FileControlEntry>();
        var ioControl = new List<IoControlEntry>();
        var paragraph = string.Empty;
        IdmsControlSection? idms = null;

        while (!AtEnd && !IsDivisionHeader(_pos) && !IsEndProgram(_pos))
        {
            if (IsWord(_pos, "IDMS-CONTROL") && IsWord(_pos + 1, "SECTION"))
            {
                idms = ParseIdmsControl();
                paragraph = string.Empty;
                continue;
            }

            if (IsWord(_pos, "CONFIGURATION") && IsWord(_pos + 1, "SECTION"))
            {
                configFirst ??= _pos;
                Next();
                Next();
                ExpectPeriod("CONFIGURATION SECTION");
                configEnd = _pos;
                paragraph = string.Empty;
                continue;
            }

            if (IsWord(_pos, "INPUT-OUTPUT") && IsWord(_pos + 1, "SECTION"))
            {
                ioFirst ??= _pos;
                Next();
                Next();
                ExpectPeriod("INPUT-OUTPUT SECTION");
                ioEnd = _pos;
                paragraph = string.Empty;
                continue;
            }

            if (Cur.Kind == TokenKind.Word && EnvironmentParagraphs.Contains(Text(_pos)) && IsPeriod(_pos + 1))
            {
                paragraph = Text(_pos).ToUpperInvariant();
                var isConfig = paragraph is "SOURCE-COMPUTER" or "OBJECT-COMPUTER" or "SPECIAL-NAMES" or "REPOSITORY";
                if (isConfig)
                {
                    configFirst ??= _pos;
                }
                else
                {
                    ioFirst ??= _pos;
                }

                Next();
                Next();
                switch (paragraph)
                {
                    case "SOURCE-COMPUTER":
                        (sourceComputer, debugging) = ParseComputerParagraph(errors, allowDebugging: true);
                        break;
                    case "OBJECT-COMPUTER":
                        (objectComputer, _) = ParseComputerParagraph(errors, allowDebugging: false);
                        break;
                    case "SPECIAL-NAMES":
                        specialNames = ParseSpecialNames(_pos - 2, errors);
                        break;
                    case "REPOSITORY":
                        errors.Add(SkipError("unsupported-repository", "REPOSITORY paragraph is not supported", EnvironmentBoundary));
                        break;
                }

                if (isConfig)
                {
                    configEnd = _pos;
                }
                else
                {
                    ioEnd = _pos;
                }

                continue;
            }

            if (paragraph == "FILE-CONTROL" && IsWord(_pos, "SELECT"))
            {
                files.Add(ParseSelect(errors));
                ioEnd = _pos;
                continue;
            }

            if (paragraph == "I-O-CONTROL" && (Cur.Kind == TokenKind.Word && IoControlKinds.Contains(Text(_pos))))
            {
                ioControl.Add(ParseIoControlEntry());
                AcceptPeriod();
                ioEnd = _pos;
                continue;
            }

            if (paragraph == "I-O-CONTROL" && AcceptPeriod())
            {
                ioEnd = _pos;
                continue;
            }

            errors.Add(SkipError("unexpected-environment-text", "Unexpected text in ENVIRONMENT DIVISION", EnvironmentBoundary));
            AcceptPeriod();
        }

        ConfigurationSection? config = null;
        if (configFirst is int cf)
        {
            var cspan = MakeSpan(cf, Math.Max(cf + 1, configEnd));
            config = new ConfigurationSection(cspan, RangeOf(cspan), sourceComputer, debugging, objectComputer, specialNames);
        }

        InputOutputSection? io = null;
        if (ioFirst is int iof)
        {
            var ispan = MakeSpan(iof, Math.Max(iof + 1, ioEnd));
            io = new InputOutputSection(ispan, RangeOf(ispan), files, ioControl);
        }

        var span = SpanFrom(first);
        return new EnvironmentDivision(span, RangeOf(span), config, io, errors, idms);
    }

    // Broadcom, DML Reference for COBOL, Precompiler-Directive Statements, ENVIRONMENT DIVISION.
    private IdmsControlSection ParseIdmsControl()
    {
        var first = _pos;
        Next();
        Next();
        ExpectPeriod("IDMS-CONTROL SECTION");
        string? mode = null;
        string? records = null;
        var debug = false;
        if (AcceptWord("PROTOCOL"))
        {
            AcceptPeriod();
            while (!AtEnd && !IsPeriod(_pos))
            {
                if (AcceptWord("MODE"))
                {
                    AcceptWord("IS");
                    if (IsWordToken(_pos))
                    {
                        mode = Up(_pos);
                        Next();
                    }

                    debug |= AcceptWord("DEBUG");
                }
                else if (AcceptWord("SUBSCHEMA-NAMES"))
                {
                    AcceptWord("LENGTH");
                    AcceptWord("IS");
                    TryInteger(out _);
                }
                else if (AcceptWord("IDMS-RECORDS"))
                {
                    if (AcceptWord("MANUAL"))
                    {
                        records = "MANUAL";
                    }
                    else if (AcceptWord("WITHIN") && IsWordToken(_pos))
                    {
                        records = Up(_pos);
                        Next();
                        AcceptWord("SECTION");
                        if (AcceptWord("LEVELS"))
                        {
                            AcceptWord("INCREMENTED");
                            AcceptWord("BY");
                            TryInteger(out _);
                        }
                    }
                }
                else
                {
                    break;
                }
            }

            ExpectPeriod("PROTOCOL");
        }

        var span = SpanFrom(first);
        return new IdmsControlSection(span, RangeOf(span), mode, debug, records);
    }

    private bool EnvironmentBoundary(int i)
        => IsPeriod(i) || IsDivisionHeader(i) || IsEndProgram(i) || IsSectionHeader(i)
            || (IsWord(i, "SELECT") && i > 0 && IsPeriod(i - 1))
            || (_ts[i].Kind == TokenKind.Word && EnvironmentParagraphs.Contains(Text(i)) && IsPeriod(i + 1));

    private (string? Name, bool Debugging) ParseComputerParagraph(List<ErrorNode> errors, bool allowDebugging)
    {
        string? name = null;
        var debugging = false;
        if (!AtEnd && Cur.Kind == TokenKind.Word && !EnvironmentBoundary(_pos))
        {
            name = Text(_pos);
            Next();
        }

        while (!AtEnd && !EnvironmentBoundary(_pos))
        {
            if (allowDebugging && (IsWord(_pos, "WITH") || IsWord(_pos, "DEBUGGING")))
            {
                AcceptWord("WITH");
                if (AcceptWord("DEBUGGING"))
                {
                    AcceptWord("MODE");
                    debugging = true;
                    continue;
                }
            }

            // OBJECT-COMPUTER options (MEMORY SIZE, PROGRAM COLLATING SEQUENCE, SEGMENT-LIMIT) carry no layout meaning.
            if (!allowDebugging && Cur.Kind is TokenKind.Word or TokenKind.IntegerLiteral)
            {
                Next();
                continue;
            }

            errors.Add(SkipError("unexpected-computer-text", "Unexpected text in computer paragraph", EnvironmentBoundary));
        }

        AcceptPeriod();
        return (name, debugging);
    }

    private SpecialNamesParagraph ParseSpecialNames(int first, List<ErrorNode> errors)
    {
        var comma = false;
        var currencies = new List<CurrencySign>();
        var classes = new List<ClassDefinition>();
        var symbolics = new List<SymbolicCharacter>();
        var mnemonics = new List<MnemonicName>();
        var alphabets = new List<AlphabetDefinition>();

        while (!AtEnd && !IsDivisionHeader(_pos) && !IsEndProgram(_pos) && !IsSectionHeader(_pos)
            && !(Cur.Kind == TokenKind.Word && EnvironmentParagraphs.Contains(Text(_pos)) && IsPeriod(_pos + 1)))
        {
            if (AcceptPeriod())
            {
                continue;
            }

            if (IsWord(_pos, "DECIMAL-POINT"))
            {
                Next();
                AcceptWord("IS");
                if (AcceptWord("COMMA"))
                {
                    comma = true;
                    _decimalPointIsComma = true;
                    continue;
                }

                errors.Add(SkipError("invalid-decimal-point", "DECIMAL-POINT IS must be followed by COMMA", SpecialNamesBoundary));
                continue;
            }

            if (IsWord(_pos, "CURRENCY"))
            {
                Next();
                AcceptWord("SIGN");
                AcceptWord("IS");
                if (!AtEnd && Cur.IsLiteral)
                {
                    var literal = NameText(_pos);
                    Next();
                    string? symbol = null;
                    AcceptWord("WITH");
                    if (AcceptWord("PICTURE"))
                    {
                        AcceptWord("SYMBOL");
                        if (!AtEnd && Cur.IsLiteral)
                        {
                            symbol = NameText(_pos);
                            Next();
                        }
                    }

                    currencies.Add(new CurrencySign(literal, symbol));
                    var pictureSymbol = symbol ?? (literal.Length == 1 ? literal : null);
                    if (pictureSymbol is { Length: 1 })
                    {
                        _currencySymbols.Add(char.ToUpperInvariant(pictureSymbol[0]));
                    }

                    continue;
                }

                errors.Add(SkipError("invalid-currency-sign", "CURRENCY SIGN needs a literal", SpecialNamesBoundary));
                continue;
            }

            if (IsWord(_pos, "CLASS") && _pos + 1 < _ts.Count && _ts[_pos + 1].Kind == TokenKind.Word)
            {
                Next();
                var name = Text(_pos);
                Next();
                AcceptWord("IS");
                classes.Add(new ClassDefinition(name, ParseValueRanges()));
                continue;
            }

            if (IsWord(_pos, "SYMBOLIC"))
            {
                Next();
                AcceptWord("CHARACTERS");
                ParseSymbolicCharacters(symbolics);
                continue;
            }

            if (IsWord(_pos, "ALPHABET") && _pos + 1 < _ts.Count && _ts[_pos + 1].Kind == TokenKind.Word)
            {
                Next();
                var name = Text(_pos);
                Next();
                AcceptWord("IS");
                var specFirst = _pos;
                while (!AtEnd && !SpecialNamesBoundary(_pos) && !IsSpecialNamesClauseStart(_pos))
                {
                    Next();
                }

                alphabets.Add(new AlphabetDefinition(name, JoinTokens(specFirst, _pos)));
                continue;
            }

            if (Cur.Kind == TokenKind.Word && (IsWord(_pos + 1, "IS") || IsWord(_pos + 1, "ON") || IsWord(_pos + 1, "OFF")))
            {
                mnemonics.Add(ParseMnemonic());
                continue;
            }

            errors.Add(SkipError("unexpected-special-names-text", "Unexpected text in SPECIAL-NAMES", SpecialNamesBoundary));
        }

        var span = SpanFrom(first);
        return new SpecialNamesParagraph(span, RangeOf(span), comma, currencies, classes, symbolics, mnemonics, alphabets);
    }

    private bool SpecialNamesBoundary(int i) => EnvironmentBoundary(i) || IsSpecialNamesClauseStart(i);

    private bool IsSpecialNamesClauseStart(int i)
        => IsWord(i, "DECIMAL-POINT") || IsWord(i, "CURRENCY") || IsWord(i, "CLASS") || IsWord(i, "SYMBOLIC") || IsWord(i, "ALPHABET")
            || (_ts[i].Kind == TokenKind.Word && !IsWord(i, "SIGN") && !IsWord(i, "STATUS")
                && (IsWord(i + 1, "IS") || IsWord(i + 1, "ON") || IsWord(i + 1, "OFF"))
                && !(i > 0 && (IsWord(i - 1, "ALPHABET") || IsWord(i - 1, "CLASS"))));

    private MnemonicName ParseMnemonic()
    {
        var env = Text(_pos);
        Next();
        string? mnemonic = null;
        string? on = null;
        string? off = null;
        if (IsWord(_pos, "IS") && _pos + 1 < _ts.Count && _ts[_pos + 1].Kind == TokenKind.Word && !IsWord(_pos + 1, "ON") && !IsWord(_pos + 1, "OFF"))
        {
            Next();
            mnemonic = Text(_pos);
            Next();
        }

        while (IsWord(_pos, "ON") || IsWord(_pos, "OFF"))
        {
            var isOn = IsWord(_pos, "ON");
            Next();
            AcceptWord("STATUS");
            AcceptWord("IS");
            if (!AtEnd && Cur.Kind == TokenKind.Word)
            {
                if (isOn)
                {
                    on = Text(_pos);
                }
                else
                {
                    off = Text(_pos);
                }

                Next();
            }
        }

        return new MnemonicName(env, mnemonic, on, off);
    }

    private List<ValueRange> ParseValueRanges()
    {
        var values = new List<ValueRange>();
        while (!AtEnd && (Cur.IsLiteral || Cur.Kind == TokenKind.Figurative || Cur.Kind == TokenKind.Comma))
        {
            if (Cur.Kind == TokenKind.Comma)
            {
                Next();
                continue;
            }

            var from = Text(_pos);
            Next();
            string? thru = null;
            if ((AcceptWord("THRU") || AcceptWord("THROUGH")) && !AtEnd && (Cur.IsLiteral || Cur.Kind == TokenKind.Figurative))
            {
                thru = Text(_pos);
                Next();
            }

            values.Add(new ValueRange(from, thru));
        }

        return values;
    }

    private bool IsSymbolicGroup(int i)
    {
        while (i < _ts.Count && _ts[i].Kind == TokenKind.Word && !IsWord(i, "IS") && !IsWord(i, "ARE"))
        {
            i++;
        }

        return (IsWord(i, "IS") || IsWord(i, "ARE")) && i + 1 < _ts.Count && _ts[i + 1].Kind == TokenKind.IntegerLiteral;
    }

    private void ParseSymbolicCharacters(List<SymbolicCharacter> into)
    {
        // "NUL IS 1" looks like a mnemonic clause; an integer after IS/ARE marks it as a symbolic character.
        var first = true;
        while (!AtEnd && Cur.Kind == TokenKind.Word && (first || !SpecialNamesBoundary(_pos) || IsSymbolicGroup(_pos)) && !IsWord(_pos, "IN"))
        {
            first = false;
            var names = new List<string>();
            while (!AtEnd && Cur.Kind == TokenKind.Word && !IsWord(_pos, "IS") && !IsWord(_pos, "ARE") && !IsWord(_pos, "IN"))
            {
                names.Add(Text(_pos));
                Next();
            }

            if (!AcceptWord("IS"))
            {
                AcceptWord("ARE");
            }

            var ordinals = new List<int>();
            while (!AtEnd)
            {
                if (Cur.Kind == TokenKind.Comma)
                {
                    Next();
                    continue;
                }

                if (!TryInteger(out var n))
                {
                    break;
                }

                ordinals.Add(n);
            }

            for (var k = 0; k < names.Count; k++)
            {
                into.Add(new SymbolicCharacter(names[k], k < ordinals.Count ? ordinals[k] : 0, null));
            }
        }

        if (AcceptWord("IN") && !AtEnd && Cur.Kind == TokenKind.Word)
        {
            var alphabet = Text(_pos);
            Next();
            for (var k = 0; k < into.Count; k++)
            {
                if (into[k].Alphabet is null)
                {
                    into[k] = into[k] with { Alphabet = alphabet };
                }
            }
        }
    }

    private FileControlEntry ParseSelect(List<ErrorNode> errors)
    {
        var first = _pos;
        Next();
        var optional = AcceptWord("OPTIONAL");
        var fileName = string.Empty;
        if (!AtEnd && Cur.Kind == TokenKind.Word)
        {
            fileName = Text(_pos);
            Next();
        }
        else
        {
            AddDiagnostic("missing-file-name", DiagnosticSeverity.Error, "SELECT needs a file name", first, _pos, "SELECT");
        }

        var assign = new List<string>();
        string? organization = null;
        string? access = null;
        QualifiedName? recordKey = null;
        var alternates = new List<AlternateKey>();
        QualifiedName? relativeKey = null;
        QualifiedName? status = null;
        QualifiedName? vsamStatus = null;
        int? reserve = null;
        string? padding = null;
        string? delimiter = null;
        string? password = null;

        while (!AtEnd && !IsPeriod(_pos) && !EnvironmentBoundary(_pos))
        {
            if (AcceptWord("ASSIGN"))
            {
                AcceptWord("TO");
                AcceptWord("USING");
                while (!AtEnd && (Cur.Kind == TokenKind.Word || Cur.IsLiteral) && !(Cur.Kind == TokenKind.Word && SelectClauseWords.Contains(Text(_pos))))
                {
                    assign.Add(NameText(_pos));
                    Next();
                }

                continue;
            }

            if (IsWord(_pos, "ORGANIZATION") || IsWord(_pos, "SEQUENTIAL") || IsWord(_pos, "INDEXED") || IsWord(_pos, "RELATIVE") && !IsWord(_pos + 1, "KEY") || IsWord(_pos, "LINE"))
            {
                AcceptWord("ORGANIZATION");
                AcceptWord("IS");
                if (AcceptWord("LINE"))
                {
                    AcceptWord("SEQUENTIAL");
                    organization = "LINE SEQUENTIAL";
                    continue;
                }

                if (IsWord(_pos, "SEQUENTIAL") || IsWord(_pos, "INDEXED") || IsWord(_pos, "RELATIVE"))
                {
                    organization = Text(_pos).ToUpperInvariant();
                    Next();
                    continue;
                }

                errors.Add(SkipError("invalid-organization", "ORGANIZATION needs SEQUENTIAL, INDEXED, RELATIVE or LINE SEQUENTIAL", SelectClauseBoundary));
                continue;
            }

            if (AcceptWord("ACCESS"))
            {
                AcceptWord("MODE");
                AcceptWord("IS");
                if (IsWord(_pos, "SEQUENTIAL") || IsWord(_pos, "RANDOM") || IsWord(_pos, "DYNAMIC"))
                {
                    access = Text(_pos).ToUpperInvariant();
                    Next();
                    continue;
                }

                errors.Add(SkipError("invalid-access-mode", "ACCESS MODE needs SEQUENTIAL, RANDOM or DYNAMIC", SelectClauseBoundary));
                continue;
            }

            if (IsWord(_pos, "RECORD") && IsWord(_pos + 1, "DELIMITER"))
            {
                Next();
                Next();
                AcceptWord("IS");
                if (!AtEnd && Cur.Kind == TokenKind.Word)
                {
                    delimiter = Text(_pos);
                    Next();
                }

                continue;
            }

            if (AcceptWord("RECORD"))
            {
                AcceptWord("KEY");
                AcceptWord("IS");
                if (TryKeyName(out var key))
                {
                    recordKey = key;
                    if (AcceptWord("WITH") || IsWord(_pos, "DUPLICATES"))
                    {
                        AcceptWord("DUPLICATES");
                    }

                    continue;
                }

                errors.Add(SkipError("missing-record-key", "RECORD KEY needs a data name", SelectClauseBoundary));
                continue;
            }

            if (AcceptWord("ALTERNATE"))
            {
                AcceptWord("RECORD");
                AcceptWord("KEY");
                AcceptWord("IS");
                if (TryKeyName(out var key))
                {
                    var dup = false;
                    if (IsWord(_pos, "WITH") && IsWord(_pos + 1, "DUPLICATES"))
                    {
                        Next();
                    }

                    if (AcceptWord("DUPLICATES"))
                    {
                        dup = true;
                    }

                    alternates.Add(new AlternateKey(key, dup));
                    continue;
                }

                errors.Add(SkipError("missing-alternate-key", "ALTERNATE RECORD KEY needs a data name", SelectClauseBoundary));
                continue;
            }

            if (AcceptWord("RELATIVE"))
            {
                AcceptWord("KEY");
                AcceptWord("IS");
                if (TryKeyName(out var key))
                {
                    relativeKey = key;
                    continue;
                }

                errors.Add(SkipError("missing-relative-key", "RELATIVE KEY needs a data name", SelectClauseBoundary));
                continue;
            }

            if (IsWord(_pos, "FILE") || IsWord(_pos, "STATUS"))
            {
                AcceptWord("FILE");
                if (AcceptWord("STATUS"))
                {
                    AcceptWord("IS");
                    if (TryKeyName(out var key))
                    {
                        status = key;
                        if (TryKeyName(out var vsam))
                        {
                            vsamStatus = vsam;
                        }

                        continue;
                    }
                }

                errors.Add(SkipError("invalid-file-status", "FILE STATUS needs a data name", SelectClauseBoundary));
                continue;
            }

            if (AcceptWord("RESERVE"))
            {
                if (TryInteger(out var n))
                {
                    reserve = n;
                }

                if (!AcceptWord("AREA"))
                {
                    AcceptWord("AREAS");
                }

                continue;
            }

            if (AcceptWord("PADDING"))
            {
                AcceptWord("CHARACTER");
                AcceptWord("IS");
                if (!AtEnd && (Cur.Kind == TokenKind.Word || Cur.IsLiteral))
                {
                    padding = Text(_pos);
                    Next();
                }

                continue;
            }

            if (AcceptWord("PASSWORD"))
            {
                AcceptWord("IS");
                if (!AtEnd && Cur.Kind == TokenKind.Word)
                {
                    password = Text(_pos);
                    Next();
                }

                continue;
            }

            errors.Add(SkipError("unexpected-select-clause", "Unexpected clause in SELECT entry", SelectClauseBoundary));
        }

        if (assign.Count == 0)
        {
            AddDiagnostic("missing-assign", DiagnosticSeverity.Warning, $"SELECT {fileName} has no ASSIGN clause", first, first + 2, "SELECT");
        }

        AcceptPeriod();
        var span = SpanFrom(first);
        return new FileControlEntry(span, RangeOf(span), fileName)
        {
            Optional = optional,
            Assign = assign,
            Organization = organization,
            AccessMode = access,
            RecordKey = recordKey,
            AlternateKeys = alternates,
            RelativeKey = relativeKey,
            FileStatus = status,
            VsamStatus = vsamStatus,
            ReserveAreas = reserve,
            Padding = padding,
            RecordDelimiter = delimiter,
            Password = password,
        };
    }

    private bool SelectClauseBoundary(int i)
        => EnvironmentBoundary(i) || (_ts[i].Kind == TokenKind.Word && SelectClauseWords.Contains(Text(i)) && !IsWord(i, "SEQUENTIAL"))
            || IsWord(i, "ASSIGN");

    private bool TryKeyName(out QualifiedName name)
    {
        if (!AtEnd && Cur.Kind == TokenKind.Word && !SelectClauseWords.Contains(Text(_pos)) && !IsWord(_pos, "WITH") && !IsWord(_pos, "DUPLICATES"))
        {
            name = ParseQualifiedName();
            return true;
        }

        name = QualifiedName.Simple(string.Empty);
        return false;
    }

    private IoControlEntry ParseIoControlEntry()
    {
        var first = _pos;
        var kind = Text(_pos).ToUpperInvariant();
        Next();
        var operands = new List<string>();
        while (!AtEnd && !IsPeriod(_pos) && !EnvironmentBoundary(_pos) && !(Cur.Kind == TokenKind.Word && IoControlKinds.Contains(Text(_pos))))
        {
            if (Cur.Kind != TokenKind.Comma)
            {
                operands.Add(Text(_pos));
            }

            Next();
        }

        var span = SpanFrom(first);
        return new IoControlEntry(span, RangeOf(span), kind, operands);
    }

    private string JoinTokens(int from, int to)
    {
        var parts = new List<string>();
        for (var i = from; i < to; i++)
        {
            parts.Add(Text(i));
        }

        return string.Join(" ", parts);
    }
}
