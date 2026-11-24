using System.Text.Json;
using Starforge.Cobol.Copy;
using Starforge.Cobol.Exec;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Deps;

public static class DependencyExtractor
{
    public static ProgramDependencies Extract(CompilationResult result)
    {
        var tokens = result.Tokens;
        var execs = ExecStatements(result);
        var programs = result.Syntax.AllPrograms().ToList();
        var calls = new List<ProgramReference>();
        var transfers = new List<ControlTransfer>();
        var resources = new List<ResourceReference>();
        var presence = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["CALL"] = 0,
            ["COPY"] = result.Inclusions.Count,
            ["EXEC_CICS"] = execs.Count(e => e.Statement.Kind == ExecKind.Cics),
            ["EXEC_DLI"] = execs.Count(e => e.Statement.Kind is ExecKind.Dli or ExecKind.SqlIms),
            ["EXEC_SQL"] = execs.Count(e => e.Statement.Kind == ExecKind.Sql),
            ["FILE_IO"] = 0,
        };

        var constants = new List<ConstantResolver>();
        for (var p = 0; p < programs.Count; p++)
        {
            if (p >= result.Programs.Count || programs[p].Procedure is not { } procedure)
            {
                continue;
            }

            var bindings = p < result.Bindings.Count ? result.Bindings[p] : ProcedureBindings.Empty;
            var statements = procedure.AllStatements().ToList();
            var resolver = new ConstantResolver(result.Programs[p], bindings, procedure, statements, execs.Select(e => e.Statement));
            constants.Add(resolver);
            foreach (var s in statements)
            {
                var line = tokens.ProgramLine(s.Span.FirstToken);
                switch (s)
                {
                    case CallStatement call:
                    {
                        presence["CALL"]++;
                        if (call.Program is { } literal)
                        {
                            calls.Add(new ProgramReference(DependencyKind.Call, literal, true, null, line));
                        }
                        else
                        {
                            var name = ProcedureText.Of(call.Target);
                            var value = call.Target is DataRef r ? resolver.ValueOf(bindings.ItemOf(r)) : null;
                            calls.Add(new ProgramReference(DependencyKind.Call, value ?? name, value is not null, value is null ? null : name, line));
                        }

                        break;
                    }

                    case OpenStatement open:
                        presence["FILE_IO"]++;
                        foreach (var f in open.Files)
                        {
                            resources.Add(new ResourceReference("file", FileName(f.File, bindings), true, "OPEN " + f.Mode, line));
                        }

                        break;
                    case CloseStatement close:
                        presence["FILE_IO"]++;
                        foreach (var f in close.Files)
                        {
                            resources.Add(new ResourceReference("file", FileName(f, bindings), true, "CLOSE", line));
                        }

                        break;
                    case FileStatement file:
                        presence["FILE_IO"]++;
                        resources.Add(new ResourceReference("file", FileName(file.Target, bindings), true, file.Verb, line));
                        break;
                }
            }
        }

        foreach (var (exec, line) in execs)
        {
            var resolver = constants.Count > 0 ? constants[0] : null;
            switch (exec)
            {
                case CicsCommand cics:
                    AddCics(cics, line, resolver, calls, transfers, resources);
                    break;
                case SqlStatement sql:
                    foreach (var t in sql.Tables)
                    {
                        resources.Add(new ResourceReference("db2-table", t.Name, true, t.Operation.ToString().ToUpperInvariant(), line));
                    }

                    break;
                case DliCommand dli:
                    if (dli.Psb is { } psb)
                    {
                        resources.Add(new ResourceReference("psb", psb.Name, psb.IsStatic, dli.Function, line));
                    }

                    foreach (var segment in dli.Segments)
                    {
                        resources.Add(new ResourceReference("ims-segment", segment.Name.Name, segment.Name.IsStatic, dli.Function, line));
                    }

                    break;
            }
        }

        return new ProgramDependencies(Copybooks(result), calls, transfers, resources, presence);
    }

    private static void AddCics(CicsCommand cics, int line, ConstantResolver? resolver, List<ProgramReference> calls, List<ControlTransfer> transfers, List<ResourceReference> resources)
    {
        (string Name, bool IsStatic, string? From) Named(ExecTarget target)
        {
            if (target.IsStatic)
            {
                return (target.Name, true, null);
            }

            var value = target.DataName is { } d ? resolver?.ValueOf(d) : null;
            return value is null ? (target.Name, false, null) : (value, true, target.Name);
        }

        var program = cics.Resources.FirstOrDefault(r => r.Kind == ExecResourceKind.Program);
        var transId = cics.Resources.FirstOrDefault(r => r.Kind == ExecResourceKind.TransId);
        if (program is not null && cics.Verb is "LINK" or "XCTL")
        {
            var (name, isStatic, from) = Named(program.Target);
            calls.Add(new ProgramReference(cics.Verb == "LINK" ? DependencyKind.CicsLink : DependencyKind.CicsXctl, name, isStatic, from, line));
        }

        // The command table names RUN by its option, "RUN TRANSID"; the transfer is recorded under the bare verb.
        var transfer = cics.Verb == "RUN TRANSID" ? "RUN" : cics.Verb;
        if (transfer is "RETURN" or "XCTL" or "START" or "RUN")
        {
            var target = transfer == "XCTL" ? program : transId;
            if (target is null)
            {
                transfers.Add(new ControlTransfer(transfer, null, true, null, line));
            }
            else
            {
                var (name, isStatic, from) = Named(target.Target);
                transfers.Add(new ControlTransfer(transfer, name, isStatic, from, line));
            }
        }

        foreach (var r in cics.Resources)
        {
            if (r.Kind is ExecResourceKind.Label || (r.Kind == ExecResourceKind.Program && cics.Verb is "LINK" or "XCTL"))
            {
                continue;
            }

            resources.Add(new ResourceReference(ResourceKind(r.Kind), r.Target.Name, r.Target.IsStatic, cics.Verb, line));
            if (r.Kind == ExecResourceKind.Map && r.Qualifier is { } mapset)
            {
                resources.Add(new ResourceReference("mapset", mapset.Name, mapset.IsStatic, cics.Verb, line));
            }
        }
    }

    private static string ResourceKind(ExecResourceKind kind) => kind switch
    {
        ExecResourceKind.TransId => "transid",
        ExecResourceKind.TsQueue => "ts-queue",
        ExecResourceKind.TdQueue => "td-queue",
        _ => kind.ToString().ToLowerInvariant(),
    };

    // WRITE and REWRITE name a record; the dependency is the file that owns it.
    private static string FileName(DataRef reference, ProcedureBindings bindings)
        => bindings.ItemOf(reference)?.FileName ?? reference.Name.Name;

    private static List<CopybookReference> Copybooks(CompilationResult result)
    {
        var list = new List<CopybookReference>();
        foreach (var inc in result.Inclusions)
        {
            var member = inc.Name.Trim('\'', '"');
            var resolved = inc.Status == CopyStatus.Resolved ? inc.ResolvedPath : null;
            var platform = resolved is not null && IsPlatformProjection(resolved);
            var file = resolved is null ? null : Path.GetFileName(resolved);
            var stem = Path.GetFileNameWithoutExtension(file ?? member);
            var name = platform ? stem : stem + ".cpy";
            list.Add(new CopybookReference(name, member, platform ? DependencyKind.PlatformCopy : DependencyKind.Copy, inc.Status, resolved, inc.StatementRange.StartLine));
        }

        return list;
    }

    // Generated platform copybooks (DFHAID, DFHBMSCA) carry a sidecar naming a platform contract as their producer.
    private static bool IsPlatformProjection(string path)
    {
        var sidecar = path + ".preprocess.json";
        if (!File.Exists(sidecar))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("producer", out var producer)
                && producer.ValueKind == JsonValueKind.String
                && producer.GetString()!.StartsWith("platform-", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static IReadOnlyList<(Exec.ExecStatement Statement, int Line)> ExecStatements(CompilationResult result)
    {
        var tokens = result.Tokens;
        var list = new List<(Exec.ExecStatement, int)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.ExecBlock)
            {
                continue;
            }

            var statement = result.ParsedExecs.TryGetValue(i, out var parsed) ? parsed : ExecParser.Parse(tokens, i);
            list.Add((statement, tokens.ProgramLine(i)));
        }

        return list;
    }
}

// A data name holds a compile-time constant when its only VALUE is one alphanumeric literal and no statement
// can change its storage: no procedure or EXEC reference other than as a CALL/LINK/XCTL program touches it.
internal sealed class ConstantResolver
{
    private readonly ProgramData _data;
    private readonly List<(DataItem Root, int Start, int End)> _touched = new();

    public ConstantResolver(ProgramData data, ProcedureBindings bindings, ProcedureDivision procedure, IReadOnlyList<Statement> statements, IEnumerable<Exec.ExecStatement> execs)
    {
        _data = data;
        var programTargets = new HashSet<DataRef>(statements.OfType<CallStatement>().Select(c => c.Target).OfType<DataRef>(), ReferenceEqualityComparer.Instance);
        foreach (var reference in procedure.DataReferences)
        {
            if (!programTargets.Contains(reference))
            {
                Touch(bindings.ItemOf(reference) is { } item ? new[] { item } : Lookup(reference.Name.Name, reference.Name.Qualifiers));
            }
        }

        foreach (var exec in execs)
        {
            var options = exec switch
            {
                CicsCommand c => c.Options.Where(o => !(o.Name == "PROGRAM" && c.Verb is "LINK" or "XCTL")),
                DliCommand d => d.Options.Concat(d.Segments.SelectMany(s => s.Options)),
                _ => Enumerable.Empty<ExecOption>(),
            };
            foreach (var option in options)
            {
                if (option.Argument?.DataName is { } name)
                {
                    Touch(Lookup(name, null));
                }
            }

            if (exec is SqlStatement sql)
            {
                foreach (var host in sql.HostVariables)
                {
                    Touch(Lookup(host.Field, host.Qualifier is null ? null : new[] { host.Qualifier }));
                    if (host.Indicator is { } indicator)
                    {
                        Touch(Lookup(indicator, null));
                    }
                }
            }
        }
    }

    public string? ValueOf(string dataName) => ValueOf(_data.Symbols.Resolve(dataName).Item);

    public string? ValueOf(DataItem? item)
    {
        if (item is null || !item.HasStorage || item.Section is not (DataSectionKind.WorkingStorage or DataSectionKind.LocalStorage)
            || item.External || item.Record.External || item.Ancestors().Prepend(item).Any(a => a.Occurs is not null || a.RedefinesName is not null)
            || item.Entry.Value is not { Values: [{ From: { Kind: TokenKind.AlphanumericLiteral, All: false } literal, Thru: null }] })
        {
            return null;
        }

        var (root, start, end) = Extent(item);
        if (_touched.Any(t => ReferenceEquals(t.Root, root) && t.Start < end && start < t.End))
        {
            return null;
        }

        var value = ExecArguments.LiteralValue(literal.Text);
        if (item.Length > 0 && value.Length > item.Length)
        {
            value = value[..item.Length];
        }

        value = value.TrimEnd(' ');
        return value.Length == 0 ? null : value;
    }

    private IReadOnlyList<DataItem> Lookup(string name, IReadOnlyList<string>? qualifiers)
        => _data.Symbols.Resolve(name, qualifiers).Candidates;

    private void Touch(IReadOnlyList<DataItem> items)
    {
        foreach (var item in items)
        {
            var outer = item.Ancestors().Prepend(item).LastOrDefault(a => a.Occurs is not null) ?? item;
            _touched.Add(Extent(outer));
        }
    }

    // Storage is identified by the record it lives in (following record-level REDEFINES) and its byte range.
    private static (DataItem Root, int Start, int End) Extent(DataItem item)
    {
        var root = item.Record;
        while (root.Redefines is { } redefined && !ReferenceEquals(redefined, root))
        {
            root = redefined.Record;
        }

        return (root, item.Offset, item.Offset + Math.Max(item.TotalLength, 1));
    }
}
