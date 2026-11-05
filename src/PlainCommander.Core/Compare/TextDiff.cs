using System.Text;
using PlainCommander.Core.Viewer;

namespace PlainCommander.Core.Compare;

public enum DiffKind
{
    Same,

    /// <summary>Only on the left (removed on the way to the right).</summary>
    Removed,

    /// <summary>Only on the right.</summary>
    Added,

    /// <summary>A left line and a right line that replace each other, shown side by side.</summary>
    Changed,
}

/// <summary>One row of a side-by-side diff; a missing side has a null number and text.</summary>
public sealed record DiffRow(DiffKind Kind, int? LeftNumber, string? LeftText, int? RightNumber, string? RightText);

public sealed record DiffResult(IReadOnlyList<DiffRow> Rows, int Added, int Removed, int Changed)
{
    public bool Identical => Added == 0 && Removed == 0 && Changed == 0;
}

/// <summary>
/// Line diff (Myers' O(ND) algorithm) for the side-by-side view after Shift+F2. Common head and tail are cut off
/// first; runs of removals followed by additions are paired up as changed lines.
/// </summary>
public static class TextDiff
{
    /// <summary>Above this many differing lines the diff stops: the files are simply too different to show line by line.</summary>
    public const int MaxEditDistance = 20_000;

    public const long MaxFileBytes = 16L * 1024 * 1024;

    public static DiffResult Compare(IReadOnlyList<string> left, IReadOnlyList<string> right, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var head = 0;
        while (head < left.Count && head < right.Count && left[head] == right[head])
        {
            head++;
        }

        var tail = 0;
        while (tail < left.Count - head && tail < right.Count - head && left[left.Count - 1 - tail] == right[right.Count - 1 - tail])
        {
            tail++;
        }

        var edits = Myers(left, head, left.Count - tail, right, head, right.Count - tail, token);

        var rows = new List<DiffRow>(left.Count + right.Count);
        for (var i = 0; i < head; i++)
        {
            rows.Add(new DiffRow(DiffKind.Same, i + 1, left[i], i + 1, right[i]));
        }

        int added = 0, removed = 0, changed = 0;
        var pendingRemoved = new List<int>();
        var pendingAdded = new List<int>();
        void Flush()
        {
            var pairs = Math.Min(pendingRemoved.Count, pendingAdded.Count);
            for (var k = 0; k < pairs; k++)
            {
                rows.Add(new DiffRow(DiffKind.Changed, pendingRemoved[k] + 1, left[pendingRemoved[k]], pendingAdded[k] + 1, right[pendingAdded[k]]));
            }

            foreach (var l in pendingRemoved.Skip(pairs))
            {
                rows.Add(new DiffRow(DiffKind.Removed, l + 1, left[l], null, null));
            }

            foreach (var r in pendingAdded.Skip(pairs))
            {
                rows.Add(new DiffRow(DiffKind.Added, null, null, r + 1, right[r]));
            }

            changed += pairs;
            removed += pendingRemoved.Count - pairs;
            added += pendingAdded.Count - pairs;
            pendingRemoved.Clear();
            pendingAdded.Clear();
        }

        foreach (var (kind, l, r) in edits)
        {
            switch (kind)
            {
                case DiffKind.Removed:
                    pendingRemoved.Add(l);
                    break;
                case DiffKind.Added:
                    pendingAdded.Add(r);
                    break;
                default:
                    Flush();
                    rows.Add(new DiffRow(DiffKind.Same, l + 1, left[l], r + 1, right[r]));
                    break;
            }
        }

        Flush();
        for (var i = 0; i < tail; i++)
        {
            var l = left.Count - tail + i;
            var r = right.Count - tail + i;
            rows.Add(new DiffRow(DiffKind.Same, l + 1, left[l], r + 1, right[r]));
        }

        return new DiffResult(rows, added, removed, changed);
    }

    /// <summary>Reads two text files with the viewer's encoding detection. Null when either looks binary or is too big.</summary>
    public static async Task<(IReadOnlyList<string> Left, IReadOnlyList<string> Right)?> ReadPairAsync(string leftPath, string rightPath, CancellationToken token = default)
    {
        var left = await ReadLinesAsync(leftPath, token).ConfigureAwait(false);
        var right = await ReadLinesAsync(rightPath, token).ConfigureAwait(false);
        return left is null || right is null ? null : (left, right);
    }

    private static async Task<IReadOnlyList<string>?> ReadLinesAsync(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        var sample = bytes.AsSpan(0, Math.Min(bytes.Length, TextContent.SampleBytes));
        if (TextDecoder.LooksBinary(sample))
        {
            return null;
        }

        var text = TextDecoder.Detect(sample).GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd('\r');
        }

        // A final newline does not make an extra empty line.
        return lines.Length > 0 && lines[^1].Length == 0 && text.EndsWith('\n') ? lines[..^1] : lines;
    }

    /// <summary>Edit script between a[aFrom..aTo) and b[bFrom..bTo): (kind, index in a, index in b).</summary>
    private static List<(DiffKind Kind, int L, int R)> Myers(IReadOnlyList<string> a, int aFrom, int aTo, IReadOnlyList<string> b, int bFrom, int bTo, CancellationToken token)
    {
        var n = aTo - aFrom;
        var m = bTo - bFrom;
        var max = n + m;
        var result = new List<(DiffKind, int, int)>();
        if (max == 0)
        {
            return result;
        }

        var offset = max + 1;
        var v = new int[(2 * max) + 3];
        var trace = new List<int[]>();
        var found = false;
        for (var d = 0; d <= max && !found; d++)
        {
            token.ThrowIfCancellationRequested();
            if (d > MaxEditDistance)
            {
                throw new InvalidOperationException("Too many differences for a line diff.");
            }

            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[aFrom + x] == b[bFrom + y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }

        // Walk the trace backwards to recover the path.
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var vd = trace[d];
            var k = cx - cy;
            var prevK = k == -d || (k != d && vd[offset + k - 1] < vd[offset + k + 1]) ? k + 1 : k - 1;
            var prevX = d == 0 ? 0 : vd[offset + prevK];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                result.Add((DiffKind.Same, aFrom + cx - 1, bFrom + cy - 1));
                cx--;
                cy--;
            }

            if (d > 0)
            {
                if (cx == prevX)
                {
                    result.Add((DiffKind.Added, aFrom + cx, bFrom + cy - 1));
                }
                else
                {
                    result.Add((DiffKind.Removed, aFrom + cx - 1, bFrom + cy));
                }
            }

            cx = prevX;
            cy = prevY;
        }

        result.Reverse();
        return result;
    }

    /// <summary>Text of a row for copy/paste or a plain-text dump.</summary>
    public static string Format(DiffRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var sb = new StringBuilder();
        sb.Append(row.Kind switch { DiffKind.Added => "+ ", DiffKind.Removed => "- ", DiffKind.Changed => "~ ", _ => "  " });
        sb.Append(row.LeftText ?? row.RightText);
        return sb.ToString();
    }
}
