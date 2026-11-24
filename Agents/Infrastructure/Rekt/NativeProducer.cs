using System.Text.Json;
using System.Text.Json.Nodes;

namespace CobolToQuarkusMigration.Agents.Infrastructure.Rekt;

// The native parser that wrote a dependency export decides how its syntax trees are read. cobol-rekt trees
// carry ANTLR rule names; Starforge trees carry Starforge node kinds that the table below maps onto the same
// verifier roles, so both producers face the same checks. Code outside a Use(...) scope reads cobol-rekt.
public static class NativeProducer
{
    public const string Rekt = "rekt";
    public const string Starforge = "starforge";
    public const string Field = "dependencyProducer";

    private static readonly AsyncLocal<string?> Scope = new();

    public static string Current => Scope.Value ?? Rekt;

    public static bool IsStarforge => Current == Starforge;

    public static string EvidencePrefix => IsStarforge ? "starforge-tree:" : "antlr-tree:";

    // dependency-evidence.json names the producer that wrote it; the cobol-rekt value predates Starforge.
    public static string EvidenceProducer => IsStarforge ? "starforge/dependency" : "cobol-rekt/dependency";

    public static string? OfEvidence(string? producer) => producer switch
    {
        "cobol-rekt/dependency" => Rekt,
        "starforge/dependency" => Starforge,
        _ => null,
    };

    public static IDisposable Use(string producer)
    {
        var previous = Scope.Value;
        Scope.Value = producer;
        return new Restore(previous);
    }

    // Absent means cobol-rekt, which never wrote the field; any other value is unknown and yields null.
    public static string? Of(JsonNode? document) =>
        document is not JsonObject obj || !obj.TryGetPropertyValue(Field, out var value) ? Rekt
        : PyJson.Str(value) == Starforge ? Starforge : null;

    public static string? Of(JsonElement document) =>
        document.ValueKind != JsonValueKind.Object || !document.TryGetProperty(Field, out var value) ? Rekt
        : value.ValueKind == JsonValueKind.String && value.GetString() == Starforge ? Starforge : null;

    public static string? Declared(JsonNode? document) =>
        document is JsonObject obj && obj.TryGetPropertyValue(Field, out var value) ? PyJson.PyStr(value) : null;

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Scope.Value = previous;
    }

    private static readonly Dictionary<string, string> FixedRoles = new(StringComparer.Ordinal)
    {
        ["MoveStatement"] = "MoveStatementContext",
        ["InitializeStatement"] = "InitializeStatementContext",
        ["StringStatement"] = "StringStatementContext",
        ["PerformPhrase"] = "PerformTypeContext",
        ["CallStatement"] = "CallStatementContext",
        ["OpenStatement"] = "OpenStatementContext",
        ["CloseStatement"] = "CloseStatementContext",
        ["DataName"] = "EntryNameContext",
        ["PictureClause"] = "DataPictureClauseContext",
        ["OccursClause"] = "DataOccursClauseContext",
        ["FileControlEntry"] = "FileControlEntryContext",
        ["FileName"] = "FileNameContext",
        ["FileDescriptionEntry"] = "FileDescriptionEntryClausesContext",
        ["FileDescriptionName"] = "CobolWordContext",
        ["ParagraphName"] = "ParagraphDefinitionNameContext",
        ["SectionName"] = "SectionNameContext",
        ["DataReference"] = "VariableNameUsageContext",
        ["Identifier"] = "GeneralIdentifierContext",
        ["ProcedureReference"] = "ParagraphNameUsageContext",
        ["Condition"] = "ConditionContext",
        ["RelationCondition"] = "SimpleConditionContext",
        ["CombinedCondition"] = "AdditionalConditionContext",
        ["EvaluateSubject"] = "EvaluateSelectContext",
        ["EvaluateWhen"] = "EvaluateWhenContext",
        ["SqlStatement"] = "SqlCodeContext",
        ["SqlSyntax"] = "StartSqlRuleContext",
        ["SqlTableName"] = "Dbs_table_nameContext",
        ["SqlViewName"] = "Dbs_view_nameContext",
        ["SqlCursorName"] = "Dbs_cursor_nameContext",
        ["SqlHostVariable"] = "Dbs_host_var_identifierContext",
        ["SqlIndicatorVariable"] = "Dbs_host_indicator_identifierContext",
        ["SqlStatementName"] = "Dbs_statement_nameContext",
        ["SqlSequenceName"] = "Dbs_sequence_nameContext",
        ["SqlFunctionName"] = "Dbs_function_nameContext",
        ["SqlBuiltinFunction"] = "Dbs_inbuild_functionsContext",
        ["SqlDeclareTable"] = "Dbs_declare_tableContext",
        ["SqlDeclareCursor"] = "Dbs_declare_cursorContext",
        ["CicsArgument"] = "Cics_nameContext",
    };

    private static readonly Dictionary<string, string> FileVerbs = new(StringComparer.Ordinal)
    {
        ["READ"] = "ReadStatementContext", ["WRITE"] = "WriteStatementContext", ["REWRITE"] = "RewriteStatementContext",
        ["DELETE"] = "DeleteStatementContext", ["START"] = "StartStatementContext",
    };

    private static readonly Dictionary<string, string> CicsVerbs = new(StringComparer.Ordinal)
    {
        ["SEND"] = "Cics_sendContext", ["RECEIVE"] = "Cics_receiveContext", ["RETURN"] = "Cics_returnContext",
        ["XCTL"] = "Cics_xctlContext", ["LINK"] = "Cics_linkContext", ["READ"] = "Cics_readContext",
        ["READNEXT"] = "Cics_readnextContext", ["READPREV"] = "Cics_readnextContext", ["WRITE"] = "Cics_writeContext",
        ["REWRITE"] = "Cics_rewriteContext", ["DELETE"] = "Cics_deleteContext", ["STARTBR"] = "Cics_startbrContext",
        ["ENDBR"] = "Cics_endbrContext", ["UNLOCK"] = "Cics_unlockContext", ["ASSIGN"] = "Cics_assignContext",
        ["ABEND"] = "Cics_abendContext", ["SYNCPOINT"] = "Cics_syncpointContext", ["ASKTIME"] = "Cics_asktimeContext",
        ["FORMATTIME"] = "Cics_formattimeContext", ["HANDLE"] = "Cics_handleContext", ["ENQ"] = "Cics_enqContext",
        ["DEQ"] = "Cics_deqContext", ["DELAY"] = "Cics_delayContext", ["GET"] = "Cics_getContext",
        ["PUT"] = "Cics_putContext", ["FETCH"] = "Cics_fetchContext", ["RUN"] = "Cics_runContext",
        ["INQUIRE"] = "Cics_inquireContext", ["BIF"] = "Cics_bifContext", ["WRITEQ"] = "Cics_writeqContext",
    };

    // Verb form roles keyed by verb + a leading word or option the form is recognised by.
    private static readonly (string Verb, string Word, string Role)[] CicsForms =
    [
        ("SEND", "MAP", "Cics_send_map_minContext"), ("SEND", "TEXT", "Cics_send_text_stdContext"),
        ("SEND", "CONTROL", "Cics_send_control_minContext"), ("RECEIVE", "MAP", "Cics_receive_mapContext"),
        ("RECEIVE", "MAPPINGDEV", "Cics_receive_mappingdevContext"), ("RETURN", "INPUTMSG", "Cics_return_inputmsgContext"),
        ("INQUIRE", "PROGRAM", "Cics_inquire_programContext"), ("INQUIRE", "TERMINAL", "Cics_inquire_terminalContext"),
        ("INQUIRE", "ASSOCIATION", "Cics_inquire_associationContext"), ("BIF", "DEEDIT", "Cics_bif_deeditContext"),
        ("HANDLE", "CONDITION", "Cics_handle_conditionContext"), ("HANDLE", "ABEND", "Cics_handle_abendContext"),
        ("WRITEQ", "TD", "Cics_writeq_tdContext"), ("WRITE", "FILE", "Cics_write_fileContext"),
        ("WRITE", "DATASET", "Cics_write_fileContext"), ("DELETE", "FILE", "Cics_delete_fileContext"),
        ("DELETE", "DATASET", "Cics_delete_fileContext"), ("GET", "CHANNEL", "Cics_get_channelContext"),
        ("PUT", "CHANNEL", "Cics_put_channelContext"), ("RUN", "TRANSID", "Cics_run_transidContext"),
        ("LINK", "PROGRAM", "Cics_link_programContext"),
    ];

    // Roles of one Starforge node from its kind, leading tokens and option names; unmapped kinds have none.
    public static IReadOnlyList<string> Roles(string kind, IReadOnlyList<string> leading, IReadOnlyList<string> options)
    {
        if (FixedRoles.TryGetValue(kind, out var role)) return [role];
        var first = leading.Count > 0 ? leading[0] : null;
        switch (kind)
        {
            case "DataEntry":
                return first switch
                {
                    "77" => ["DataDescriptionEntryFormat1Level77Context"],
                    "66" or "88" => [],
                    _ => ["DataDescriptionEntryFormat1Context"],
                };
            case "FileStatement":
                return first is not null && FileVerbs.TryGetValue(first, out var file) ? [file] : [];
            case "CicsValue":
                return first switch
                {
                    "DFHRESP" => ["CicsDfhRespLiteralContext"],
                    "DFHVALUE" => ["CicsDfhValueLiteralContext"],
                    _ => [],
                };
            case "CicsOption":
                return first switch
                {
                    "FILE" or "DATASET" => ["Cics_file_nameContext"],
                    "RESP" or "RESP2" or "NOHANDLE" => ["Cics_handle_responseContext"],
                    "ERASE" => ["Cics_send_eraseContext"],
                    _ => [],
                };
            case "CicsCommand":
                if (first is null || !CicsVerbs.TryGetValue(first, out var verb))
                    return first == "SET" && options.Contains("TERMINAL") ? ["Cics_set_terminalContext"] : [];
                var roles = new List<string> { verb };
                foreach (var form in CicsForms)
                    if (form.Verb == first && (leading.Skip(1).Contains(form.Word) || options.Contains(form.Word))
                        && !roles.Contains(form.Role))
                        roles.Add(form.Role);
                return roles;
            default:
                return [];
        }
    }

    private static readonly string[] RootKeys = ["node", "children", "range", "textSha256"];

    // A root from COPY ... REPLACING text also carries the "replacing" proof of the COPY statement.
    public static bool IsStarforgeNode(JsonNode? node) =>
        node is JsonObject obj && PyJson.IsStr(Get(obj, "node")) && Get(obj, "children") is JsonArray
        && (obj.Count == 2 || obj.Count == 4 && RootKeys.All(obj.ContainsKey)
            || obj.Count == 5 && RootKeys.All(obj.ContainsKey) && obj.ContainsKey("replacing"));

    public static bool IsStarforgeNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("node", out var kind)
            || kind.ValueKind != JsonValueKind.String || !node.TryGetProperty("children", out var children)
            || children.ValueKind != JsonValueKind.Array)
            return false;
        var count = node.EnumerateObject().Count();
        return count == 2 || (count == 4 || count == 5 && node.TryGetProperty("replacing", out _))
            && node.TryGetProperty("range", out _) && node.TryGetProperty("textSha256", out _);
    }

    public static IReadOnlyList<string> Roles(JsonNode? node)
    {
        if (!IsStarforgeNode(node)) return [];
        var children = ((JsonArray)node!["children"]!).ToList();
        var leading = children.TakeWhile(child => child is JsonObject obj && obj.ContainsKey("token"))
            .Select(child => (PyJson.Str(child!["token"]) ?? "").ToUpperInvariant()).ToList();
        var options = children.Where(child => child is JsonObject obj && PyJson.Str(Get(obj, "node")) == "CicsOption")
            .Select(child => (child!["children"] as JsonArray)?.FirstOrDefault() is JsonObject token && PyJson.Str(Get(token, "token")) is { } name
                ? name.ToUpperInvariant() : "")
            .ToList();
        return Roles(PyJson.Str(node!["node"])!, leading, options);
    }

    public static IReadOnlyList<string> Roles(JsonElement node)
    {
        if (!IsStarforgeNode(node)) return [];
        var children = node.GetProperty("children").EnumerateArray().ToList();
        static string? Token(JsonElement item) =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty("token", out var token)
                && token.ValueKind == JsonValueKind.String ? token.GetString() : null;
        var leading = children.TakeWhile(child => Token(child) is not null).Select(child => Token(child)!.ToUpperInvariant()).ToList();
        var options = children.Where(child => child.ValueKind == JsonValueKind.Object && child.TryGetProperty("node", out var kind)
                && kind.ValueKind == JsonValueKind.String && kind.GetString() == "CicsOption")
            .Select(child => child.GetProperty("children") is { ValueKind: JsonValueKind.Array } items && items.GetArrayLength() > 0
                && Token(items[0]) is { } name ? name.ToUpperInvariant() : "")
            .ToList();
        return Roles(node.GetProperty("node").GetString()!, leading, options);
    }

    // The primary role, or the ANTLR rule for a cobol-rekt tree; null for tokens and foreign nodes.
    public static string? RuleOf(JsonNode? node) =>
        IsStarforge ? Roles(node).FirstOrDefault() : PyJson.Str(Get(node, "rule"));

    public static string? RuleOf(JsonElement node)
    {
        if (IsStarforge) return Roles(node).FirstOrDefault();
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty("rule", out var rule)
            && rule.ValueKind == JsonValueKind.String ? rule.GetString() : null;
    }

    public static bool IsRule(JsonNode? node, string role) =>
        IsStarforge ? Roles(node).Contains(role) : PyJson.Str(Get(node, "rule")) == role;

    public static bool IsRule(JsonElement node, string role) =>
        IsStarforge ? Roles(node).Contains(role) : RuleOf(node) == role;

    // A non-token tree node in the current producer's format (Has(node, "rule") for cobol-rekt).
    public static bool HasRule(JsonNode? node) =>
        IsStarforge ? node is JsonObject obj && obj.ContainsKey("node") : node is JsonObject rekt && rekt.ContainsKey("rule");

    public static bool HasRule(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(IsStarforge ? "node" : "rule", out _);

    // Well-formed interior node: {rule, children} for cobol-rekt, {node, children[, range, textSha256]} for Starforge.
    public static bool IsRuleNode(JsonNode? node) =>
        IsStarforge ? IsStarforgeNode(node)
        : node is JsonObject obj && obj.Count == 2 && PyJson.IsStr(Get(obj, "rule")) && Get(obj, "children") is JsonArray;

    public static bool IsRuleNode(JsonElement node)
    {
        if (IsStarforge) return IsStarforgeNode(node);
        return node.ValueKind == JsonValueKind.Object && node.EnumerateObject().Count() == 2
            && node.TryGetProperty("rule", out var rule) && rule.ValueKind == JsonValueKind.String
            && node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array;
    }

    // The role a statement is dispatched on: its grammarRule for cobol-rekt; for Starforge the grammarRule must
    // name the syntax root's node kind and the root's primary role is used.
    public static string? StatementRule(JsonNode? statement)
    {
        if (!IsStarforge) return PyJson.Str(Get(statement, "grammarRule"));
        var syntax = Get(statement, "syntax");
        return IsStarforgeNode(syntax) && PyJson.Str(Get(statement, "grammarRule")) == PyJson.Str(syntax!["node"])
            ? RuleOf(syntax) : null;
    }

    public static string? StatementRule(JsonElement statement)
    {
        string? Text(JsonElement element, string key) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (!IsStarforge) return Text(statement, "grammarRule");
        return statement.ValueKind == JsonValueKind.Object && statement.TryGetProperty("syntax", out var syntax)
            && IsStarforgeNode(syntax) && Text(statement, "grammarRule") == Text(syntax, "node") ? RuleOf(syntax) : null;
    }

    public static IEnumerable<string?> AllRoles(JsonElement node) => IsStarforge ? Roles(node) : [RuleOf(node)];

    // The producer's own name for a node: the ANTLR rule or the Starforge node kind.
    public static string? NodeName(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(IsStarforge ? "node" : "rule", out var name)
            && name.ValueKind == JsonValueKind.String ? name.GetString() : null;

    // Command words and option names directly under a Starforge CICS command node.
    public static List<string> CommandWords(JsonElement syntax)
    {
        var words = new List<string>();
        if (!syntax.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return words;
        foreach (var child in children.EnumerateArray())
            if (child.ValueKind != JsonValueKind.Object) continue;
            else if (child.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String)
                words.Add(token.GetString()!.ToUpperInvariant());
            else if (NodeName(child) == "CicsOption" && child.GetProperty("children") is { ValueKind: JsonValueKind.Array } parts
                     && parts.GetArrayLength() > 0 && parts[0].TryGetProperty("token", out var name) && name.ValueKind == JsonValueKind.String)
                words.Add(name.GetString()!.ToUpperInvariant());
        return words;
    }

    // SEND FROM(area) [LENGTH|FLENGTH(n)] [WAIT] [ERASE] [RESP/RESP2/NOHANDLE] over a Starforge command's words.
    public static bool PlainSend(IReadOnlyList<string> words) =>
        words.Count >= 2 && words[0] == "SEND" && words[1] == "FROM" && words.Count(word => word == "FROM") == 1
        && words.Count(word => word is "LENGTH" or "FLENGTH") <= 1
        && words.Skip(2).All(word => word is "LENGTH" or "FLENGTH" or "WAIT" or "ERASE" or "RESP" or "RESP2" or "NOHANDLE")
        && words.Distinct().Count() == words.Count;

    // A synthetic one-name operand node in the current producer's format.
    public static JsonObject NameOperand(string operand) => IsStarforge
        ? new JsonObject
        {
            ["node"] = "CicsArgument",
            ["children"] = new JsonArray(new JsonObject { ["token"] = "(" }, new JsonObject { ["token"] = operand }, new JsonObject { ["token"] = ")" }),
        }
        : new JsonObject
        {
            ["rule"] = "Cics_nameContext",
            ["children"] = new JsonArray(new JsonObject { ["token"] = "(" }, new JsonObject { ["token"] = operand }, new JsonObject { ["token"] = ")" }),
        };

    private static JsonNode? Get(JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;
}
