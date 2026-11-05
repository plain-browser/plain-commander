using System.Globalization;
using AwesomeAssertions;
using PlainCommander.Core.Sorting;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class NaturalStringComparerTests
{
    private readonly NaturalStringComparer _cmp = new(CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("file2", "file10")]
    [InlineData("a", "b")]
    [InlineData("abc", "abcd")]
    [InlineData("img007", "img8")]
    [InlineData("alpha2", "Zeta")]
    public void Orders_numbers_numerically(string first, string second)
    {
        _cmp.Compare(first, second).Should().BeNegative();
        _cmp.Compare(second, first).Should().BePositive();
    }

    [Fact]
    public void Ignores_case()
    {
        _cmp.Compare("README", "readme").Should().Be(0);
    }

    [Fact]
    public void Sorts_like_explorer()
    {
        var input = new[] { "file10.txt", "file2.txt", "File1.txt", "file1a.txt" };
        var sorted = input.Order(_cmp).ToArray();
        sorted.Should().Equal("File1.txt", "file1a.txt", "file2.txt", "file10.txt");
    }
}
