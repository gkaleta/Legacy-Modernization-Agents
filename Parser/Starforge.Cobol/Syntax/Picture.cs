namespace Starforge.Cobol.Syntax;

public enum PictureCategory
{
    Alphabetic,
    Alphanumeric,
    AlphanumericEdited,
    Numeric,
    NumericEdited,
    ExternalFloatingPoint,
    National,
    NationalEdited,
    Dbcs,
    Utf8,
    Boolean,
    Invalid,
}

// Size counts character positions (CR/DB take two, S/V/P none); bytes per position depend on USAGE and are applied by the layout.
public sealed record PictureInfo(
    string Text,
    PictureCategory Category,
    int Size,
    int Digits,
    int Scale,
    bool Signed,
    IReadOnlyList<string> Errors)
{
    public bool IsNumeric => Category == PictureCategory.Numeric;
}

public static class PictureAnalyzer
{
    private const char CreditSymbol = '\u0001';
    private const char DebitSymbol = '\u0002';
    private const char CurrencySymbol = '\u0003';

    public static PictureInfo Analyze(string text, bool decimalPointIsComma = false, IReadOnlyCollection<char>? currencySymbols = null)
    {
        var errors = new List<string>();
        var symbols = Expand(text, currencySymbols, errors);
        var decimalChar = decimalPointIsComma ? ',' : '.';

        int Count(char c) => symbols.Count(s => s == c);
        bool Has(char c) => symbols.Contains(c);

        var nines = Count('9');
        var ps = Count('P');
        var s = Count('S');
        var v = Count('V');
        var editing = symbols.Any(c => c is 'B' or '0' or '/' or ',' or '.' or '+' or '-' or 'Z' or '*' or CreditSymbol or DebitSymbol or CurrencySymbol);
        var numericEditing = symbols.Any(c => c is ',' or '.' or '+' or '-' or 'Z' or '*' or CreditSymbol or DebitSymbol or CurrencySymbol);

        if (s > 1)
        {
            errors.Add("S may appear only once");
        }

        if (s == 1 && symbols[0] != 'S')
        {
            errors.Add("S must be the leftmost symbol");
        }

        if (v > 1)
        {
            errors.Add("V may appear only once");
        }

        PictureCategory category;
        if (Has('E'))
        {
            category = PictureCategory.ExternalFloatingPoint;
        }
        else if (Has('N'))
        {
            category = symbols.All(c => c == 'N') ? PictureCategory.National
                : symbols.All(c => c is 'N' or 'B' or '0' or '/') ? PictureCategory.NationalEdited
                : PictureCategory.Invalid;
        }
        else if (Has('G'))
        {
            category = symbols.All(c => c is 'G' or 'B') ? PictureCategory.Dbcs : PictureCategory.Invalid;
        }
        else if (Has('U'))
        {
            category = symbols.All(c => c == 'U') ? PictureCategory.Utf8 : PictureCategory.Invalid;
        }
        else if (Has('1'))
        {
            category = symbols.All(c => c == '1') ? PictureCategory.Boolean : PictureCategory.Invalid;
        }
        else if (Has('X') || Has('A'))
        {
            if (symbols.All(c => c is 'A' or 'B') && Has('A') && !Has('B'))
            {
                category = PictureCategory.Alphabetic;
            }
            else if (symbols.All(c => c is 'A' or 'X' or '9'))
            {
                category = PictureCategory.Alphanumeric;
            }
            else if (symbols.All(c => c is 'A' or 'X' or '9' or 'B' or '0' or '/'))
            {
                category = Has('A') && !Has('X') && !Has('9') && !Has('0') && !Has('/') ? PictureCategory.Alphabetic : PictureCategory.AlphanumericEdited;
            }
            else
            {
                category = PictureCategory.Invalid;
            }
        }
        else if (!editing)
        {
            category = symbols.All(c => c is '9' or 'S' or 'V' or 'P') && nines + ps > 0 ? PictureCategory.Numeric : PictureCategory.Invalid;
        }
        else
        {
            category = s == 0 && symbols.All(c => c is '9' or 'V' or 'P' or 'B' or '0' or '/' or ',' or '.' or '+' or '-' or 'Z' or '*' or CreditSymbol or DebitSymbol or CurrencySymbol)
                ? PictureCategory.NumericEdited
                : PictureCategory.Invalid;
        }

        if (category == PictureCategory.Invalid && errors.Count == 0)
        {
            errors.Add("symbols of different categories cannot be combined");
        }

        var size = 0;
        foreach (var c in symbols)
        {
            size += c switch
            {
                'S' or 'V' or 'P' => 0,
                CreditSymbol or DebitSymbol => 2,
                _ => 1,
            };
        }

        int digits;
        int scale;
        bool signed;
        if (category == PictureCategory.NumericEdited)
        {
            digits = NumericEditedDigits(symbols);
            scale = DigitsAfter(symbols, symbols.FindIndex(c => c == decimalChar || c == 'V'));
            signed = symbols.Any(c => c is '+' or '-' or CreditSymbol or DebitSymbol);
        }
        else if (category == PictureCategory.ExternalFloatingPoint)
        {
            var e = symbols.IndexOf('E');
            var mantissa = symbols.Take(e).ToList();
            digits = mantissa.Count(c => c == '9');
            scale = DigitsAfter(mantissa, mantissa.FindIndex(c => c == decimalChar || c == 'V'));
            signed = true;
        }
        else
        {
            digits = nines;
            scale = category == PictureCategory.Numeric ? NumericScale(symbols) : 0;
            signed = s > 0;
        }

        if (category == PictureCategory.Numeric && nines + ps > 31)
        {
            errors.Add($"numeric item has {nines + ps} digit positions; the maximum is 31");
        }

        if (!editing && numericEditing)
        {
            errors.Add("internal: editing classification mismatch");
        }

        return new PictureInfo(text, category, size, digits, scale, signed, errors);
    }

    // Decimal point sits at V, else left of leading P's, else at the right end; P's left of the point scale the value up.
    private static int NumericScale(List<char> symbols)
    {
        var digitSymbols = symbols.Where(c => c is '9' or 'P' or 'V').ToList();
        var point = digitSymbols.IndexOf('V');
        if (point < 0)
        {
            point = digitSymbols.Count > 0 && digitSymbols[0] == 'P' ? -1 : digitSymbols.Count;
        }

        var scale = 0;
        for (var i = 0; i < digitSymbols.Count; i++)
        {
            var c = digitSymbols[i];
            if (c == 'V')
            {
                continue;
            }

            if (i > point)
            {
                scale++;
            }
            else if (c == 'P')
            {
                scale--;
            }
        }

        return scale;
    }

    private static int DigitsAfter(List<char> symbols, int point)
    {
        if (point < 0)
        {
            return 0;
        }

        return symbols.Skip(point + 1).Count(c => c is '9' or 'Z' or '*' or 'P' or '+' or '-' or CurrencySymbol);
    }

    // A floating insertion string (+++, $$$) reserves its first symbol for the sign or currency sign.
    private static int NumericEditedDigits(List<char> symbols)
    {
        var digits = symbols.Count(c => c is '9' or 'Z' or '*' or 'P');
        foreach (var floating in new[] { '+', '-', CurrencySymbol })
        {
            var n = symbols.Count(c => c == floating);
            if (n > 1)
            {
                digits += n - 1;
            }
        }

        return digits;
    }

    private static List<char> Expand(string text, IReadOnlyCollection<char>? currencySymbols, List<string> errors)
    {
        var result = new List<char>(text.Length + 8);
        var upper = text.ToUpperInvariant();
        var i = 0;
        while (i < upper.Length)
        {
            var c = upper[i];
            if (c == '(')
            {
                var close = upper.IndexOf(')', i + 1);
                if (close < 0)
                {
                    errors.Add("unbalanced parenthesis");
                    break;
                }

                var countText = upper.AsSpan(i + 1, close - i - 1);
                if (result.Count == 0 || !int.TryParse(countText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) || count <= 0)
                {
                    errors.Add($"invalid repetition factor '({countText})'");
                }
                else
                {
                    var last = result[^1];
                    for (var k = 1; k < count; k++)
                    {
                        result.Add(last);
                    }
                }

                i = close + 1;
                continue;
            }

            if ((c == 'C' && At(upper, i + 1) == 'R') || (c == 'D' && At(upper, i + 1) == 'B'))
            {
                result.Add(c == 'C' ? CreditSymbol : DebitSymbol);
                i += 2;
                continue;
            }

            if (c == '$' || (currencySymbols?.Contains(c) ?? false))
            {
                result.Add(CurrencySymbol);
                i++;
                continue;
            }

            if (c is 'A' or 'B' or 'E' or 'G' or 'N' or 'P' or 'S' or 'U' or 'V' or 'X' or 'Z' or '9' or '0' or '1' or '/' or ',' or '.' or '+' or '-' or '*')
            {
                result.Add(c);
                i++;
                continue;
            }

            errors.Add($"invalid picture symbol '{text[i]}'");
            i++;
        }

        return result;
    }

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';
}
