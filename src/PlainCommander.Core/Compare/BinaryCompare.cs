namespace PlainCommander.Core.Compare;

/// <summary>Byte-by-byte comparison of two files that are not text.</summary>
public static class BinaryCompare
{
    /// <summary>The offset of the first byte that differs (or where the shorter file ends); null when the files are equal.</summary>
    public static async Task<long?> FirstDifferenceAsync(string left, string right, CancellationToken token = default)
    {
        await using var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bufferA = new byte[1 << 16];
        var bufferB = new byte[1 << 16];
        long offset = 0;
        while (true)
        {
            var readA = await a.ReadAtLeastAsync(bufferA, bufferA.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
            var readB = await b.ReadAtLeastAsync(bufferB, bufferB.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
            var common = Math.Min(readA, readB);
            var mismatch = bufferA.AsSpan(0, common).CommonPrefixLength(bufferB.AsSpan(0, common));
            if (mismatch < common || readA != readB)
            {
                return offset + mismatch;
            }

            if (readA == 0)
            {
                return null;
            }

            offset += readA;
        }
    }
}
