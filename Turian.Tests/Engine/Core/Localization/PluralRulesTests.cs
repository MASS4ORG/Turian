namespace Turian.Tests;

/// <summary>Tests for the CLDR cardinal plural rules.</summary>
public class PluralRulesTests
{
    /// <summary>Each locale family maps counts to its CLDR category.</summary>
    [Theory]
    [InlineData("en", 1, PluralCategory.One)]
    [InlineData("en", 0, PluralCategory.Other)]
    [InlineData("en", 2, PluralCategory.Other)]
    [InlineData("pt-BR", 1, PluralCategory.One)]
    [InlineData("de_DE", -1, PluralCategory.One)]
    [InlineData(null, 1, PluralCategory.One)]
    [InlineData("xx", 1, PluralCategory.One)]
    [InlineData("ja", 1, PluralCategory.Other)]
    [InlineData("TR", 5, PluralCategory.Other)]
    [InlineData("fr", 0, PluralCategory.One)]
    [InlineData("fr", 1, PluralCategory.One)]
    [InlineData("fr", 2, PluralCategory.Other)]
    [InlineData("ru", 1, PluralCategory.One)]
    [InlineData("ru", 21, PluralCategory.One)]
    [InlineData("ru", 11, PluralCategory.Many)]
    [InlineData("ru", 3, PluralCategory.Few)]
    [InlineData("ru", 22, PluralCategory.Few)]
    [InlineData("ru", 12, PluralCategory.Many)]
    [InlineData("uk", 5, PluralCategory.Many)]
    [InlineData("be", 0, PluralCategory.Many)]
    [InlineData("pl", 1, PluralCategory.One)]
    [InlineData("pl", 21, PluralCategory.Many)]
    [InlineData("pl", 24, PluralCategory.Few)]
    [InlineData("pl", 14, PluralCategory.Many)]
    [InlineData("cs", 1, PluralCategory.One)]
    [InlineData("sk", 4, PluralCategory.Few)]
    [InlineData("cs", 5, PluralCategory.Other)]
    [InlineData("ar", 0, PluralCategory.Zero)]
    [InlineData("ar", 1, PluralCategory.One)]
    [InlineData("ar", 2, PluralCategory.Two)]
    [InlineData("ar", 103, PluralCategory.Few)]
    [InlineData("ar", 11, PluralCategory.Many)]
    [InlineData("ar", 100, PluralCategory.Other)]
    [InlineData("he", 1, PluralCategory.One)]
    [InlineData("he", 2, PluralCategory.Two)]
    [InlineData("he", 3, PluralCategory.Other)]
    public void GetCategory_Integers(string? locale, int value, PluralCategory expected) =>
        Assert.Equal(expected, PluralRules.GetCategory(locale, value));

    /// <summary>Fractional counts fall into <see cref="PluralCategory.Other"/> wherever the rule requires an integer.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("pl")]
    [InlineData("cs")]
    [InlineData("ar")]
    [InlineData("he")]
    public void GetCategory_FractionIsOther(string locale) =>
        Assert.Equal(PluralCategory.Other, PluralRules.GetCategory(locale, 1.5m));
}
