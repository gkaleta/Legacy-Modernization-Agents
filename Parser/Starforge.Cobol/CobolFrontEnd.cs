using System.Text.RegularExpressions;
using Starforge.Cobol.Copy;
using Starforge.Cobol.Coverage;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Source;

namespace Starforge.Cobol;

// Null switches are detected from the source (WITH DEBUGGING MODE, DECIMAL-POINT IS COMMA).
public sealed record FrontEndOptions(
    IReadOnlyList<string> EstateRoots,
    bool? DebuggingMode = null,
    bool? DecimalPointIsComma = null,
    int MaxCopyDepth = 50,
    SourceFormat? Format = null);

public sealed class CompilationResult
{
    internal CompilationResult(
        SourceUnit program,
        IReadOnlyList<SourceUnit> units,
        IReadOnlyList<CopyInclusion> inclusions,
        ExpandedSource expanded,
        TokenStream tokens,
        IReadOnlyList<Diagnostic> diagnostics,
        TokenAccounting accounting,
        CoverageReport coverage,
        bool debuggingMode,
        bool decimalPointIsComma)
    {
        Program = program;
        Units = units;
        Inclusions = inclusions;
        Expanded = expanded;
        Tokens = tokens;
        Diagnostics = diagnostics;
        Accounting = accounting;
        Coverage = coverage;
        DebuggingMode = debuggingMode;
        DecimalPointIsComma = decimalPointIsComma;
    }

    public SourceUnit Program { get; }

    public IReadOnlyList<SourceUnit> Units { get; }

    public IReadOnlyList<CopyInclusion> Inclusions { get; }

    public ExpandedSource Expanded { get; }

    public TokenStream Tokens { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    // Later stages promote Lexed characters to Parsed here and call RebuildCoverage().
    public TokenAccounting Accounting { get; }

    public CoverageReport Coverage { get; private set; }

    public bool DebuggingMode { get; }

    public bool DecimalPointIsComma { get; }

    public int PhysicalLines => Program.Source.File.Lines.Count;

    // Physical lines of the program plus every copybook instance, i.e. what a listing of the expanded source would show.
    public int ExpandedPhysicalLines => Units.Sum(u => u.Source.File.Lines.Count);

    public int ExpandedCodeLines => Units.Sum(u => u.Source.Lines.Count(l => l.Kind is SourceLineKind.Code or SourceLineKind.Continuation));

    public CoverageReport RebuildCoverage() => Coverage = Accounting.BuildReport();

    // Later passes (EXEC, statement parsers) report here so one list holds every diagnostic of the program.
    public void AddDiagnostics(IEnumerable<Diagnostic> diagnostics) => ((List<Diagnostic>)Diagnostics).AddRange(diagnostics);
}

// Thread-safe: one instance can process many programs in parallel and shares the copybook cache between them.
public sealed class CobolFrontEnd
{
    private static readonly Regex DecimalPointComma = new(@"\bDECIMAL-POINT\s+(IS\s+)?COMMA\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly FrontEndOptions _options;
    private readonly CopybookResolver _resolver;
    private readonly SourceCache _cache = new();

    public CobolFrontEnd(FrontEndOptions options)
    {
        _options = options;
        _resolver = new CopybookResolver(options.EstateRoots);
    }

    public CopybookResolver Resolver => _resolver;

    public CompilationResult Process(string programPath) => Process(SourceFile.Load(Path.GetFullPath(programPath)));

    public CompilationResult Process(SourceFile file)
    {
        var debug = _options.DebuggingMode ?? SourceReader.DeclaresDebuggingMode(file);
        var logical = SourceReader.Read(file, new SourceReaderOptions(debug, IsProgram: true, _options.Format));
        var accounting = new TokenAccounting();
        var program = new SourceUnit(0, SourceUnitKind.Program, logical, -1, null, null, null, 0, false);
        accounting.Register(program);

        var diagnostics = new List<Diagnostic>(logical.Diagnostics);
        var inclusions = new List<CopyInclusion>();
        var expander = new CopyExpander(_resolver, _cache, accounting, diagnostics, inclusions, new CopyExpansionOptions(_options.MaxCopyDepth, debug));
        var copied = expander.ExpandProgram(program);
        var afterReplace = ReplaceStage.Apply(copied, new ExpandedSource(copied, accounting.Units), accounting, diagnostics);
        var expanded = new ExpandedSource(afterReplace, accounting.Units);

        var comma = _options.DecimalPointIsComma ?? DecimalPointComma.IsMatch(expanded.Content);
        var lex = Lexer.Lex(expanded, new LexerOptions(comma));
        diagnostics.AddRange(lex.Diagnostics);
        MarkTokens(lex.Tokens, accounting);

        return new CompilationResult(program, accounting.Units, inclusions, expanded, lex.Tokens, diagnostics, accounting, accounting.BuildReport(), debug, comma);
    }

    private static void MarkTokens(TokenStream tokens, TokenAccounting accounting)
    {
        var text = tokens.Source.Text;
        foreach (var token in tokens)
        {
            var (cls, reason) = token.Kind switch
            {
                TokenKind.ExecBlock => (CoverageClass.Unsupported, "exec-not-yet-parsed"),
                TokenKind.Unknown => (CoverageClass.Unsupported, "lex-error"),
                TokenKind.CompilerDirective => (CoverageClass.Directive, "trace-statement"),
                TokenKind.CommentEntry => (CoverageClass.Comment, "comment-entry"),
                _ when (token.Flags & TokenFlags.Unterminated) != 0 => (CoverageClass.Unsupported, "lex-error"),
                _ => (CoverageClass.Lexed, string.Empty),
            };
            accounting.MarkExpanded(text, token.Start, token.End, cls, reason);
        }
    }
}
