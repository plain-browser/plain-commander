namespace PlainCommander.Core.Operations;

public sealed record DirectorySizeResult(long Bytes, int Files, int Directories);

public static class DirectorySize
{
    /// <summary>Space on a folder: total size of everything below it, not entering links.</summary>
    public static Task<DirectorySizeResult> CalculateAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                var (bytes, files, dirs) = TreeScanner.Measure(path, cancellationToken);
                return new DirectorySizeResult(bytes, files, dirs);
            },
            cancellationToken);
}
