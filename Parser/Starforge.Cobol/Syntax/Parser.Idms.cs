using Starforge.Cobol.Lexing;

namespace Starforge.Cobol.Syntax;

// IDMS DML statements for the Broadcom COBOL DML precompiler (docs: "COBOL Data Manipulation Language (DML) Statements").
// A DML statement ends at a period, a semicolon, or where the next statement begins; any statement may carry one
// "ON condition-name imperative" clause ("Autostatus Protocols"). Operands are kept as text: they name records, sets,
// areas and maps from the subschema, not COBOL data items.
internal sealed partial class Parser
{
    private bool _idmsOption;
    private bool _idms;

    // Reserved DML words that are not COBOL verbs ("CA IDMS Keywords"); each starts a statement on its own.
    private static readonly HashSet<string> IdmsVerbs = new(StringComparer.Ordinal)
    {
        "ABEND", "ATTACH", "BIND", "CHANGE", "CHECK", "COMMIT", "CONNECT", "DC", "DEQUEUE", "DISCONNECT", "ENQUEUE",
        "ERASE", "FIND", "FINISH", "GET", "INQUIRE", "KEEP", "LOAD", "MAP", "MODIFY", "OBTAIN", "POST", "PUT", "READY",
        "ROLLBACK", "SEND", "STORE", "TRANSFER", "WAIT",
    };

    // Statements whose names are not reserved; they start a statement only as the first word on a source line.
    private static readonly HashSet<string> IdmsLineVerbs = new(StringComparer.Ordinal) { "STARTPAGE", "ENDPAGE", "SNAP" };

    // Second words that are part of the statement name (GET QUEUE, MAP OUT, BIND RUN-UNIT, ...).
    private static readonly Dictionary<string, string[]> IdmsSubForms = new(StringComparer.Ordinal)
    {
        ["BIND"] = new[] { "RUN-UNIT", "MAP", "TASK", "PROCEDURE", "TRANSACTION" },
        ["CHANGE"] = new[] { "PRIORITY" },
        ["CHECK"] = new[] { "TERMINAL" },
        ["DC"] = new[] { "RETURN" },
        ["GET"] = new[] { "QUEUE", "SCRATCH", "STORAGE", "TIME" },
        ["INQUIRE"] = new[] { "MAP" },
        ["KEEP"] = new[] { "LONGTERM" },
        ["MAP"] = new[] { "IN", "OUT", "OUTIN" },
        ["MODIFY"] = new[] { "MAP" },
        ["PUT"] = new[] { "QUEUE", "SCRATCH" },
        ["SEND"] = new[] { "MESSAGE" },
        ["TRANSFER"] = new[] { "CONTROL" },
    };

    // Verb words that may appear inside a statement of the given form without starting a new one (from the syntax
    // diagrams: WRITE TERMINAL ... FREE STORAGE, GET STORAGE ... KEEP, DC RETURN ... CONTINUE, ...).
    private static readonly Dictionary<string, string[]> IdmsInnerVerbs = new(StringComparer.Ordinal)
    {
        ["ATTACH"] = new[] { "WAIT" },
        ["ENQUEUE"] = new[] { "WAIT" },
        ["LOAD"] = new[] { "WAIT" },
        ["WRITE JOURNAL"] = new[] { "WAIT" },
        ["CHECK TERMINAL"] = new[] { "GET", "RETURN" },
        ["DC RETURN"] = new[] { "CONTINUE" },
        ["DELETE SCRATCH"] = new[] { "RETURN" },
        ["END TRANSACTION"] = new[] { "WRITE" },
        ["ACCEPT TRANSACTION"] = new[] { "WRITE" },
        ["GET QUEUE"] = new[] { "DELETE", "WAIT", "KEEP", "RETURN" },
        ["GET SCRATCH"] = new[] { "DELETE", "WAIT", "KEEP", "RETURN" },
        ["GET STORAGE"] = new[] { "DELETE", "WAIT", "KEEP", "RETURN" },
        ["INQUIRE MAP"] = new[] { "MOVE" },
        ["KEEP LONGTERM"] = new[] { "RETURN", "RELEASE", "WAIT" },
        ["MAP IN"] = new[] { "WAIT", "RETURN", "ERASE" },
        ["MAP OUT"] = new[] { "WAIT", "RETURN", "ERASE" },
        ["MAP OUTIN"] = new[] { "WAIT", "RETURN", "ERASE" },
        ["MODIFY MAP"] = new[] { "ERASE", "SUPPRESS", "DISPLAY" },
        ["PUT QUEUE"] = new[] { "RETURN" },
        ["PUT SCRATCH"] = new[] { "RETURN" },
        ["READ TERMINAL"] = new[] { "WAIT", "GET", "RETURN" },
        ["READ LINE"] = new[] { "WAIT", "GET", "RETURN" },
        ["ROLLBACK"] = new[] { "CONTINUE" },
        ["SET ABEND"] = new[] { "EXIT" },
        ["SET TIMER"] = new[] { "WAIT", "POST", "START", "CANCEL" },
        ["STARTPAGE"] = new[] { "WAIT", "RETURN" },
        ["WRITE LINE"] = new[] { "READ", "WAIT", "FREE", "ERASE", "KEEP", "GET", "RETURN" },
        ["WRITE PRINTER"] = new[] { "READ", "WAIT", "FREE", "ERASE", "KEEP", "GET", "RETURN" },
        ["WRITE TERMINAL"] = new[] { "READ", "WAIT", "FREE", "ERASE", "KEEP", "GET", "RETURN" },
        ["WRITE THEN"] = new[] { "READ", "WAIT", "FREE", "ERASE", "KEEP", "GET", "RETURN" },
        ["TRANSFER CONTROL"] = new[] { "RETURN" },
        ["TRANSFER"] = new[] { "RETURN" },
    };

    // The statement form starting at token i ("OBTAIN", "GET QUEUE", ...), or null when token i does not start a DML
    // statement. COBOL verbs shared with DML (ACCEPT, READ, WRITE, ...) are DML only with their DML-specific words.
    private string? IdmsForm(int i)
    {
        if (!IsWordToken(i))
        {
            return null;
        }

        var w = Up(i);
        var second = IsWordToken(i + 1) ? Up(i + 1) : null;
        if (IdmsVerbs.Contains(w))
        {
            return second is not null && IdmsSubForms.TryGetValue(w, out var subs) && subs.Contains(second) ? w + " " + second : w;
        }

        if (IdmsLineVerbs.Contains(w))
        {
            return FirstOnLine(i) ? w : null;
        }

        switch (w)
        {
            case "ACCEPT":
                if (second is "TASK" or "LTERM" or "PTERM" or "USER" or "SYSTEM" ? IsWord(i + 2, "ID") || IsWord(i + 2, "CODE") : second is "SYSVERSION" or "SCREENSIZE")
                {
                    return w + " " + second;
                }

                if (second == "TRANSACTION" && IsWord(i + 2, "STATISTICS"))
                {
                    return "ACCEPT TRANSACTION";
                }

                return IsAcceptFromDatabase(i) ? w : null;
            case "RETURN":
                // DML RETURN db-key FROM index-set ...; a sort-file RETURN has no FROM.
                return IsWordToken(i + 1) && IsWord(i + 2, "FROM") ? w : null;
            case "SET":
                return second == "ABEND" && IsWord(i + 2, "EXIT") ? "SET ABEND" : second == "TIMER" ? "SET TIMER" : null;
            case "READ":
                return second == "TERMINAL" ? "READ TERMINAL" : second == "LINE" && IsWord(i + 2, "FROM") && IsWord(i + 3, "TERMINAL") ? "READ LINE" : null;
            case "WRITE":
                return second switch
                {
                    "TERMINAL" or "PRINTER" or "LOG" or "JOURNAL" => w + " " + second,
                    "LINE" when IsWord(i + 2, "TO") && IsWord(i + 3, "TERMINAL") => "WRITE LINE",
                    "THEN" when IsWord(i + 2, "READ") => "WRITE THEN",
                    _ => null,
                };
            case "DELETE":
                return second is "QUEUE" or "SCRATCH" or "TABLE" ? w + " " + second : null;
            case "END":
                return (second == "LINE" && IsWord(i + 2, "TERMINAL")) || (second == "TRANSACTION" && IsWord(i + 2, "STATISTICS")) ? w + " " + second : null;
            case "FREE":
                return second == "STORAGE" ? "FREE STORAGE" : null;
            default:
                return null;
        }
    }

    // ACCEPT x FROM {set|record|area} CURRENCY, FROM set {NEXT|PRIOR|OWNER} CURRENCY, FROM record BIND,
    // FROM procedure PROCEDURE, FROM IDMS-STATISTICS, or ACCEPT x FOR record (page info).
    private bool IsAcceptFromDatabase(int i)
    {
        if (!IsWordToken(i + 1))
        {
            return false;
        }

        if (IsWord(i + 2, "FOR"))
        {
            return IsWordToken(i + 3);
        }

        if (!IsWord(i + 2, "FROM"))
        {
            return false;
        }

        return IsWord(i + 3, "CURRENCY") || IsWord(i + 3, "IDMS-STATISTICS")
            || IsWord(i + 4, "CURRENCY") || IsWord(i + 4, "BIND") || IsWord(i + 4, "PROCEDURE")
            || ((IsWord(i + 4, "NEXT") || IsWord(i + 4, "PRIOR") || IsWord(i + 4, "OWNER")) && IsWord(i + 5, "CURRENCY"));
    }

    private bool FirstOnLine(int i)
    {
        if (i == 0)
        {
            return true;
        }

        var here = _ts.Origin(i).Range;
        var before = _ts.Origin(i - 1).Range;
        return here.File != before.File || here.StartLine != before.StartLine;
    }

    private Statement ParseIdms(string form)
    {
        var first = _pos;
        var words = form.Split(' ').Length;
        for (var k = 0; k < words; k++)
        {
            Next();
        }

        IdmsKind kind = form switch
        {
            "BIND RUN-UNIT" => IdmsKind.BindRunUnit,
            "FINISH" => IdmsKind.Finish,
            "TRANSFER" or "TRANSFER CONTROL" => IdmsKind.Transfer,
            "DC RETURN" => IdmsKind.DcReturn,
            _ => IdmsKind.Generic,
        };

        IdmsTransfer? transfer = null;
        if (kind == IdmsKind.Transfer)
        {
            AcceptWord("TO");
            if (AtEnd || !(Cur.Kind == TokenKind.Word || Cur.Kind == TokenKind.AlphanumericLiteral))
            {
                throw Expected("program name after TRANSFER CONTROL");
            }

            var literal = Cur.Kind == TokenKind.AlphanumericLiteral;
            var target = NameText(_pos);
            Next();
            var mode = "NORETURN";
            if (IsWord(_pos, "RETURN") || IsWord(_pos, "LINK"))
            {
                mode = "RETURN";
                Next();
            }
            else if (IsWord(_pos, "NORETURN") || IsWord(_pos, "XCTL"))
            {
                Next();
            }

            transfer = new IdmsTransfer(target, literal, mode);
        }

        var start = _pos;
        IdmsInnerVerbs.TryGetValue(form, out var inner);
        var operands = new List<string>();
        IdmsTransfer? nextTask = null;
        while (!IdmsStop(_pos, form, start, inner) && !IsIdmsOnClause(_pos))
        {
            if (kind == IdmsKind.DcReturn && IsWord(_pos, "NEXT") && IsWord(_pos + 1, "TASK") && IsWord(_pos + 2, "CODE") && _pos + 3 < _ts.Count
                && _ts[_pos + 3].Kind is TokenKind.Word or TokenKind.AlphanumericLiteral)
            {
                nextTask = new IdmsTransfer(NameText(_pos + 3), _ts[_pos + 3].Kind == TokenKind.AlphanumericLiteral, "NEXT TASK");
                for (var k = 0; k < 4; k++)
                {
                    Next();
                }

                continue;
            }

            if (Cur.Kind == TokenKind.Word && !IsReserved(Up(_pos)))
            {
                operands.Add(Text(_pos));
            }

            Next();
        }

        var command = SpanFrom(first);
        IdmsOnClause? on = null;
        if (IsIdmsOnClause(_pos))
        {
            var onFirst = _pos;
            Next();
            var condition = NameText(_pos);
            Next();
            var statements = ParseStatementList();
            on = new IdmsOnClause(SpanFrom(onFirst), condition, statements);
        }

        if (IsSemicolon(_pos))
        {
            Next();
        }

        var span = SpanFrom(first);
        return new IdmsStatement(span, RangeOf(span), form, kind, command, transfer, nextTask, operands, on);
    }

    private bool IsSemicolon(int i) => i < _ts.Count && _ts[i].Kind == TokenKind.Semicolon;

    // "ON condition-name" followed by the imperative statement; SET ABEND EXIT ON PROGRAM 'x' does not match.
    private bool IsIdmsOnClause(int i)
        => IsWord(i, "ON") && i + 1 < _ts.Count && (_ts[i + 1].Kind == TokenKind.Word || _ts[i + 1].Kind == TokenKind.AlphanumericLiteral)
            && IsStatementStart(i + 2);

    private bool IdmsStop(int i, string form, int start, string[]? inner)
    {
        if (i >= _ts.Count || IsPeriod(i) || IsSemicolon(i) || BodyStop(i) || IsSectionStart(i) || (IsParagraphHeader(i) && InAreaA(i))
            || IsScopeTerminator(i) || IsWord(i, "ELSE") || IsWord(i, "WHEN") || _ts[i].Kind == TokenKind.ExecBlock)
        {
            return true;
        }

        if (!IsStatementStart(i))
        {
            return false;
        }

        var w = Up(i);
        if (inner is not null && inner.Contains(w))
        {
            return false;
        }

        // OBTAIN KEEP [EXCLUSIVE] ..., ACCEPT x FROM record BIND.
        return !((form is "FIND" or "OBTAIN" && w == "KEEP" && i == start)
            || (form == "ACCEPT" && w == "BIND" && IsWord(i - 2, "FROM")));
    }

    // DML IF conditions: "set-name [IS] [NOT] EMPTY" and "set-name MEMBER" (a leading NOT is parsed by ParseNot).
    private Condition? IdmsSetTest(int i)
    {
        if (!IsWordToken(i))
        {
            return null;
        }

        var j = i + 1;
        if (IsWord(j, "MEMBER"))
        {
            var first = _pos;
            var set = Text(_pos);
            Next();
            Next();
            return new IdmsSetCondition(SpanFrom(first), set, Empty: false, Not: false);
        }

        if (IsWord(j, "IS"))
        {
            j++;
        }

        var not = IsWord(j, "NOT");
        if (not)
        {
            j++;
        }

        if (!IsWord(j, "EMPTY"))
        {
            return null;
        }

        var begin = _pos;
        var name = Text(_pos);
        while (_pos <= j)
        {
            Next();
        }

        return new IdmsSetCondition(SpanFrom(begin), name, Empty: true, Not: not);
    }
}
