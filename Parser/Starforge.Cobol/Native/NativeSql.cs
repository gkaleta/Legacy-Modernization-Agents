using System.Text.Json.Nodes;
using Starforge.Cobol.Exec;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Native;

// The target-port profile fields an SQL relation qualification cites.
internal sealed record SqlProfile(string Path, string Sha256, string Name, string Kind, string? Schema);

// One DECLARE TABLE parsed from an explicit SQL catalog file (a member no program COPYs or INCLUDEs).
internal sealed record SqlCatalogDeclaration(string Name, JsonObject Node, string Path, string Sha256);

internal sealed class SqlCatalog
{
    private readonly List<SqlCatalogDeclaration> _declarations;

    private SqlCatalog(List<SqlCatalogDeclaration> declarations) => _declarations = declarations;

    public IReadOnlyList<SqlCatalogDeclaration> Declarations => _declarations;

    // Each file goes through the same front end as programs, so its declarations are Starforge parse trees
    // rooted in the catalog file; a declaration whose range cannot be bound is left out.
    public static SqlCatalog Load(CobolFrontEnd frontEnd, IEnumerable<string> paths)
    {
        var declarations = new List<SqlCatalogDeclaration>();
        foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            var result = frontEnd.Process(path);
            var roots = new SyntaxRoots(result);
            var sha = NativeSyntax.FileSha256(path);
            var tokens = result.Tokens;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != TokenKind.ExecBlock
                    || (result.ParsedExecs.TryGetValue(i, out var parsed) ? parsed : ExecParser.Parse(tokens, i)) is not SqlStatement
                    {
                        Status: ExecStatus.Parsed, StatementKind: SqlStatementKind.DeclareTable,
                    } sql)
                {
                    continue;
                }

                var problems = roots.Problems.Count;
                try
                {
                    var tree = NativeSql.Tree(roots, tokens.Text(i), tokens[i].Start, sql);
                    if (roots.Problems.Count == problems && tree.Declaration is { } node && tree.Declared is { } name)
                    {
                        declarations.Add(new SqlCatalogDeclaration(name, node, path, sha));
                    }
                }
                catch (NativeSql.Problem)
                {
                }
            }
        }

        return new SqlCatalog(declarations);
    }
}

// EXEC SQL blocks as native EXEC_SQL statements. syntax is the statement's flat token list; semanticSyntax
// structures the same tokens into the relation, cursor, host-variable and function names the SQL parser
// found, and wraps a DECLARE in its declaration node, which is also each binding's definitionSyntax.
internal sealed class NativeSql
{
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "DECLARE", "SELECT", "INSERT", "UPDATE", "DELETE", "OPEN", "FETCH", "CLOSE", "COMMIT", "ROLLBACK",
    };

    // Db2 for z/OS built-in functions (https://www.ibm.com/docs/en/db2-for-zos/13?topic=functions-built-in);
    // a call to anything else is a user-defined function this exporter cannot bind.
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "ABS", "AVG", "BIGINT", "CAST", "CEILING", "CHAR", "COALESCE", "CONCAT", "COUNT", "COUNT_BIG", "DATE", "DAY", "DAYS",
        "DEC", "DECIMAL", "DIGITS", "DOUBLE", "FLOAT", "FLOOR", "HEX", "HOUR", "IFNULL", "INT", "INTEGER", "LCASE", "LEFT",
        "LENGTH", "LOCATE", "LOWER", "LTRIM", "MAX", "MICROSECOND", "MIN", "MINUTE", "MOD", "MONTH", "NULLIF", "POSITION",
        "POWER", "REAL", "REPEAT", "REPLACE", "RIGHT", "ROUND", "RTRIM", "SECOND", "SIGN", "SMALLINT", "SPACE", "SQRT",
        "STRIP", "SUBSTR", "SUBSTRING", "SUM", "TIME", "TIMESTAMP", "TIMESTAMP_FORMAT", "TO_CHAR", "TO_DATE", "TRIM",
        "TRUNC", "TRUNCATE", "UCASE", "UPPER", "VALUE", "VARCHAR", "VARCHAR_FORMAT", "YEAR",
    };

    // Words a parenthesis may follow without forming a function call.
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "ALL", "AND", "ANY", "AS", "BETWEEN", "BY", "CASE", "ELSE", "EXISTS", "FOR", "FROM", "HAVING", "IN", "INTO", "IS",
        "LIKE", "NOT", "OF", "ON", "OR", "OVER", "SELECT", "SET", "SOME", "THEN", "UNION", "VALUES", "WHEN", "WHERE", "WITH",
    };

    // Db2 catalog relations with no estate data, which the gate does not ask to bind.
    private static readonly HashSet<string> PlatformRelations = new(StringComparer.Ordinal) { "SYSIBM.SYSDUMMY1" };

    private readonly NativeData _data;
    private readonly SqlCatalog _catalog;
    private readonly SqlProfile? _profile;

    public NativeSql(NativeData data, SqlCatalog catalog, SqlProfile? profile)
    {
        _data = data;
        _catalog = catalog;
        _profile = profile;
    }

    internal sealed class Problem(string message) : Exception(message);

    internal sealed record Built(JsonObject Syntax, JsonObject Semantic, JsonObject? Declaration, string? Declared, string Operation, string SourceText, int Start);

    // Every EXEC SQL block of the unit except INCLUDE, data division DECLAREs included.
    public List<JsonObject> Statements(ProgramUnit unit, List<string> diagnostics)
    {
        var tokens = _data.Roots.Tokens;
        var built = new List<(Built Tree, SqlStatement Sql)>();
        foreach (var (index, statement) in _data.ExecBlocks(unit))
        {
            if (statement.Kind is not (ExecKind.Sql or ExecKind.SqlIms))
            {
                continue;
            }

            if (statement is not SqlStatement { Status: ExecStatus.Parsed } sql)
            {
                diagnostics.Add("starforge-unsupported:sql:parse");
                continue;
            }

            if (sql.StatementKind == SqlStatementKind.Include)
            {
                continue;
            }

            try
            {
                built.Add((Tree(_data.Roots, tokens.Text(index), tokens[index].Start, sql), sql));
            }
            catch (Problem problem)
            {
                diagnostics.Add($"starforge-unsupported:sql:{sql.Leading.Split(' ')[0]}:{problem.Message}");
            }
        }

        var declarations = built.Where(b => b.Tree.Declaration is not null && b.Tree.Declared is not null)
            .GroupBy(b => (b.Sql.StatementKind, b.Tree.Declared!))
            .ToDictionary(g => g.Key, g => g.Select(b => b.Tree.Declaration!).ToList());
        var statements = new List<JsonObject>();
        foreach (var (tree, sql) in built)
        {
            try
            {
                statements.Add(Statement(tree, unit, declarations));
            }
            catch (Problem problem)
            {
                diagnostics.Add($"starforge-unsupported:sql:{tree.Operation}:{problem.Message}");
            }
        }

        return statements;
    }

    private JsonObject Statement(Built tree, ProgramUnit unit, Dictionary<(SqlStatementKind, string), List<JsonObject>> declarations)
    {
        var references = References(tree.Semantic, null);
        if (tree.Operation == "DECLARE" && !references.Any(r => (string?)r["role"] == "declaration"))
        {
            throw new Problem("declaration");
        }

        var bindings = new JsonArray();
        var seen = new HashSet<(string, string)>();
        foreach (var reference in references.Where(r => (string?)r["role"] == "reference"))
        {
            var kind = (string)reference["kind"]!;
            var name = (string)reference["name"]!;
            if (seen.Add((kind, name)))
            {
                bindings.Add(Binding(kind, name, unit, declarations));
            }
        }

        var (line, column) = _data.Roots.Position(tree.Start);
        return new JsonObject
        {
            ["kind"] = "EXEC_SQL",
            ["operation"] = tree.Operation,
            ["grammarRule"] = "SqlStatement",
            ["sourceText"] = tree.SourceText,
            ["coordinateSpace"] = "expanded-parser-input",
            ["line"] = line,
            ["column"] = column,
            ["bindingStatus"] = "complete",
            ["runtimeMapping"] = "unverified",
            ["syntax"] = tree.Syntax,
            ["semanticSyntax"] = tree.Semantic,
            ["semanticStatus"] = "complete",
            ["resourceReferences"] = new JsonArray(references.ToArray<JsonNode>()),
            ["resourceBindings"] = bindings,
        };
    }

    private JsonObject Binding(string kind, string name, ProgramUnit unit, Dictionary<(SqlStatementKind, string), List<JsonObject>> declarations)
    {
        var binding = new JsonObject { ["kind"] = kind, ["name"] = name, ["definitionScope"] = "expanded-parser-input" };
        switch (kind)
        {
            case "SQL_HOST_VARIABLE":
            {
                var symbols = _data.DataOf(unit)?.Symbols ?? throw new Problem("data");
                var item = symbols.Resolve(name, null).Item;
                if (item is null || item.Level is 66 or 88)
                {
                    throw new Problem("host-variable:" + name);
                }

                binding["definitionSyntax"] = _data.Entry(item) ?? throw new Problem("host-variable:" + name);
                return binding;
            }

            case "SQL_CURSOR":
                binding["definitionSyntax"] = Single(declarations, SqlStatementKind.DeclareCursor, name)?.DeepClone() ?? throw new Problem("cursor:" + name);
                return binding;
            case "SQL_RELATION":
                return Relation(binding, name, declarations);
            default:
                throw new Problem("reference:" + kind);
        }
    }

    // A relation binds to the program's own DECLARE TABLE, else to an explicit catalog declaration; either may
    // spell it schema-qualified when the schema is the profile's default SQL schema.
    private JsonObject Relation(JsonObject binding, string name, Dictionary<(SqlStatementKind, string), List<JsonObject>> declarations)
    {
        var qualified = _profile?.Schema is { Length: > 0 } schema && !name.Contains('.') ? schema + "." + name : null;
        if (Single(declarations, SqlStatementKind.DeclareTable, name) is { } own)
        {
            binding["definitionSyntax"] = own.DeepClone();
            return binding;
        }

        if (qualified is not null && Single(declarations, SqlStatementKind.DeclareTable, qualified) is { } ownQualified)
        {
            binding["definitionSyntax"] = ownQualified.DeepClone();
            binding["qualification"] = Qualification();
            return binding;
        }

        var exact = _catalog.Declarations.Where(d => d.Name == name).ToList();
        var matches = exact.Count > 0 || qualified is null ? exact : _catalog.Declarations.Where(d => d.Name == qualified).ToList();
        if (matches.Count != 1)
        {
            throw new Problem((matches.Count == 0 ? "relation:" : "relation-ambiguous:") + name);
        }

        binding["definitionScope"] = "explicit-sql-catalog";
        binding["definitionSyntax"] = matches[0].Node.DeepClone();
        binding["sourcePath"] = matches[0].Path;
        binding["sourceSha256"] = matches[0].Sha256;
        if (exact.Count == 0)
        {
            binding["qualification"] = Qualification();
        }

        return binding;
    }

    private JsonObject Qualification() => new()
    {
        ["profileKind"] = _profile!.Kind,
        ["profileName"] = _profile.Name,
        ["sourcePath"] = _profile.Path,
        ["sourceSha256"] = _profile.Sha256,
        ["schema"] = _profile.Schema,
    };

    private static JsonObject? Single(Dictionary<(SqlStatementKind, string), List<JsonObject>> declarations, SqlStatementKind kind, string name) =>
        declarations.TryGetValue((kind, name), out var found)
            ? found.Count == 1 ? found[0] : throw new Problem("declaration-ambiguous:" + name)
            : null;

    // The trees for one parsed SQL statement; block is the EXEC block text starting at expanded offset blockStart.
    internal static Built Tree(SyntaxRoots roots, string block, int blockStart, SqlStatement sql)
    {
        var t = sql.Tokens;
        if (t.Count == 0)
        {
            throw new Problem("tokens");
        }

        var operation = t[0].Kind == SKind.Word ? t[0].Text : string.Empty;
        if (!Operations.Contains(operation))
        {
            throw new Problem("operation");
        }

        string Text(SToken token) => block[token.Start..token.End];
        JsonNode Leaf(int i) => NativeSyntax.Token(Text(t[i]));
        var tables = new Dictionary<int, int>();
        for (var k = 0; k < sql.TableTokens.Count; k++)
        {
            if (!tables.TryAdd(sql.TableTokens[k].First, sql.TableTokens[k].End))
            {
                throw new Problem("table-span");
            }
        }

        var cursors = CursorTokens(sql, t);
        var declareTable = sql.StatementKind == SqlStatementKind.DeclareTable;
        string? declared = null;
        var body = new List<JsonNode>();
        for (var i = 0; i < t.Count;)
        {
            if (tables.TryGetValue(i, out var end))
            {
                var name = NativeSyntax.Node("SqlTableName", Enumerable.Range(i, end - i).Select(Leaf));
                if (declareTable && i == 1)
                {
                    declared = string.Concat(Enumerable.Range(i, end - i).Select(k => Text(t[k]))).ToUpperInvariant();
                }

                body.Add(name);
                i = end;
                continue;
            }

            if (cursors.Contains(i))
            {
                body.Add(NativeSyntax.Node("SqlCursorName", [Leaf(i)]));
                if (sql.StatementKind == SqlStatementKind.DeclareCursor && i == 1)
                {
                    declared = t[i].Text;
                }

                i++;
                continue;
            }

            if (t[i].Kind == SKind.Host)
            {
                if (t[i].Indicator is not null || (i + 1 < t.Count && t[i + 1].Kind == SKind.Dot) || Text(t[i]).Contains(' '))
                {
                    throw new Problem("host-variable:" + t[i].Text);
                }

                body.Add(NativeSyntax.Node("SqlHostVariable", [Leaf(i)]));
                i++;
                continue;
            }

            if (!declareTable && t[i].Kind == SKind.Word && i + 1 < t.Count && t[i + 1].Kind == SKind.LeftParen)
            {
                if (i > 0 && t[i - 1].Kind == SKind.Dot)
                {
                    throw new Problem("function:" + t[i].Text);
                }

                if (Builtins.Contains(t[i].Text))
                {
                    body.Add(NativeSyntax.Node("SqlBuiltinFunction", [Leaf(i)]));
                    i++;
                    continue;
                }

                if (!Keywords.Contains(t[i].Text))
                {
                    throw new Problem("function:" + t[i].Text);
                }
            }

            body.Add(Leaf(i));
            i++;
        }

        var start = blockStart + t[0].Start;
        var stop = blockStart + t[^1].End;
        var what = "sql:" + operation;
        JsonObject? declaration = null;
        List<JsonNode> semantic = body;
        if (sql.StatementKind is SqlStatementKind.DeclareTable or SqlStatementKind.DeclareCursor)
        {
            if (declared is null)
            {
                throw new Problem("declaration-name");
            }

            var kind = declareTable ? "SqlDeclareTable" : "SqlDeclareCursor";
            declaration = roots.Root(NativeSyntax.Node(kind, body), start, stop, what);
            semantic = [declaration];
        }

        return new Built(
            roots.Root(NativeSyntax.Node("SqlStatement", Enumerable.Range(0, t.Count).Select(Leaf)), start, stop, what),
            roots.Root(NativeSyntax.Node("SqlSyntax", semantic), start, stop, what),
            declaration is null ? null : (JsonObject)declaration.DeepClone(),
            declared,
            operation,
            block[t[0].Start..t[^1].End],
            start);
    }

    // The token indexes naming a cursor: after DECLARE, OPEN or CLOSE, the FETCH source and WHERE CURRENT OF.
    private static HashSet<int> CursorTokens(SqlStatement sql, IReadOnlyList<SToken> t)
    {
        var found = new HashSet<int>();
        bool Named(int i) => i < t.Count && t[i].IsName && !(i + 1 < t.Count && t[i + 1].Kind == SKind.Dot);
        switch (sql.StatementKind)
        {
            case SqlStatementKind.DeclareCursor or SqlStatementKind.Open or SqlStatementKind.Close:
                if (!Named(1) || t[1].Text != sql.Cursor)
                {
                    throw new Problem("cursor");
                }

                found.Add(1);
                break;
            case SqlStatementKind.Fetch:
            {
                var into = Enumerable.Range(1, t.Count - 1).FirstOrDefault(i => t[i].Is("INTO"), t.Count);
                var at = Enumerable.Range(1, into - 1).Where(i => Named(i) && t[i].Text == sql.Cursor).ToList();
                if (at.Count != 1)
                {
                    throw new Problem("cursor");
                }

                found.Add(at[0]);
                break;
            }
        }

        for (var i = 0; i + 2 < t.Count; i++)
        {
            if (t[i].Is("CURRENT") && t[i + 1].Is("OF"))
            {
                if (!Named(i + 2))
                {
                    throw new Problem("cursor");
                }

                found.Add(i + 2);
            }
        }

        return found;
    }

    // The gate's reference walk: a pre-order visit that stops at the first node naming a resource.
    internal static List<JsonObject> References(JsonObject node, string? parent)
    {
        var kind = (string?)node["node"];
        var resource = kind switch
        {
            "SqlTableName" or "SqlViewName" => "SQL_RELATION",
            "SqlCursorName" => "SQL_CURSOR",
            "SqlHostVariable" or "SqlIndicatorVariable" => "SQL_HOST_VARIABLE",
            "SqlFunctionName" => "SQL_FUNCTION",
            "SqlStatementName" => "SQL_STATEMENT",
            "SqlSequenceName" => "SQL_SEQUENCE",
            _ => null,
        };
        var joined = string.Concat(Leaves(node));
        if (resource == "SQL_RELATION" && parent != "SqlDeclareTable" && PlatformRelations.Contains(joined.ToUpperInvariant()))
        {
            resource = null;
        }

        if (resource is not null)
        {
            var name = joined.StartsWith(':') ? joined[1..] : joined;
            if (!name.Contains('"'))
            {
                name = name.ToUpperInvariant();
            }

            var declaration = parent == "SqlDeclareTable" || (resource == "SQL_CURSOR" && parent == "SqlDeclareCursor");
            return [new JsonObject { ["kind"] = resource, ["name"] = name, ["role"] = declaration ? "declaration" : "reference", ["grammarRule"] = kind }];
        }

        return (node["children"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(child => child.ContainsKey("node"))
            .SelectMany(child => References(child, kind))
            .ToList();
    }

    private static IEnumerable<string> Leaves(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            yield break;
        }

        if (obj["token"] is JsonValue token)
        {
            yield return token.GetValue<string>();
            yield break;
        }

        foreach (var child in obj["children"] as JsonArray ?? [])
        {
            foreach (var leaf in Leaves(child))
            {
                yield return leaf;
            }
        }
    }
}
