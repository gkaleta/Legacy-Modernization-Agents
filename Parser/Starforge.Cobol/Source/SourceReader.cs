using System.Text;
using System.Text.RegularExpressions;
using Starforge.Cobol.Coverage;

namespace Starforge.Cobol.Source;

public sealed record SourceReaderOptions(bool DebuggingMode = false, bool IsProgram = true, SourceFormat? Format = null);

public static partial class SourceReader
{
    private const int ProgramAreaEnd = SourceLine.ProgramAreaEnd;

    public static LogicalSource Read(SourceFile file, SourceReaderOptions? options = null)
    {
        options ??= new SourceReaderOptions();
        var format = options.Format ?? DetectFormat(file);
        var state = new ReaderState(file, format, options);
        for (var i = 0; i < file.Lines.Count; i++)
        {
            state.ProcessLine(i + 1, file.Lines[i], file.TabExpanded[i]);
        }

        return state.Finish();
    }

    public static SourceFormat DetectFormat(SourceFile file)
    {
        foreach (var line in file.Lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var directive = SourceFormatDirective(trimmed);
            if (directive is not null)
            {
                return directive.Value;
            }

            var header = DivisionHeader().Match(line);
            if (header.Success)
            {
                // Fixed format puts division headers in Area A (column 8); anything left of that is free form.
                return header.Groups[1].Length < 7 ? SourceFormat.Free : SourceFormat.Fixed;
            }
        }

        return SourceFormat.Fixed;
    }

    public static bool DeclaresDebuggingMode(SourceFile file)
    {
        foreach (var line in file.Lines)
        {
            if (line.Length >= 7 && line[6] is '*' or '/')
            {
                continue;
            }

            if (DebuggingMode().IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    internal static SourceFormat? SourceFormatDirective(string trimmed)
    {
        var m = SourceFormatRegex().Match(trimmed);
        if (!m.Success)
        {
            return null;
        }

        return m.Groups[1].Value.Equals("FREE", StringComparison.OrdinalIgnoreCase) ? SourceFormat.Free : SourceFormat.Fixed;
    }

    internal static bool StartsWithWord(string text, string word)
    {
        if (!text.StartsWith(word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return text.Length == word.Length || !IsWordChar(text[word.Length]);
    }

    internal static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';

    [GeneratedRegex(@"^>>\s*SOURCE(?:\s+FORMAT)?(?:\s+IS)?\s+(FREE|FIXED)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceFormatRegex();

    [GeneratedRegex(@"^(\s*)(?:IDENTIFICATION|ID|ENVIRONMENT|DATA|PROCEDURE)\s+DIVISION\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DivisionHeader();

    [GeneratedRegex(@"\bDEBUGGING\s+MODE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DebuggingMode();

    private static readonly string[] CommentEntryParagraphs = { "AUTHOR", "INSTALLATION", "DATE-WRITTEN", "DATE-COMPILED", "SECURITY", "REMARKS" };

    private sealed class ReaderState
    {
        private readonly SourceFile _file;
        private readonly SourceReaderOptions _options;
        private readonly SourceFormat _initialFormat;
        private readonly StringBuilder _text = new();
        private readonly List<(int Start, int Line, int Column, int Length)> _runs = new();
        private readonly List<SourceLine> _lines = new();
        private readonly List<ReaderMark> _marks = new();
        private readonly List<Diagnostic> _diagnostics = new();
        private SourceFormat _format;
        private bool _inPrologue;
        private bool _inCommentEntry;
        private bool _hasCode;
        private char _openQuote;
        private int _lastCodeLine;
        private int _lastCodeEndColumn;
        private SourceFormat _lastCodeFormat;
        private int _tabLines;
        private int _firstTabLine;
        private int _tabOverflowLines;
        private int _firstTabOverflowLine;

        public ReaderState(SourceFile file, SourceFormat format, SourceReaderOptions options)
        {
            _file = file;
            _format = format;
            _initialFormat = format;
            _options = options;
            _inPrologue = options.IsProgram;
        }

        public void ProcessLine(int number, string raw, bool hadTabs)
        {
            if (_format == SourceFormat.Free)
            {
                ProcessFree(number, raw, hadTabs);
            }
            else
            {
                ProcessFixed(number, raw, hadTabs);
            }
        }

        public LogicalSource Finish()
        {
            if (_openQuote != '\0')
            {
                Diag("unterminated-literal", DiagnosticSeverity.Error, "Alphanumeric literal is not closed before end of file", _lastCodeLine, _lastCodeEndColumn);
            }

            if (_hasCode)
            {
                _text.Append('\n');
            }

            if (_tabLines > 0)
            {
                Diag("tab-expanded", DiagnosticSeverity.Info, $"{_tabLines} line(s) contain tabs; expanded to {SourceFile.TabStop}-column stops before column rules were applied", _firstTabLine, 1);
            }

            if (_tabOverflowLines > 0)
            {
                Diag("tab-overflow", DiagnosticSeverity.Warning, $"{_tabOverflowLines} line(s) with tabs extend past column {ProgramAreaEnd} after expansion; the text beyond it is ignored", _firstTabOverflowLine, ProgramAreaEnd + 1);
            }

            return new LogicalSource(_file, _initialFormat, _lines, _text.ToString(), _runs, _marks, _diagnostics);
        }

        private void ProcessFixed(int number, string raw, bool hadTabs)
        {
            if (_inPrologue && IsCblProcess(raw))
            {
                MarkProgram(number, raw, 1, ProgramAreaEnd, CoverageClass.Directive, "cbl-process-statement");
                MarkOutside(number, raw, 73, int.MaxValue, ReaderArea.Identification, "identification-area");
                AddLine(number, raw, SourceLineKind.Directive, hadTabs, "cbl-process-statement");
                return;
            }

            MarkOutside(number, raw, 1, 6, ReaderArea.Sequence, "sequence-area");
            MarkOutside(number, raw, 73, int.MaxValue, ReaderArea.Identification, "identification-area");

            // Tabs are not valid in IBM fixed-form source; text they push into columns 73+ is a likely editing accident.
            if (hadTabs && raw.Length > ProgramAreaEnd && !IsBlank(raw, ProgramAreaEnd + 1, raw.Length) && _tabOverflowLines++ == 0)
            {
                _firstTabOverflowLine = number;
            }

            if (raw.Length < 7 || IsBlank(raw, 7, ProgramAreaEnd))
            {
                AddLine(number, raw, SourceLineKind.Blank, hadTabs);
                return;
            }

            var indicator = raw[6];
            var content = raw.Length > 7 ? raw.Substring(7, Math.Min(raw.Length, ProgramAreaEnd) - 7) : string.Empty;
            var trimmed = content.TrimStart();

            if (indicator == '>' && content.StartsWith('>'))
            {
                MarkProgram(number, raw, 7, ProgramAreaEnd, CoverageClass.Directive, "compiler-directive");
                HandleDirective(number, raw, hadTabs, raw.Substring(6, Math.Min(raw.Length, ProgramAreaEnd) - 6));
                return;
            }

            if (indicator is not (' ' or '-' or '*' or '/' or 'D' or 'd'))
            {
                Diag("invalid-indicator", DiagnosticSeverity.Warning, $"Column 7 holds '{indicator}', which is not a valid indicator; the line is read as code", number, 7, indicator.ToString());
            }

            if (indicator != ' ')
            {
                MarkOutside(number, raw, 7, 7, ReaderArea.Indicator, "indicator-area");
            }

            switch (indicator)
            {
                case '*':
                    if (StartsWithWord(trimmed, "CBL") || StartsWithWord(trimmed, "CONTROL"))
                    {
                        MarkProgram(number, raw, 8, ProgramAreaEnd, CoverageClass.Directive, "control-statement");
                        AddLine(number, raw, SourceLineKind.Directive, hadTabs, "control-statement");
                    }
                    else
                    {
                        MarkProgram(number, raw, 8, ProgramAreaEnd, CoverageClass.Comment, "comment-line");
                        AddLine(number, raw, SourceLineKind.Comment, hadTabs, "comment-line");
                    }

                    return;
                case '/':
                    MarkProgram(number, raw, 8, ProgramAreaEnd, CoverageClass.Comment, "page-eject-comment-line");
                    AddLine(number, raw, SourceLineKind.Comment, hadTabs, "page-eject-comment-line");
                    return;
                case 'D' or 'd' when !_options.DebuggingMode:
                    MarkProgram(number, raw, 8, ProgramAreaEnd, CoverageClass.Comment, "debugging-line");
                    AddLine(number, raw, SourceLineKind.Debug, hadTabs, "debugging-line");
                    return;
            }

            if (indicator != '-' && trimmed.StartsWith(">>", StringComparison.Ordinal))
            {
                MarkProgram(number, raw, 8, ProgramAreaEnd, CoverageClass.Directive, "compiler-directive");
                HandleDirective(number, raw, hadTabs, trimmed);
                return;
            }

            if (indicator != '-' && IsListingDirective(trimmed))
            {
                MarkProgram(number, raw, 8, ProgramAreaEnd, CoverageClass.Directive, "listing-directive");
                AddLine(number, raw, SourceLineKind.Directive, hadTabs, "listing-directive");
                return;
            }

            _inPrologue = false;
            var kind = indicator == '-' ? SourceLineKind.Continuation : indicator is 'D' or 'd' ? SourceLineKind.Debug : SourceLineKind.Code;
            AddLine(number, raw, kind, hadTabs, kind == SourceLineKind.Debug ? "debugging-line-compiled" : null);
            if (indicator != '-')
            {
                var first = FirstNonBlank(content);
                if (first is >= 0 and < 4)
                {
                    _inCommentEntry = StartsCommentEntry(trimmed);
                }
            }

            AppendCode(number, content, 8, indicator == '-');
        }

        private void ProcessFree(int number, string raw, bool hadTabs)
        {
            var trimmed = raw.TrimStart();
            var lead = raw.Length - trimmed.Length;
            if (trimmed.Length == 0)
            {
                AddLine(number, raw, SourceLineKind.Blank, hadTabs);
                return;
            }

            if (trimmed.StartsWith("*>", StringComparison.Ordinal))
            {
                MarkProgram(number, raw, lead + 1, int.MaxValue, CoverageClass.Comment, "comment-line");
                AddLine(number, raw, SourceLineKind.Comment, hadTabs, "comment-line");
                return;
            }

            if (trimmed.StartsWith(">>D ", StringComparison.OrdinalIgnoreCase) && !_options.DebuggingMode)
            {
                MarkProgram(number, raw, lead + 1, int.MaxValue, CoverageClass.Comment, "debugging-line");
                AddLine(number, raw, SourceLineKind.Debug, hadTabs, "debugging-line");
                return;
            }

            if (trimmed.StartsWith(">>", StringComparison.Ordinal) && !trimmed.StartsWith(">>D ", StringComparison.OrdinalIgnoreCase))
            {
                MarkProgram(number, raw, lead + 1, int.MaxValue, CoverageClass.Directive, "compiler-directive");
                HandleDirective(number, raw, hadTabs, trimmed);
                return;
            }

            if ((_inPrologue && IsCblProcess(raw)) || IsListingDirective(trimmed))
            {
                var reason = IsListingDirective(trimmed) ? "listing-directive" : "cbl-process-statement";
                MarkProgram(number, raw, lead + 1, int.MaxValue, CoverageClass.Directive, reason);
                AddLine(number, raw, SourceLineKind.Directive, hadTabs, reason);
                return;
            }

            _inPrologue = false;
            AddLine(number, raw, SourceLineKind.Code, hadTabs);
            if (trimmed.StartsWith(">>D ", StringComparison.OrdinalIgnoreCase))
            {
                // Debugging lines compiled in: drop the >>D marker and keep the statement text.
                MarkProgram(number, raw, lead + 1, lead + 3, CoverageClass.Directive, "debugging-line-marker");
                AppendCode(number, raw[(lead + 3)..], lead + 4, false);
                return;
            }

            if (StartsCommentEntry(trimmed))
            {
                _inCommentEntry = true;
            }
            else if (EndsCommentEntryInFreeFormat(trimmed))
            {
                _inCommentEntry = false;
            }

            AppendCode(number, raw, 1, false);
        }

        // AUTHOR/INSTALLATION/... paragraphs hold free text where a lone apostrophe ("O'Grady") is not a literal.
        private static bool StartsCommentEntry(string trimmed)
            => CommentEntryParagraphs.Any(p => StartsWithWord(trimmed, p));

        private static bool EndsCommentEntryInFreeFormat(string trimmed)
            => StartsWithWord(trimmed, "PROGRAM-ID") || StartsWithWord(trimmed, "ENVIRONMENT") || StartsWithWord(trimmed, "DATA")
                || StartsWithWord(trimmed, "PROCEDURE") || StartsWithWord(trimmed, "IDENTIFICATION") || StartsWithWord(trimmed, "ID")
                || StartsWithWord(trimmed, "END");

        private void HandleDirective(int number, string raw, bool hadTabs, string directiveText)
        {
            AddLine(number, raw, SourceLineKind.Directive, hadTabs, "compiler-directive");
            var newFormat = SourceFormatDirective(directiveText);
            if (newFormat is not null)
            {
                _format = newFormat.Value;
            }
        }

        private void AppendCode(int number, string content, int startColumn, bool continuation)
        {
            var fixedArea = _format == SourceFormat.Fixed;
            if (continuation && !_hasCode)
            {
                Diag("continuation-without-previous-line", DiagnosticSeverity.Warning, "Continuation line has no preceding code line", number, 7);
                continuation = false;
            }

            var pos = 0;
            if (continuation)
            {
                var first = FirstNonBlank(content);
                if (first < 0)
                {
                    return;
                }

                if (first < 4)
                {
                    Diag("continuation-in-area-a", DiagnosticSeverity.Warning, "Continued text must start in Area B", number, startColumn + first);
                }

                if (_openQuote != '\0')
                {
                    if (content[first] is '\'' or '"')
                    {
                        if (content[first] != _openQuote)
                        {
                            Diag("continuation-quote-mismatch", DiagnosticSeverity.Warning, "Continuation line resumes the literal with a different quote character", number, startColumn + first);
                        }

                        // The continued literal runs through column 72 of the previous line, including trailing blanks.
                        if (_lastCodeFormat == SourceFormat.Fixed && _lastCodeEndColumn < ProgramAreaEnd)
                        {
                            _text.Append(' ', ProgramAreaEnd - _lastCodeEndColumn);
                        }

                        _marks.Add(new ReaderMark(CoverageClass.Skipped, "continuation-quote", ReaderArea.Program, number, startColumn + first, startColumn + first, 1));
                        pos = first + 1;
                    }
                    else
                    {
                        Diag("continuation-missing-quote", DiagnosticSeverity.Error, "A continued literal must resume with a quote on the continuation line", number, startColumn + first);
                        _openQuote = '\0';
                        pos = first;
                    }
                }
                else
                {
                    pos = first;
                }
            }
            else
            {
                if (_hasCode)
                {
                    if (_openQuote != '\0')
                    {
                        Diag("unterminated-literal", DiagnosticSeverity.Error, "Alphanumeric literal is not closed and the next line is not a continuation line", _lastCodeLine, _lastCodeEndColumn);
                        _openQuote = '\0';
                    }

                    _text.Append('\n');
                }
            }

            var quote = _inCommentEntry ? '\0' : _openQuote;
            var commentAt = -1;
            for (var i = pos; i < content.Length; i++)
            {
                var c = content[i];
                if (_inCommentEntry)
                {
                    if (c == '*' && i + 1 < content.Length && content[i + 1] == '>' && (i == 0 || content[i - 1] == ' '))
                    {
                        commentAt = i;
                        break;
                    }
                }
                else if (quote != '\0')
                {
                    if (c == quote)
                    {
                        if (i + 1 < content.Length && content[i + 1] == quote)
                        {
                            i++;
                        }
                        else
                        {
                            quote = '\0';
                        }
                    }
                }
                else if (c is '\'' or '"')
                {
                    quote = c;
                }
                else if (c == '*' && i + 1 < content.Length && content[i + 1] == '>' && (i == 0 || content[i - 1] == ' '))
                {
                    commentAt = i;
                    break;
                }
            }

            var end = commentAt >= 0 ? commentAt : content.Length;
            if (commentAt >= 0)
            {
                var chars = CountNonBlank(content, commentAt, content.Length);
                _marks.Add(new ReaderMark(CoverageClass.Comment, "floating-comment", ReaderArea.Program, number, startColumn + commentAt, startColumn + content.Length - 1, chars));
            }

            var appendEnd = end;
            if (quote == '\0')
            {
                while (appendEnd > pos && char.IsWhiteSpace(content[appendEnd - 1]))
                {
                    appendEnd--;
                }
            }

            if (appendEnd > pos)
            {
                _runs.Add((_text.Length, number, startColumn + pos, appendEnd - pos));
                _text.Append(content, pos, appendEnd - pos);
            }

            _openQuote = quote;
            _hasCode = true;
            _lastCodeLine = number;
            _lastCodeFormat = fixedArea ? SourceFormat.Fixed : SourceFormat.Free;
            _lastCodeEndColumn = startColumn + Math.Max(appendEnd, pos) - 1;
        }

        private void AddLine(int number, string raw, SourceLineKind kind, bool hadTabs, string? reason = null)
        {
            _lines.Add(new SourceLine(number, raw, _format, kind, hadTabs, reason));
            if (hadTabs)
            {
                _tabLines++;
                _firstTabLine = _firstTabLine == 0 ? number : _firstTabLine;
            }
        }

        private void MarkProgram(int number, string raw, int fromColumn, int toColumn, CoverageClass cls, string reason)
        {
            var end = Math.Min(raw.Length, toColumn);
            var chars = CountNonBlank(raw, fromColumn - 1, end);
            if (chars > 0)
            {
                _marks.Add(new ReaderMark(cls, reason, ReaderArea.Program, number, fromColumn, end, chars));
            }
        }

        private void MarkOutside(int number, string raw, int fromColumn, int toColumn, ReaderArea area, string reason)
        {
            var end = Math.Min(raw.Length, toColumn);
            var chars = CountNonBlank(raw, fromColumn - 1, end);
            if (chars > 0)
            {
                _marks.Add(new ReaderMark(CoverageClass.Skipped, reason, area, number, fromColumn, end, chars));
            }
        }

        private void Diag(string code, DiagnosticSeverity severity, string message, int line, int column, string shape = "")
        {
            var range = new SourceRange(_file.Path, line, Math.Max(1, column), line, Math.Max(1, column));
            _diagnostics.Add(Diagnostic.Create(code, severity, message, range, shape));
        }

        private static bool IsCblProcess(string raw)
        {
            var t = raw.TrimStart();
            if (StartsWithWord(t, "CBL") || StartsWithWord(t, "PROCESS"))
            {
                return true;
            }

            // A sequence number may precede CBL/PROCESS, which then starts in column 8 or later.
            if (raw.Length > 7 && IsSequenceNumber(raw))
            {
                var rest = raw[7..].TrimStart();
                return StartsWithWord(rest, "CBL") || StartsWithWord(rest, "PROCESS");
            }

            return false;
        }

        private static bool IsSequenceNumber(string raw)
        {
            for (var i = 0; i < 6 && i < raw.Length; i++)
            {
                if (!char.IsDigit(raw[i]) && raw[i] != ' ')
                {
                    return false;
                }
            }

            return raw.Length < 7 || raw[6] == ' ';
        }

        private static bool IsListingDirective(string trimmed)
        {
            foreach (var word in new[] { "EJECT", "SKIP1", "SKIP2", "SKIP3" })
            {
                if (StartsWithWord(trimmed, word))
                {
                    var rest = trimmed[word.Length..].Trim();
                    return rest.Length == 0 || rest == ".";
                }
            }

            if (StartsWithWord(trimmed, "TITLE"))
            {
                var rest = trimmed[5..].TrimStart();
                return rest.Length > 0 && rest[0] is '\'' or '"';
            }

            return false;
        }

        private static bool IsBlank(string raw, int fromColumn, int toColumn)
        {
            var end = Math.Min(raw.Length, toColumn);
            for (var i = fromColumn - 1; i < end; i++)
            {
                if (!char.IsWhiteSpace(raw[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static int FirstNonBlank(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                if (!char.IsWhiteSpace(s[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int CountNonBlank(string s, int from, int toExclusive)
        {
            var n = 0;
            for (var i = Math.Max(0, from); i < toExclusive && i < s.Length; i++)
            {
                if (!char.IsWhiteSpace(s[i]))
                {
                    n++;
                }
            }

            return n;
        }
    }
}
