using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Tests;

public sealed class PictureTests
{
    [Theory]
    [InlineData("X(10)", PictureCategory.Alphanumeric, 10, 0, 0, false)]
    [InlineData("A(3)", PictureCategory.Alphabetic, 3, 0, 0, false)]
    [InlineData("S9(7)V99", PictureCategory.Numeric, 9, 9, 2, true)]
    [InlineData("9(3)PPP", PictureCategory.Numeric, 3, 3, -3, false)]
    [InlineData("SVPP99", PictureCategory.Numeric, 2, 2, 4, true)]
    [InlineData("PPP99", PictureCategory.Numeric, 2, 2, 5, false)]
    [InlineData("ZZ,ZZ9.99", PictureCategory.NumericEdited, 9, 7, 2, false)]
    [InlineData("$$$,$$9.99CR", PictureCategory.NumericEdited, 12, 7, 2, true)]
    [InlineData("-9(5)", PictureCategory.NumericEdited, 6, 5, 0, true)]
    [InlineData("99/99/99", PictureCategory.NumericEdited, 8, 6, 0, false)]
    [InlineData("XX/XX", PictureCategory.AlphanumericEdited, 5, 0, 0, false)]
    [InlineData("XXBXX", PictureCategory.AlphanumericEdited, 5, 0, 0, false)]
    [InlineData("N(4)", PictureCategory.National, 4, 0, 0, false)]
    [InlineData("G(5)", PictureCategory.Dbcs, 5, 0, 0, false)]
    [InlineData("+9.99E+99", PictureCategory.ExternalFloatingPoint, 9, 3, 2, true)]
    public void AnalysesCategorySizeDigitsAndScale(string text, PictureCategory category, int size, int digits, int scale, bool signed)
    {
        var info = PictureAnalyzer.Analyze(text);

        info.Errors.Should().BeEmpty();
        info.Category.Should().Be(category);
        info.Size.Should().Be(size);
        info.Digits.Should().Be(digits);
        info.Scale.Should().Be(scale);
        info.Signed.Should().Be(signed);
    }

    [Fact]
    public void DecimalPointIsCommaSwapsPeriodAndComma()
    {
        var info = PictureAnalyzer.Analyze("ZZ.ZZ9,99", decimalPointIsComma: true);

        info.Category.Should().Be(PictureCategory.NumericEdited);
        info.Digits.Should().Be(7);
        info.Scale.Should().Be(2);
    }

    [Fact]
    public void CustomCurrencySymbolIsAccepted()
    {
        var info = PictureAnalyzer.Analyze("EEE9.99", currencySymbols: new[] { 'E' });

        info.Errors.Should().BeEmpty();
        info.Category.Should().Be(PictureCategory.NumericEdited);
        info.Size.Should().Be(7);
    }

    [Theory]
    [InlineData("9S99")]
    [InlineData("9V9V9")]
    [InlineData("X(2")]
    [InlineData("9(32)")]
    [InlineData("X?X")]
    public void InvalidPicturesReportErrors(string text)
    {
        PictureAnalyzer.Analyze(text).Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void MixedCategoriesAreInvalid()
    {
        var info = PictureAnalyzer.Analyze("XZ9");

        info.Category.Should().Be(PictureCategory.Invalid);
        info.Errors.Should().Equal("symbols of different categories cannot be combined");
    }
}
