using AwesomeAssertions;
using PlainCommander.Core.Formatting;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class PluralTests
{
    [Theory]
    [InlineData(0, "plików")]
    [InlineData(1, "plik")]
    [InlineData(2, "pliki")]
    [InlineData(4, "pliki")]
    [InlineData(5, "plików")]
    [InlineData(12, "plików")]
    [InlineData(14, "plików")]
    [InlineData(22, "pliki")]
    [InlineData(104, "pliki")]
    [InlineData(112, "plików")]
    public void Polish_forms(long count, string expected)
    {
        Plural.Polish(count, "plik", "pliki", "plików").Should().Be(expected);
    }
}
