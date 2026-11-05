namespace PlainCommander.Core.Network;

/// <summary>A folder a computer shares ("docs"), with its comment.</summary>
public sealed record NetworkShare(string Name, string Remark);

/// <summary>Finds computers and their shared folders, and logs on to a server when Windows asks for a password.</summary>
public interface INetworkBrowser
{
    /// <summary>Computer names seen on the local network (no backslashes). May take a few seconds.</summary>
    IReadOnlyList<string> Computers(CancellationToken cancellationToken);

    /// <summary>The disk shares of a server. Throws UnauthorizedAccessException when it needs a logon.</summary>
    IReadOnlyList<NetworkShare> Shares(string server, bool includeHidden);

    /// <summary>Asks for a user and password (the Windows dialog) and connects to <paramref name="remoteName"/>. False when cancelled.</summary>
    bool Logon(string remoteName);
}
