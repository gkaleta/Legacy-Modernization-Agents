using System.Text.Json.Nodes;
using Starforge.Cobol.Copy;
using Starforge.Cobol.Exec;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Native;

// ProgramPath is the program inside its dependency search view (a folder holding the program and the callees
// it may statically CALL); CopybookRoot is the folder copybooks resolve from. Profile is the target-port
// profile whose runtimeContracts document CALL targets that have no COBOL source.
public sealed record NativeExportOptions(string ProgramPath, string CopybookRoot, string? ProfilePath = null);

// Writes the native dependency contract the dependency evidence gate verifies, from Starforge's own parse.
// Statement and definition trees are Starforge nodes with source ranges; a construct this exporter cannot
// prove yet becomes a "starforge-unsupported:*" diagnostic, which the gate reports, instead of a guess.
public static class NativeExporter
{
    public const string Producer = "starforge";

    private static readonly string[] Domains = ["CALL", "COPY", "FILE_IO", "EXEC_CICS", "EXEC_SQL"];

    private static readonly HashSet<string> SourceSuffixes = new(StringComparer.OrdinalIgnoreCase) { ".cbl", ".cob", ".cobol", "" };

    private static readonly HashSet<string> FileHandlerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "NOT", "INVALID", "END", "EOP", "END-OF-PAGE", "END-READ", "END-WRITE", "END-REWRITE", "END-DELETE", "END-START",
    };

    public static JsonObject Export(NativeExportOptions options)
    {
        var program = Path.GetFullPath(options.ProgramPath);
        var profile = options.ProfilePath is { Length: > 0 } p ? Profile.Load(Path.GetFullPath(p)) : null;
        var frontEnd = new CobolFrontEnd(new FrontEndOptions(new[] { Path.GetFullPath(options.CopybookRoot) }));
        var exporter = new Run(frontEnd, profile, Path.GetDirectoryName(program)!);
        return exporter.Program(program, Path.GetFileName(program), "PROGRAM", new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed record Profile(string Path, string Sha256, string Name, string Kind, JsonObject Contracts)
    {
        public static Profile Load(string path)
        {
            var document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException($"dependency profile is not a JSON object: {path}");
            return new Profile(
                path,
                NativeSyntax.FileSha256(path),
                (string?)document["profileName"] ?? string.Empty,
                (string?)document["profileKind"] ?? string.Empty,
                document["runtimeContracts"] as JsonObject ?? new JsonObject());
        }
    }

    private sealed class Run(CobolFrontEnd frontEnd, Profile? profile, string searchView)
    {
        public JsonObject Program(string path, string name, string kind, HashSet<string> active)
        {
            active.Add(path);
            var result = frontEnd.Process(path);
            var roots = new SyntaxRoots(result);
            var diagnostics = new List<string>();
            var statements = new JsonArray();
            var children = new List<JsonObject>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var copy in Copies(result, diagnostics))
            {
                if (seen.Add("COPY:" + copy["callTarget"]!["name"]))
                {
                    children.Add(copy);
                }
            }

            var units = result.Syntax.AllPrograms().ToList();
            var fileIo = false;
            for (var u = 0; u < units.Count; u++)
            {
                if (units[u].Procedure is not { } procedure)
                {
                    continue;
                }

                var files = FileBindings(units[u], roots);
                foreach (var statement in procedure.AllStatements())
                {
                    switch (statement)
                    {
                        case OpenStatement or CloseStatement or Syntax.FileStatement:
                            fileIo = true;
                            if (FileIo(statement, files, roots, diagnostics) is { } file)
                            {
                                statements.Add(file);
                            }

                            break;
                        case CallStatement call:
                            if (Call(call, active, diagnostics) is { } child && seen.Add("CALL:" + child["callTarget"]!["name"]))
                            {
                                children.Add(child);
                            }

                            break;
                        case ExecStatementSyntax exec:
                            if (Exec(exec, roots, diagnostics) is { } dialect)
                            {
                                statements.Add(dialect);
                            }

                            break;
                    }
                }
            }

            diagnostics.AddRange(roots.Problems);
            active.Remove(path);
            return Document(path, name, kind, children, statements, diagnostics.Distinct(StringComparer.Ordinal).ToList(), fileIo);
        }

        private static JsonObject Document(string path, string name, string kind, List<JsonObject> children, JsonArray statements, List<string> diagnostics, bool fileIo)
        {
            var list = statements.OfType<JsonObject>().ToList();
            var presence = new JsonObject();
            var absence = new JsonObject();
            const string prefix = "starforge-tree:";
            if (list.Count == 0)
            {
                absence["EXEC_CICS"] = prefix + "no-dialect-contexts";
                absence["EXEC_SQL"] = prefix + "no-dialect-contexts";
            }
            else
            {
                if (!list.Any(s => (string?)s["kind"] == "EXEC_CICS"))
                {
                    absence["EXEC_CICS"] = prefix + "no-cics-contexts";
                }

                if (!list.Any(s => (string?)s["kind"] == "EXEC_SQL"))
                {
                    absence["EXEC_SQL"] = prefix + "no-sql-contexts";
                }
            }

            var cics = list.Where(s => (string?)s["kind"] == "EXEC_CICS").ToList();
            var cicsFiles = cics.Any(s => s["resourceBindings"] is JsonArray b && b.OfType<JsonObject>().Any(r => (string?)r["kind"] == "CICS_FILE"));
            var sql = list.Any(s => (string?)s["kind"] == "EXEC_SQL");
            if (list.Any(s => (string?)s["kind"] == "FILE_IO"))
            {
                presence["FILE_IO"] = prefix + "bound-file-control-resources";
            }
            else if (cicsFiles)
            {
                presence["FILE_IO"] = prefix + "bound-cics-file-resources";
            }
            else if (sql)
            {
                presence["FILE_IO"] = prefix + "bound-sql-resources";
            }
            else if (!fileIo)
            {
                absence["FILE_IO"] = prefix + "no-file-io-contexts";
            }

            if (cics.Count > 0)
            {
                presence["EXEC_CICS"] = prefix + (cics.All(BareReturn) ? "resource-free-cics-return" : "bound-cics-resources");
            }

            if (sql)
            {
                presence["EXEC_SQL"] = prefix + "bound-sql-resources";
            }

            var coverage = new JsonObject();
            foreach (var domain in Domains)
            {
                coverage[domain] = diagnostics.Count == 0 ? "complete" : "unavailable";
            }

            var document = new JsonObject
            {
                ["callTarget"] = Target(name),
                ["dependencies"] = new JsonArray(children.ToArray<JsonNode>()),
                ["dependencyExportSchemaVersion"] = 1,
                ["dependencyProducer"] = Producer,
                ["dependencyCoverage"] = coverage,
                ["dependencyDiagnostics"] = new JsonArray(diagnostics.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
                ["dependencyControlTransfers"] = new JsonArray(),
                ["dependencyDialectStatements"] = statements,
                ["commareaInventory"] = new JsonObject
                {
                    ["schema"] = 1,
                    ["records"] = new JsonArray(),
                    ["carriers"] = new JsonArray(),
                    ["sends"] = new JsonArray(),
                },
                ["dependencyDerivedInputs"] = new JsonArray(),
                ["dependencyPresenceEvidence"] = presence,
                ["dependencyAbsenceEvidence"] = absence,
                ["sourcePath"] = path,
                ["sourceSha256"] = NativeSyntax.FileSha256(path),
                ["dependencyKind"] = kind,
                ["dependencyAnalysisComplete"] = diagnostics.Count == 0,
                ["runtimeContractBindingComplete"] = false,
            };
            return document;
        }

        private static bool BareReturn(JsonObject statement) =>
            (string?)statement["operation"] == "RETURN" && statement["syntax"]?["children"] is JsonArray { Count: 1 } children
            && children[0] is JsonObject token && token.ContainsKey("token");

        private static JsonObject Target(string name, bool isStatic = true) => new()
        {
            ["name"] = name,
            ["programReferenceType"] = isStatic ? "STATIC" : "DYNAMIC",
        };

        private static JsonObject Leaf(string name, string kind) => new()
        {
            ["callTarget"] = Target(name),
            ["dependencies"] = new JsonArray(),
            ["dependencyExportSchemaVersion"] = 1,
            ["dependencyProducer"] = Producer,
            ["dependencyCoverage"] = new JsonObject
            {
                ["CALL"] = "unavailable",
                ["COPY"] = "unavailable",
                ["FILE_IO"] = "unavailable",
                ["EXEC_CICS"] = "unavailable",
                ["EXEC_SQL"] = "unavailable",
            },
            ["dependencyDiagnostics"] = new JsonArray(),
            ["dependencyControlTransfers"] = new JsonArray(),
            ["dependencyDialectStatements"] = new JsonArray(),
            ["dependencyDerivedInputs"] = new JsonArray(),
            ["dependencyPresenceEvidence"] = new JsonObject(),
            ["dependencyAbsenceEvidence"] = new JsonObject(),
            ["dependencyKind"] = kind,
            ["dependencyAnalysisComplete"] = false,
            ["runtimeContractBindingComplete"] = false,
        };

        private static IEnumerable<JsonObject> Copies(CompilationResult result, List<string> diagnostics)
        {
            foreach (var inclusion in result.Inclusions)
            {
                var member = inclusion.Name.Trim('\'', '"');
                if (inclusion.Status != CopyStatus.Resolved || inclusion.ResolvedPath is not { } resolved)
                {
                    diagnostics.Add($"copy-unresolved:{member}");
                    continue;
                }

                var path = Path.GetFullPath(resolved);
                if (path.Contains($"{Path.DirectorySeparatorChar}.preprocessed{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    diagnostics.Add($"starforge-unsupported:platform-copy:{Path.GetFileNameWithoutExtension(path).ToUpperInvariant()}");
                    continue;
                }

                var leaf = Leaf(Path.GetFileNameWithoutExtension(path) + ".cpy", "COPY");
                leaf.Remove("dependencyKind");
                leaf.Remove("dependencyAnalysisComplete");
                leaf.Remove("runtimeContractBindingComplete");
                leaf["sourcePath"] = path;
                leaf["sourceSha256"] = NativeSyntax.FileSha256(path);
                leaf["dependencyKind"] = "COPY";
                leaf["dependencyAnalysisComplete"] = false;
                leaf["runtimeContractBindingComplete"] = false;
                yield return leaf;
            }
        }

        private JsonObject? Call(CallStatement call, HashSet<string> active, List<string> diagnostics)
        {
            if (call.Program is not { } literal)
            {
                diagnostics.Add($"dynamic-call-unsupported:{ProcedureText.Of(call.Target)}");
                return null;
            }

            var name = literal.Trim('\'', '"').ToUpperInvariant();
            var callee = Directory.EnumerateFiles(searchView)
                .Where(f => SourceSuffixes.Contains(Path.GetExtension(f))
                    && string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .FirstOrDefault();
            if (callee is not null)
            {
                if (active.Contains(callee))
                {
                    diagnostics.Add($"starforge-unsupported:recursive-call:{name}");
                    return null;
                }

                return Program(callee, name, "CALL", active);
            }

            if (profile?.Contracts[name] is JsonObject definition)
            {
                var leaf = Leaf(name, "EXTERNAL_RUNTIME");
                leaf["runtimeContractBindingComplete"] = true;
                leaf["runtimeContract"] = new JsonObject
                {
                    ["profileName"] = profile.Name,
                    ["profileKind"] = profile.Kind,
                    ["sourcePath"] = profile.Path,
                    ["sourceSha256"] = profile.Sha256,
                    ["name"] = name,
                    ["definition"] = definition.DeepClone(),
                    ["runtimeMapping"] = "unverified",
                    ["resolutionKind"] = "documented-platform-contract",
                };
                return leaf;
            }

            diagnostics.Add($"call-target-unresolved:{name}");
            return null;
        }

        private static Dictionary<string, (JsonObject Binding, HashSet<string> Records)> FileBindings(ProgramUnit unit, SyntaxRoots roots)
        {
            var bindings = new Dictionary<string, (JsonObject, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);
            var descriptions = (unit.Data?.Sections ?? [])
                .SelectMany(s => s.Files)
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in unit.Environment?.InputOutput?.Files ?? [])
            {
                if (!descriptions.TryGetValue(entry.FileName, out var description))
                {
                    continue;
                }

                var name = entry.FileName.ToUpperInvariant();
                var records = description.Records.Where(r => r.Level == 1 && r.Name is not null)
                    .Select(r => r.Name!.ToUpperInvariant()).ToList();
                var binding = new JsonObject
                {
                    ["kind"] = "COBOL_FILE",
                    ["name"] = name,
                    ["assignment"] = string.Join(' ', entry.Assign),
                    ["organization"] = entry.Organization ?? "SEQUENTIAL",
                    ["accessMode"] = entry.AccessMode ?? "SEQUENTIAL",
                    ["records"] = new JsonArray(records.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                    ["definitionScope"] = "expanded-parser-input",
                    ["definitionSyntax"] = Definition(entry, roots),
                    ["descriptionSyntax"] = Description(description, roots),
                    ["runtimeMapping"] = "unverified",
                };
                bindings[name] = (binding, records.ToHashSet(StringComparer.OrdinalIgnoreCase));
            }

            return bindings;
        }

        // SELECT ... with the file name wrapped in a FileName node.
        private static JsonObject Definition(FileControlEntry entry, SyntaxRoots roots)
        {
            var tokens = roots.Tokens;
            var children = new List<JsonNode>();
            var named = false;
            for (var i = entry.Span.FirstToken; i < entry.Span.EndToken; i++)
            {
                var text = tokens.Text(i);
                if (!named && string.Equals(text, entry.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    children.Add(NativeSyntax.Node("FileName", [NativeSyntax.Token(text)]));
                    named = true;
                }
                else
                {
                    children.Add(NativeSyntax.Token(text));
                }
            }

            return roots.RootTokens(NativeSyntax.Node("FileControlEntry", children), entry.Span.FirstToken, entry.Span.EndToken, "select:" + entry.FileName);
        }

        // The FD header up to its period, with the file name wrapped in a FileDescriptionName node.
        private static JsonObject Description(FileDescription description, SyntaxRoots roots)
        {
            var tokens = roots.Tokens;
            var children = new List<JsonNode>();
            var named = false;
            var end = description.Span.FirstToken;
            for (var i = description.Span.FirstToken; i < description.Span.EndToken; i++)
            {
                var text = tokens.Text(i);
                end = i + 1;
                if (!named && string.Equals(text, description.Name, StringComparison.OrdinalIgnoreCase))
                {
                    children.Add(NativeSyntax.Node("FileDescriptionName", [NativeSyntax.Token(text)]));
                    named = true;
                }
                else
                {
                    children.Add(NativeSyntax.Token(text));
                }

                if (text == ".")
                {
                    break;
                }
            }

            return roots.RootTokens(NativeSyntax.Node("FileDescriptionEntry", children), description.Span.FirstToken, end, "fd:" + description.Name);
        }

        private static JsonObject? FileIo(Statement statement, Dictionary<string, (JsonObject Binding, HashSet<string> Records)> files, SyntaxRoots roots, List<string> diagnostics)
        {
            var (kind, operation, operands) = statement switch
            {
                OpenStatement open => ("OpenStatement", "OPEN", open.Files.Select(f => f.File.Name.Name).ToList()),
                CloseStatement close => ("CloseStatement", "CLOSE", close.Files.Select(f => f.Name.Name).ToList()),
                Syntax.FileStatement file => ("FileStatement", file.Verb.ToUpperInvariant(), new List<string> { file.Target.Name.Name }),
                _ => throw new InvalidOperationException(),
            };
            operands = operands.Select(o => o.ToUpperInvariant()).ToList();
            var bindings = new List<JsonObject>();
            foreach (var operand in operands)
            {
                var match = files.Values.FirstOrDefault(f => (string?)f.Binding["name"] == operand || f.Records.Contains(operand));
                if (match.Binding is null)
                {
                    diagnostics.Add($"file-operand-unbound:{operation}:{operand}");
                    return null;
                }

                if (!bindings.Contains(match.Binding))
                {
                    bindings.Add(match.Binding);
                }
            }

            var tokens = roots.Tokens;
            var end = statement.Span.FirstToken + 1;
            for (var i = statement.Span.FirstToken + 1; i < statement.Span.EndToken; i++)
            {
                if (statement is Syntax.FileStatement && FileHandlerWords.Contains(tokens.Text(i)))
                {
                    break;
                }

                end = i + 1;
            }

            var words = Enumerable.Range(statement.Span.FirstToken, end - statement.Span.FirstToken).Select(tokens.Text).ToList();
            var syntax = roots.RootTokens(NativeSyntax.Node(kind, words.Select(w => (JsonNode)NativeSyntax.Token(w))), statement.Span.FirstToken, end, operation);
            var (line, column) = roots.Position(tokens[statement.Span.FirstToken].Start);
            return new JsonObject
            {
                ["kind"] = "FILE_IO",
                ["operation"] = operation,
                ["grammarRule"] = kind,
                ["sourceText"] = string.Join(' ', words),
                ["coordinateSpace"] = "expanded-parser-input",
                ["line"] = line,
                ["column"] = column,
                ["runtimeMapping"] = "unverified",
                ["syntax"] = syntax,
                ["fileOperands"] = new JsonArray(operands.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()),
                ["resourceBindings"] = new JsonArray(bindings.Select(b => (JsonNode)b.DeepClone()).ToArray()),
                ["bindingStatus"] = "complete",
            };
        }

        private static JsonObject? Exec(ExecStatementSyntax exec, SyntaxRoots roots, List<string> diagnostics)
        {
            switch (exec.Parsed)
            {
                case CicsCommand { Verb: "RETURN", Options.Count: 0 } command:
                {
                    var syntax = roots.Root(NativeSyntax.Node("CicsCommand", [NativeSyntax.Token("RETURN")]), command.ExpandedStart, command.ExpandedEnd, "cics:RETURN");
                    var (line, column) = roots.Position(roots.Tokens[exec.Span.FirstToken].Start);
                    return new JsonObject
                    {
                        ["kind"] = "EXEC_CICS",
                        ["operation"] = "RETURN",
                        ["grammarRule"] = "CicsCommand",
                        ["sourceText"] = "RETURN",
                        ["coordinateSpace"] = "expanded-parser-input",
                        ["line"] = line,
                        ["column"] = column,
                        ["runtimeMapping"] = "unverified",
                        ["syntax"] = syntax,
                        ["resourceReferences"] = new JsonArray(),
                        ["resourceBindings"] = new JsonArray(),
                        ["bindingStatus"] = "complete",
                        ["bindingBasis"] = "parsed-cics-terminal-operation",
                    };
                }

                case CicsCommand command:
                    diagnostics.Add($"starforge-unsupported:cics:{command.Verb}");
                    return null;
                default:
                    diagnostics.Add($"starforge-unsupported:{exec.Kind.ToString().ToLowerInvariant()}:{exec.Parsed.Operation}");
                    return null;
            }
        }
    }
}
