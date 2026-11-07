using System.Globalization;
using System.Reflection;
using AwesomeAssertions;
using Xunit;

namespace PlainCommander.Presentation.Tests.Resources;

/// <summary>
/// Calls every member of <see cref="Texts"/> in English and in Polish with sample arguments and checks the
/// result is a finished sentence: no exception, no leftover format braces, and a real translation.
/// Spacing is not checked: status lines pad their parts with several spaces on purpose.
/// </summary>
public sealed class TextsTests
{
    private static readonly int[] Counts = [0, 1, 2, 5, 22, 112];

    public static TheoryData<string> Members()
    {
        var data = new TheoryData<string>();
        foreach (var name in Callables().Select(m => m.Name).Distinct().Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void Every_text_is_finished_in_both_languages(string member)
    {
        foreach (var method in Callables().Where(m => m.Name == member))
        {
            foreach (var args in Samples(method))
            {
                var english = Call(method, args, "en-US");
                var polish = Call(method, args, "pl-PL");
                var shown = $"{member}({string.Join(", ", args.Select(a => a?.ToString() ?? "null"))})";
                foreach (var (lang, text) in new[] { ("en", english), ("pl", polish) })
                {
                    if (text is not string s)
                    {
                        continue;
                    }

                    s.Should().NotContainAny(["{0", "{1", "{2", "{{", "}}"], $"{shown} in {lang} must not show format codes");
                    s.Should().NotContain("plik.txtplik.txt", $"{shown} in {lang} glues two values together");
                }

                if (english is string en && polish is string pl && args.Length == 0 && en.Any(char.IsLetter))
                {
                    pl.Should().NotBe(en, $"{shown} is not translated");
                }
            }
        }
    }

    [Theory]
    [InlineData(1, 2, 5, 12, 22, 25)]
    public void Polish_plurals_follow_the_number(params int[] numbers)
    {
        // 1 plik, 2 pliki, 5 plików, 12 plików, 22 pliki, 25 plików.
        var texts = numbers.Select(n => Call(typeof(Texts).GetMethod(nameof(Texts.Summary))!, [n, 0L, n], "pl-PL") as string).ToList();
        texts[0].Should().StartWith("1 plik ");
        texts[1].Should().StartWith("2 pliki ");
        texts[2].Should().StartWith("5 plików ");
        texts[3].Should().StartWith("12 plików ");
        texts[4].Should().StartWith("22 pliki ");
        texts[5].Should().StartWith("25 plików ");
    }

    private static IEnumerable<MethodInfo> Callables() =>
        typeof(Texts).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsSpecialName || m.Name.StartsWith("get_", StringComparison.Ordinal))
            .Where(m => m.ReturnType == typeof(string))
            .Where(m => m.GetParameters().All(p => CanMake(p.ParameterType)));

    private static bool CanMake(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(string) || type == typeof(bool) || type.IsEnum;

    private static IEnumerable<object?[]> Samples(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            yield return [];
            yield break;
        }

        // One call per sample count (all numbers the same), plus every enum value and both bools.
        foreach (var n in Counts)
        {
            yield return parameters.Select(p => Make(p.ParameterType, n, 0)).ToArray();
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            var variants = type.IsEnum ? Enum.GetValues(type).Cast<object>().ToArray() : type == typeof(bool) ? [true, false] : [];
            foreach (var variant in variants)
            {
                var args = parameters.Select(p => Make(p.ParameterType, 2, 0)).ToArray();
                args[i] = variant;
                yield return args;
            }
        }
    }

    private static object? Make(Type type, int n, int _) =>
        type == typeof(int) ? n
        : type == typeof(long) ? (long)n * 1024
        : type == typeof(double) ? n / 10.0
        : type == typeof(string) ? "plik.txt"
        : type == typeof(bool) ? n % 2 == 1
        : type.IsEnum ? Enum.GetValues(type).GetValue(0)
        : null;

    private static object? Call(MethodInfo method, object?[] args, string culture)
    {
        var (ui, current) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            return method.Invoke(null, args);
        }
        catch (TargetInvocationException ex)
        {
            throw new InvalidOperationException($"{method.Name} threw in {culture}", ex.InnerException);
        }
        finally
        {
            (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = (ui, current);
        }
    }
}
