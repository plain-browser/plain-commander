using System.Globalization;

namespace PlainCommander.Core.Sorting;

/// <summary>
/// Orders "file2" before "file10", like Explorer does, and ignores case.
/// Culture-aware for letters; digit runs compare numerically.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new(CultureInfo.CurrentCulture);

    private readonly CompareInfo _compare;

    public NaturalStringComparer(CultureInfo culture)
    {
        _compare = culture.CompareInfo;
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var xs = i;
                var ys = j;
                while (i < x.Length && char.IsDigit(x[i])) { i++; }
                while (j < y.Length && char.IsDigit(y[j])) { j++; }

                var xn = x.AsSpan(xs, i - xs).TrimStart('0');
                var yn = y.AsSpan(ys, j - ys).TrimStart('0');
                if (xn.Length != yn.Length)
                {
                    return xn.Length.CompareTo(yn.Length);
                }

                var c = xn.SequenceCompareTo(yn);
                if (c != 0)
                {
                    return c;
                }
            }
            else
            {
                var xe = i;
                var ye = j;
                while (xe < x.Length && !char.IsDigit(x[xe])) { xe++; }
                while (ye < y.Length && !char.IsDigit(y[ye])) { ye++; }

                var c = _compare.Compare(x.AsSpan(i, xe - i), y.AsSpan(j, ye - j), CompareOptions.IgnoreCase);
                if (c != 0)
                {
                    return c;
                }

                i = xe;
                j = ye;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
