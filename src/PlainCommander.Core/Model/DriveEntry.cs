namespace PlainCommander.Core.Model;

public sealed record DriveEntry(
    string Root,
    string Label,
    DriveType Type,
    long TotalBytes,
    long FreeBytes,
    bool IsReady,
    string? CloudName = null);
