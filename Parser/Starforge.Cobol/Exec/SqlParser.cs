namespace Starforge.Cobol.Exec;

// Shallow DB2 for z/OS statement reader: classifies the statement, then a clause-aware scan over parenthesis scopes
// finds table references. FROM/JOIN only count inside query scopes, so EXTRACT(YEAR FROM x) is never a table.
internal static class SqlParser
{
    // Words that end a table reference instead of naming its correlation.
    private static readonly HashSet<string> NotAlias = new(StringComparer.Ordinal)
    {
        "WHERE", "GROUP", "HAVING", "ORDER", "FETCH", "UNION", "EXCEPT", "INTERSECT", "FOR", "WITH", "OPTIMIZE", "QUERYNO",
        "SKIP", "SET", "WHEN", "ON", "USING", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "JOIN", "NATURAL", "AS",
        "SELECT", "VALUES", "INTO", "FROM", "AND", "OR", "NOT", "LIMIT", "OFFSET", "WITHOUT", "ISOLATION", "READ", "IN",
        "OVERRIDING", "INCLUDE", "PERIOD", "TABLESAMPLE", "THEN", "ELSE", "END", "LATERAL", "TABLE", "CORRELATION",
    };

    // Words after which a FROM list is over.
    private static readonly HashSet<string> ClauseEnd = new(StringComparer.Ordinal)
    {
        "WHERE", "GROUP", "HAVING", "ORDER", "FETCH", "UNION", "EXCEPT", "INTERSECT", "FOR", "WITH", "OPTIMIZE", "QUERYNO",
        "SKIP", "SET", "WHEN", "SELECT", "VALUES", "INTO", "LIMIT", "OFFSET", "WITHOUT", "ISOLATION", "INCLUDE", "PERIOD",
    };

    private static readonly HashSet<string> JoinWords = new(StringComparer.Ordinal) { "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "NATURAL", "EXCEPTION" };

    // Table functions and data-change table references whose parenthesised body stands in for a table.
    private static readonly HashSet<string> TableFunctionWords = new(StringComparer.Ordinal) { "TABLE", "LATERAL", "UNNEST", "XMLTABLE", "FINAL", "NEW", "OLD" };

    private static readonly HashSet<string> FetchWords = new(StringComparer.Ordinal)
    {
        "INSENSITIVE", "SENSITIVE", "WITH", "CONTINUE", "NEXT", "PRIOR", "FIRST", "LAST", "CURRENT", "BEFORE", "AFTER",
        "ABSOLUTE", "RELATIVE", "ROWSET", "STARTING", "AT", "FROM",
    };

    private static readonly HashSet<string> CursorAttributeWords = new(StringComparer.Ordinal)
    {
        "NO", "SCROLL", "ASENSITIVE", "INSENSITIVE", "SENSITIVE", "STATIC", "DYNAMIC", "WITH", "WITHOUT", "HOLD", "RETURN",
        "TO", "CALLER", "CLIENT", "ROWSET", "POSITIONING",
    };

    // Groups cursor attribute words into DB2 phrases: "WITH HOLD", "WITH RETURN TO CALLER", "INSENSITIVE SCROLL", "NO SCROLL".
    private static IEnumerable<string> AttributePhrases(IEnumerable<string> words)
    {
        var phrase = new List<string>();
        foreach (var w in words)
        {
            var joinsScroll = w == "SCROLL" && phrase.Count > 0 && phrase[^1] is "NO" or "STATIC" or "DYNAMIC" or "INSENSITIVE" or "ASENSITIVE" or "SENSITIVE";
            var starts = w is "WITH" or "WITHOUT" or "NO" or "ASENSITIVE" or "INSENSITIVE" or "SENSITIVE" || (w == "SCROLL" && !joinsScroll);
            if (starts && phrase.Count > 0)
            {
                yield return string.Join(' ', phrase);
                phrase.Clear();
            }

            phrase.Add(w);
        }

        if (phrase.Count > 0)
        {
            yield return string.Join(' ', phrase);
        }
    }

    private enum FromState
    {
        None,
        Expect,
        ExpectFunctionBody,
        AfterTable,
        InFrom,
    }

    private sealed class Scope
    {
        public bool Query;
        public FromState State;
        public bool IsTableReference;
        public int TableIndex = -1;
    }

    private sealed class Reader
    {
        public required ExecBlockContext Ctx;
        public required List<SToken> T;
        public readonly List<SqlTableReference> Tables = new();
        public readonly List<(int First, int End)> TableTokens = new();
        public readonly List<string> Ctes = new();
        public string? Problem;
        public int ProblemAt = -1;
    }

    public static SqlStatement Parse(ExecBlockContext ctx)
    {
        var comments = new List<ExecSpan>();
        var t = SqlTokenizer.Tokenize(ctx.Text, ctx.InnerStart, ctx.InnerEnd, comments, out var unterminated);
        if (unterminated >= 0)
        {
            return Fail(ctx, comments, t, "sql-unterminated-literal", "String, identifier or comment inside EXEC SQL is not terminated", unterminated, ctx.InnerEnd, "LITERAL");
        }

        if (t.Count == 0)
        {
            return Fail(ctx, comments, t, "sql-empty-statement", "EXEC SQL contains no statement", ctx.InnerStart, ctx.InnerEnd, string.Empty);
        }

        var depth = 0;
        foreach (var tok in t)
        {
            depth += tok.Kind == SKind.LeftParen ? 1 : tok.Kind == SKind.RightParen ? -1 : 0;
            if (depth < 0)
            {
                break;
            }
        }

        if (depth != 0)
        {
            return Fail(ctx, comments, t, "sql-unbalanced-parentheses", "Unbalanced parentheses in EXEC SQL", t[0].Start, t[^1].End, Leading(t));
        }

        var kind = Classify(t);
        if (kind == SqlStatementKind.Unknown)
        {
            var shape = t[0].Kind == SKind.Word ? t[0].Text : DiagnosticShape.Normalize(ctx.Text[t[0].Start..t[0].End]);
            return Fail(ctx, comments, t, "sql-unknown-statement", $"EXEC SQL statement {shape} is not recognised", t[0].Start, t[^1].End, shape);
        }

        var r = new Reader { Ctx = ctx, T = t };
        string? cursor = null;
        string? statementName = null;
        string? target = null;
        var whereCurrentOf = false;
        var attributes = new List<string>();
        var scanFrom = -1;

        switch (kind)
        {
            case SqlStatementKind.Select:
            case SqlStatementKind.SelectInto:
            case SqlStatementKind.Values:
            case SqlStatementKind.ValuesInto:
            case SqlStatementKind.Set:
                scanFrom = 0;
                target = kind == SqlStatementKind.Set ? SetTarget(t) : null;
                break;
            case SqlStatementKind.DeclareCursor:
            {
                cursor = Name(t, 1, out var at);
                var forAt = IndexOfWord(t, "FOR", at);
                attributes.AddRange(AttributePhrases(Words(t, at + 1, forAt < 0 ? t.Count : forAt).Where(CursorAttributeWords.Contains)));
                if (forAt < 0 || forAt + 1 >= t.Count)
                {
                    r.Problem ??= "DECLARE CURSOR without FOR";
                }
                else if (t[forAt + 1].Is("SELECT") || t[forAt + 1].Is("WITH") || t[forAt + 1].Is("VALUES") || t[forAt + 1].Kind == SKind.LeftParen)
                {
                    scanFrom = forAt + 1;
                }
                else
                {
                    statementName = Name(t, forAt + 1, out _);
                }

                break;
            }

            case SqlStatementKind.DeclareTable:
                Target(r, 1, SqlTableOperation.Declare);
                break;
            case SqlStatementKind.DeclareGlobalTemporaryTable:
                Target(r, 4, SqlTableOperation.Declare);
                var likeAt = IndexOfWord(t, "LIKE", 5);
                if (likeAt > 0)
                {
                    Target(r, likeAt + 1, SqlTableOperation.Select);
                }

                var asAt = IndexOfWord(t, "AS", 5);
                if (asAt > 0 && asAt + 1 < t.Count && t[asAt + 1].Kind == SKind.LeftParen)
                {
                    scanFrom = asAt + 1;
                }

                break;
            case SqlStatementKind.DeclareStatement:
                statementName = Name(t, 1, out _);
                break;
            case SqlStatementKind.Open:
            case SqlStatementKind.Close:
                cursor = Name(t, 1, out _);
                break;
            case SqlStatementKind.Fetch:
                cursor = FetchCursor(t);
                break;
            case SqlStatementKind.AllocateCursor:
                cursor = Name(t, 1, out _);
                break;
            case SqlStatementKind.Insert:
            {
                var at = t.Count > 1 && t[1].Is("INTO") ? 2 : 1;
                scanFrom = Target(r, at, SqlTableOperation.Insert);
                break;
            }

            case SqlStatementKind.Update:
            {
                var next = Target(r, 1, SqlTableOperation.Update);
                scanFrom = Alias(r, next);
                (whereCurrentOf, cursor) = CurrentOf(t);
                break;
            }

            case SqlStatementKind.Delete:
            {
                var at = t.Count > 1 && t[1].Is("FROM") ? 2 : 1;
                var next = Target(r, at, SqlTableOperation.Delete);
                scanFrom = Alias(r, next);
                (whereCurrentOf, cursor) = CurrentOf(t);
                break;
            }

            case SqlStatementKind.Merge:
            {
                var at = t.Count > 1 && t[1].Is("INTO") ? 2 : 1;
                var next = Alias(r, Target(r, at, SqlTableOperation.Merge));
                if (next < t.Count && t[next].Is("USING"))
                {
                    scanFrom = next;
                }
                else
                {
                    r.Problem ??= "MERGE without USING";
                }

                break;
            }

            case SqlStatementKind.Truncate:
                Target(r, t.Count > 1 && t[1].Is("TABLE") ? 2 : 1, SqlTableOperation.Truncate);
                break;
            case SqlStatementKind.LockTable:
                Target(r, 2, SqlTableOperation.Lock);
                break;
            case SqlStatementKind.Create:
            case SqlStatementKind.Drop:
            case SqlStatementKind.Alter:
            {
                var objAt = 1;
                while (objAt < t.Count && (t[objAt].Is("GLOBAL") || t[objAt].Is("TEMPORARY") || t[objAt].Is("AUXILIARY") || t[objAt].Is("OR") || t[objAt].Is("REPLACE")))
                {
                    objAt++;
                }

                if (objAt < t.Count && (t[objAt].Is("TABLE") || t[objAt].Is("VIEW")))
                {
                    Target(r, objAt + 1, SqlTableOperation.Ddl);
                    var viewAs = kind == SqlStatementKind.Create ? IndexOfWord(t, "AS", objAt + 2) : -1;
                    scanFrom = viewAs > 0 ? viewAs + 1 : -1;
                }

                break;
            }

            case SqlStatementKind.Prepare:
                statementName = Name(t, 1, out _);
                break;
            case SqlStatementKind.Execute:
                statementName = Name(t, 1, out _);
                break;
            case SqlStatementKind.Describe:
            {
                var at = 1;
                if (at < t.Count && (t[at].Is("OUTPUT") || t[at].Is("INPUT")))
                {
                    at++;
                }

                if (at < t.Count && t[at].Is("CURSOR"))
                {
                    cursor = Name(t, at + 1, out _);
                }
                else if (at < t.Count && (t[at].Is("TABLE") || t[at].Is("PROCEDURE")))
                {
                    target = Name(t, at + 1, out _);
                }
                else
                {
                    statementName = Name(t, at, out _);
                }

                break;
            }

            case SqlStatementKind.Call:
            case SqlStatementKind.Include:
            case SqlStatementKind.Savepoint:
                target = Name(t, 1, out _);
                break;
            case SqlStatementKind.ReleaseSavepoint:
            {
                var at = t.Count > 1 && t[1].Is("TO") ? 3 : 2;
                target = Name(t, at, out _);
                break;
            }

            case SqlStatementKind.Rollback:
            {
                var sp = IndexOfWord(t, "SAVEPOINT", 1);
                target = sp > 0 && sp + 1 < t.Count ? Name(t, sp + 1, out _) : null;
                break;
            }

            case SqlStatementKind.Connect:
            {
                var to = IndexOfWord(t, "TO", 1);
                target = to > 0 && to + 1 < t.Count ? Name(t, to + 1, out _) : t.Count > 1 && t[1].Is("RESET") ? "RESET" : null;
                break;
            }

            case SqlStatementKind.Release:
                target = t.Count > 1 ? Name(t, 1, out _) : null;
                break;
            case SqlStatementKind.Whenever:
                target = Whenever(ctx, t, r);
                break;
        }

        if (scanFrom >= 0)
        {
            ScanTables(r, scanFrom, kind == SqlStatementKind.Merge);
        }

        if (kind is SqlStatementKind.Open or SqlStatementKind.Close or SqlStatementKind.Fetch or SqlStatementKind.DeclareCursor or SqlStatementKind.AllocateCursor
            && cursor is null)
        {
            r.Problem ??= "cursor name missing";
        }

        if (kind is SqlStatementKind.Prepare or SqlStatementKind.Execute or SqlStatementKind.DeclareStatement && statementName is null)
        {
            r.Problem ??= "statement name missing";
        }

        var ctes = new HashSet<string>(r.Ctes, StringComparer.Ordinal);
        var kept = Enumerable.Range(0, r.Tables.Count).Where(i => r.Tables[i].Schema is not null || !ctes.Contains(r.Tables[i].Table)).ToList();
        var tables = kept.Select(i => r.Tables[i]).ToList();
        if (r.Problem is not null)
        {
            var at = r.ProblemAt >= 0 && r.ProblemAt < t.Count ? t[r.ProblemAt] : t[0];
            return Fail(ctx, comments, t, "sql-malformed-statement", $"EXEC SQL {Operation(kind)}: {r.Problem}", at.Start, Math.Max(at.End, t[^1].End),
                Operation(kind), kind);
        }

        return new SqlStatement
        {
            Kind = ctx.Kind,
            Status = ExecStatus.Parsed,
            Range = ctx.Range,
            ExpandedStart = ctx.ExpandedStart,
            ExpandedEnd = ctx.ExpandedEnd,
            Diagnostics = ctx.Diagnostics,
            Comments = comments,
            StatementKind = kind,
            Leading = Leading(t),
            Tables = tables,
            Tokens = t,
            TableTokens = kept.Select(i => r.TableTokens[i]).ToList(),
            CommonTableExpressions = r.Ctes,
            HostVariables = HostVariables(t, kind),
            Cursor = cursor,
            StatementName = statementName,
            WhereCurrentOf = whereCurrentOf,
            CursorAttributes = attributes,
            Target = target,
        };
    }

    private static SqlStatementKind Classify(List<SToken> t)
    {
        string W(int i) => i < t.Count && t[i].Kind == SKind.Word ? t[i].Text : string.Empty;
        switch (W(0))
        {
            case "SELECT":
                return HasTopLevelWord(t, "INTO", 1) ? SqlStatementKind.SelectInto : SqlStatementKind.Select;
            case "WITH":
                return HasTopLevelWord(t, "INTO", 1) ? SqlStatementKind.SelectInto : SqlStatementKind.Select;
            case "VALUES":
                return HasTopLevelWord(t, "INTO", 1) ? SqlStatementKind.ValuesInto : SqlStatementKind.Values;
            case "DECLARE":
                if (W(1) == "GLOBAL" && W(2) == "TEMPORARY" && W(3) == "TABLE")
                {
                    return SqlStatementKind.DeclareGlobalTemporaryTable;
                }

                Name(t, 1, out var after);
                var next = W(after + 1);
                if (t.Count > 1 && t[1].Kind == SKind.Host)
                {
                    return W(2) == "VARIABLE" ? SqlStatementKind.DeclareVariable : SqlStatementKind.Unknown;
                }

                if (next == "TABLE")
                {
                    return SqlStatementKind.DeclareTable;
                }

                if (IndexOfWord(t, "CURSOR", after + 1) > 0)
                {
                    return SqlStatementKind.DeclareCursor;
                }

                return IndexOfWord(t, "STATEMENT", after + 1) > 0 ? SqlStatementKind.DeclareStatement : SqlStatementKind.Unknown;
            case "OPEN":
                return SqlStatementKind.Open;
            case "FETCH":
                return SqlStatementKind.Fetch;
            case "CLOSE":
                return SqlStatementKind.Close;
            case "INSERT":
                return SqlStatementKind.Insert;
            case "UPDATE":
                return SqlStatementKind.Update;
            case "DELETE":
                return SqlStatementKind.Delete;
            case "MERGE":
                return SqlStatementKind.Merge;
            case "TRUNCATE":
                return SqlStatementKind.Truncate;
            case "COMMIT":
                return SqlStatementKind.Commit;
            case "ROLLBACK":
                return SqlStatementKind.Rollback;
            case "SAVEPOINT":
                return SqlStatementKind.Savepoint;
            case "RELEASE":
                return W(1) == "SAVEPOINT" || (W(1) == "TO" && W(2) == "SAVEPOINT") ? SqlStatementKind.ReleaseSavepoint : SqlStatementKind.Release;
            case "WHENEVER":
                return SqlStatementKind.Whenever;
            case "CALL":
                return SqlStatementKind.Call;
            case "SET":
                return SqlStatementKind.Set;
            case "PREPARE":
                return SqlStatementKind.Prepare;
            case "EXECUTE":
                return W(1) == "IMMEDIATE" ? SqlStatementKind.ExecuteImmediate : SqlStatementKind.Execute;
            case "DESCRIBE":
                return SqlStatementKind.Describe;
            case "LOCK":
                return W(1) == "TABLE" ? SqlStatementKind.LockTable : SqlStatementKind.Unknown;
            case "CONNECT":
                return SqlStatementKind.Connect;
            case "INCLUDE":
                return SqlStatementKind.Include;
            case "BEGIN":
                return W(1) == "DECLARE" && W(2) == "SECTION" ? SqlStatementKind.BeginDeclareSection : SqlStatementKind.Unknown;
            case "END":
                return W(1) == "DECLARE" && W(2) == "SECTION" ? SqlStatementKind.EndDeclareSection : SqlStatementKind.Unknown;
            case "GET":
                return W(1) == "DIAGNOSTICS" ? SqlStatementKind.GetDiagnostics : SqlStatementKind.Unknown;
            case "SIGNAL":
                return SqlStatementKind.Signal;
            case "ASSOCIATE":
                return SqlStatementKind.AssociateLocators;
            case "ALLOCATE":
                return IndexOfWord(t, "CURSOR", 1) > 0 ? SqlStatementKind.AllocateCursor : SqlStatementKind.Unknown;
            case "FREE":
                return W(1) == "LOCATOR" ? SqlStatementKind.FreeLocator : SqlStatementKind.Unknown;
            case "HOLD":
                return W(1) == "LOCATOR" ? SqlStatementKind.HoldLocator : SqlStatementKind.Unknown;
            case "CREATE":
                return SqlStatementKind.Create;
            case "DROP":
                return SqlStatementKind.Drop;
            case "ALTER":
                return SqlStatementKind.Alter;
            case "GRANT":
                return SqlStatementKind.Grant;
            case "REVOKE":
                return SqlStatementKind.Revoke;
            case "COMMENT":
                return SqlStatementKind.Comment;
            case "LABEL":
                return SqlStatementKind.Label;
            case "EXPLAIN":
                return SqlStatementKind.Explain;
            case "REFRESH":
                return SqlStatementKind.Refresh;
            default:
                return SqlStatementKind.Unknown;
        }
    }

    // Reads a table name at t[at] as the statement's target; returns the index after the name.
    private static int Target(Reader r, int at, SqlTableOperation op)
    {
        var name = TableName(r.T, at, out var next);
        if (name is null)
        {
            r.Problem ??= "table name expected";
            r.ProblemAt = r.ProblemAt < 0 ? at : r.ProblemAt;
            return at;
        }

        r.Tables.Add(name with { Operation = op });
        r.TableTokens.Add((at, next));
        return next;
    }

    // Optional [AS] correlation name after a target table; returns the index after it.
    private static int Alias(Reader r, int at)
    {
        var t = r.T;
        var i = at;
        if (i < t.Count && t[i].Is("AS"))
        {
            i++;
        }

        if (i < t.Count && t[i].IsName && !(t[i].Kind == SKind.Word && NotAlias.Contains(t[i].Text)) && r.Tables.Count > 0)
        {
            r.Tables[^1] = r.Tables[^1] with { Alias = t[i].Text };
            return i + 1;
        }

        return at;
    }

    private static SqlTableReference? TableName(List<SToken> t, int at, out int next)
    {
        next = at;
        if (at >= t.Count || !t[at].IsName || (t[at].Kind == SKind.Word && NotAlias.Contains(t[at].Text)))
        {
            return null;
        }

        var parts = new List<string> { t[at].Text };
        var i = at + 1;
        while (i + 1 < t.Count && t[i].Kind == SKind.Dot && t[i + 1].IsName && parts.Count < 3)
        {
            parts.Add(t[i + 1].Text);
            i += 2;
        }

        next = i;
        return new SqlTableReference(string.Join('.', parts), parts.Count > 1 ? parts[^2] : null, parts[^1], null, SqlTableOperation.Select);
    }

    private static void ScanTables(Reader r, int from, bool merge)
    {
        var t = r.T;
        var stack = new Stack<Scope>();
        var scope = new Scope { Query = true };
        for (var i = from; i < t.Count; i++)
        {
            var tok = t[i];
            if (tok.Kind == SKind.LeftParen)
            {
                var startsQuery = StartsQuery(t, i);
                var tableRef = scope.Query && scope.State is FromState.Expect or FromState.ExpectFunctionBody;

                // In table position a '(' without a fullselect is a parenthesised joined table: (A JOIN B ON ...).
                var joined = tableRef && scope.State == FromState.Expect && !startsQuery;
                stack.Push(scope);
                scope = new Scope
                {
                    Query = startsQuery || joined,
                    State = joined ? FromState.Expect : FromState.None,
                    IsTableReference = tableRef,
                };
                CteNames(r, i + 1);
                continue;
            }

            if (tok.Kind == SKind.RightParen)
            {
                var closed = scope;
                scope = stack.Count > 0 ? stack.Pop() : new Scope { Query = false };
                if (closed.IsTableReference)
                {
                    scope.State = FromState.AfterTable;
                    scope.TableIndex = -1;
                }

                continue;
            }

            if (!scope.Query)
            {
                continue;
            }

            if (i == from && tok.Is("WITH"))
            {
                CteNames(r, i);
            }

            if (tok.Kind == SKind.Comma)
            {
                if (scope.State is FromState.AfterTable or FromState.InFrom)
                {
                    scope.State = FromState.Expect;
                }

                continue;
            }

            if (tok.Kind != SKind.Word && tok.Kind != SKind.QuotedId)
            {
                if (scope.State is FromState.Expect or FromState.ExpectFunctionBody)
                {
                    r.Problem ??= "table reference expected after FROM/JOIN";
                    r.ProblemAt = r.ProblemAt < 0 ? i : r.ProblemAt;
                    scope.State = FromState.None;
                }
                else if (scope.State == FromState.AfterTable)
                {
                    scope.State = FromState.InFrom;
                }

                continue;
            }

            var w = tok.Kind == SKind.Word ? tok.Text : string.Empty;
            switch (scope.State)
            {
                case FromState.Expect:
                    if (TableFunctionWords.Contains(w))
                    {
                        scope.State = FromState.ExpectFunctionBody;
                        continue;
                    }

                    var name = TableName(t, i, out var next);
                    if (name is null)
                    {
                        r.Problem ??= $"table reference expected, found {w}";
                        r.ProblemAt = r.ProblemAt < 0 ? i : r.ProblemAt;
                        scope.State = FromState.None;
                        continue;
                    }

                    r.Tables.Add(name);
                    r.TableTokens.Add((i, next));
                    scope.TableIndex = r.Tables.Count - 1;
                    scope.State = FromState.AfterTable;
                    i = next - 1;
                    continue;
                case FromState.ExpectFunctionBody:
                    // TABLE/LATERAL keyword chains such as FINAL TABLE; the body arrives with the next '('.
                    continue;
                case FromState.AfterTable:
                    if (w == "AS" && i + 1 < t.Count && t[i + 1].IsName)
                    {
                        SetAlias(r, scope, t[i + 1].Text);
                        scope.State = FromState.InFrom;
                        i++;
                        continue;
                    }

                    if (tok.Kind == SKind.QuotedId || !NotAlias.Contains(w))
                    {
                        SetAlias(r, scope, tok.Text);
                        scope.State = FromState.InFrom;
                        continue;
                    }

                    break;
            }

            if (w == "FROM" || w == "JOIN")
            {
                scope.State = FromState.Expect;
            }
            else if (merge && w == "USING" && stack.Count == 0)
            {
                scope.State = FromState.Expect;
            }
            else if (JoinWords.Contains(w) || w == "ON" || w == "USING")
            {
                if (scope.State != FromState.None)
                {
                    scope.State = FromState.InFrom;
                }
            }
            else if (ClauseEnd.Contains(w))
            {
                scope.State = FromState.None;
            }
            else if (scope.State == FromState.AfterTable)
            {
                scope.State = FromState.InFrom;
            }
        }
    }

    private static void SetAlias(Reader r, Scope scope, string alias)
    {
        if (scope.TableIndex >= 0 && scope.TableIndex < r.Tables.Count)
        {
            r.Tables[scope.TableIndex] = r.Tables[scope.TableIndex] with { Alias = alias };
        }
    }

    // A '(' opens a query scope when a fullselect starts inside it, possibly behind more '('.
    private static bool StartsQuery(List<SToken> t, int open)
    {
        var i = open + 1;
        while (i < t.Count && t[i].Kind == SKind.LeftParen)
        {
            i++;
        }

        return i < t.Count && (t[i].Is("SELECT") || t[i].Is("WITH") || t[i].Is("VALUES"));
    }

    // WITH name [(columns)] AS ( ... ) [, name AS ( ... )]...
    private static void CteNames(Reader r, int at)
    {
        var t = r.T;
        if (at >= t.Count || !t[at].Is("WITH"))
        {
            return;
        }

        var i = at + 1;
        while (i < t.Count && t[i].IsName)
        {
            var name = t[i].Text;
            var j = i + 1;
            if (j < t.Count && t[j].Kind == SKind.LeftParen)
            {
                j = Match(t, j) + 1;
            }

            if (j + 1 >= t.Count || !t[j].Is("AS") || t[j + 1].Kind != SKind.LeftParen)
            {
                return;
            }

            r.Ctes.Add(name);
            var close = Match(t, j + 1);
            if (close < 0 || close + 1 >= t.Count || t[close + 1].Kind != SKind.Comma)
            {
                return;
            }

            i = close + 2;
        }
    }

    private static int Match(List<SToken> t, int open)
    {
        var depth = 0;
        for (var i = open; i < t.Count; i++)
        {
            depth += t[i].Kind == SKind.LeftParen ? 1 : t[i].Kind == SKind.RightParen ? -1 : 0;
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static string? FetchCursor(List<SToken> t)
    {
        var end = t.Count;
        for (var i = 1; i < t.Count; i++)
        {
            if (t[i].Is("INTO") || t[i].Is("USING"))
            {
                end = i;
                break;
            }
        }

        var fromAt = IndexOfWord(t, "FROM", 1);
        if (fromAt > 0 && fromAt < end)
        {
            return Name(t, fromAt + 1, out _);
        }

        for (var i = 1; i < end; i++)
        {
            if (t[i].Kind == SKind.Word && !FetchWords.Contains(t[i].Text) && !(t[i].Is("FOR") || t[i].Is("ROWS") || t[i].Is("ROW")))
            {
                // Orientation counts (ABSOLUTE 5, FOR 10 ROWS) are numbers or host variables, never words.
                return t[i].Text;
            }
        }

        return null;
    }

    private static (bool, string?) CurrentOf(List<SToken> t)
    {
        for (var i = 0; i + 3 < t.Count; i++)
        {
            if (t[i].Is("WHERE") && t[i + 1].Is("CURRENT") && t[i + 2].Is("OF"))
            {
                return (true, Name(t, i + 3, out _));
            }
        }

        return (false, null);
    }

    private static string? SetTarget(List<SToken> t)
    {
        if (t.Count < 2)
        {
            return null;
        }

        return t[1].Kind == SKind.Host ? null
            : t[1].Is("CURRENT") && t.Count > 2 && t[2].Kind == SKind.Word ? "CURRENT " + t[2].Text
            : t[1].Kind == SKind.Word ? t[1].Text : null;
    }

    private static string? Whenever(ExecBlockContext ctx, List<SToken> t, Reader r)
    {
        var i = 1;
        if (i < t.Count && t[i].Is("NOT") && i + 1 < t.Count && t[i + 1].Is("FOUND"))
        {
            i += 2;
        }
        else if (i < t.Count && (t[i].Is("SQLERROR") || t[i].Is("SQLWARNING")))
        {
            i++;
        }
        else
        {
            r.Problem ??= "WHENEVER condition expected";
            return null;
        }

        if (i < t.Count && t[i].Is("CONTINUE"))
        {
            return "CONTINUE";
        }

        if (i < t.Count && t[i].Is("GOTO"))
        {
            i++;
        }
        else if (i + 1 < t.Count && t[i].Is("GO") && t[i + 1].Is("TO"))
        {
            i += 2;
        }
        else
        {
            r.Problem ??= "WHENEVER action expected";
            return null;
        }

        if (i < t.Count && t[i].Kind == SKind.Host)
        {
            return t[i].Text;
        }

        if (i >= t.Count || t[i].Kind != SKind.Word)
        {
            r.Problem ??= "WHENEVER label expected";
            return null;
        }

        return ExecArguments.Collapse(ctx.Text, t[i].Start, t[^1].End).ToUpperInvariant();
    }

    private static IReadOnlyList<SqlHostVariable> HostVariables(List<SToken> t, SqlStatementKind kind)
    {
        var list = new List<SqlHostVariable>();
        var depth = 0;
        var output = false;
        var beforeEquals = kind is SqlStatementKind.Set or SqlStatementKind.GetDiagnostics;
        for (var i = 0; i < t.Count; i++)
        {
            var tok = t[i];
            depth += tok.Kind == SKind.LeftParen ? 1 : tok.Kind == SKind.RightParen ? -1 : 0;
            if (depth == 0 && tok.Kind == SKind.Word && kind is SqlStatementKind.SelectInto or SqlStatementKind.Fetch or SqlStatementKind.ValuesInto)
            {
                output = tok.Text == "INTO" || (output && !(tok.Text is "FROM" or "WHERE" or "GROUP" or "HAVING" or "ORDER" or "FETCH" or "WITH"
                    or "OPTIMIZE" or "QUERYNO" or "SKIP" or "FOR" or "UNION" or "EXCEPT" or "INTERSECT" or "USING" or "LIMIT" or "OFFSET"));
            }

            if (beforeEquals && tok.Kind == SKind.Operator && tok.Text == "=" && depth == 0)
            {
                beforeEquals = false;
            }

            if (tok.Kind == SKind.Host)
            {
                var dot = tok.Text.LastIndexOf('.');
                var role = output || beforeEquals ? SqlHostVariableRole.Output : SqlHostVariableRole.Input;
                list.Add(new SqlHostVariable(tok.Text, dot < 0 ? null : tok.Text[..dot], dot < 0 ? tok.Text : tok.Text[(dot + 1)..], tok.Indicator, role));
            }
        }

        return list;
    }

    private static bool HasTopLevelWord(List<SToken> t, string word, int from)
    {
        var depth = 0;
        for (var i = from; i < t.Count; i++)
        {
            depth += t[i].Kind == SKind.LeftParen ? 1 : t[i].Kind == SKind.RightParen ? -1 : 0;
            if (depth == 0 && t[i].Is(word))
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOfWord(List<SToken> t, string word, int from)
    {
        for (var i = Math.Max(0, from); i < t.Count; i++)
        {
            if (t[i].Is(word))
            {
                return i;
            }
        }

        return -1;
    }

    private static IEnumerable<string> Words(List<SToken> t, int from, int to)
    {
        for (var i = Math.Max(0, from); i < to && i < t.Count; i++)
        {
            if (t[i].Kind == SKind.Word)
            {
                yield return t[i].Text;
            }
        }
    }

    // A cursor, statement, routine or member name: an identifier (possibly qualified) or a host variable.
    private static string? Name(List<SToken> t, int at, out int last)
    {
        last = at;
        if (at >= t.Count)
        {
            return null;
        }

        if (t[at].Kind == SKind.Host)
        {
            return ":" + t[at].Text;
        }

        if (t[at].Kind == SKind.String)
        {
            return ExecArguments.LiteralValue(t[at].Text);
        }

        if (!t[at].IsName)
        {
            return null;
        }

        var parts = new List<string> { t[at].Text };
        var i = at;
        while (i + 2 < t.Count && t[i + 1].Kind == SKind.Dot && t[i + 2].IsName)
        {
            parts.Add(t[i + 2].Text);
            i += 2;
        }

        last = i;
        return string.Join('.', parts);
    }

    private static string Leading(List<SToken> t)
        => string.Join(' ', t.Take(3).TakeWhile(x => x.Kind == SKind.Word).Select(x => x.Text));

    private static string Operation(SqlStatementKind kind) => SqlStatement.OperationOf(kind);

    private static SqlStatement Fail(ExecBlockContext ctx, List<ExecSpan> comments, List<SToken> t, string code, string message, int start, int end, string shape,
        SqlStatementKind kind = SqlStatementKind.Unknown)
    {
        ctx.Report(code, DiagnosticSeverity.Error, message, start, end, shape);
        return new SqlStatement
        {
            Kind = ctx.Kind,
            Status = ExecStatus.Unsupported,
            Range = ctx.Range,
            ExpandedStart = ctx.ExpandedStart,
            ExpandedEnd = ctx.ExpandedEnd,
            Diagnostics = ctx.Diagnostics,
            Comments = comments,
            StatementKind = kind,
            Leading = Leading(t),
            UnsupportedReason = code,
        };
    }
}
