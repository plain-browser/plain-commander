namespace PlainCommander.Core.Remote;

/// <summary>Who the server claims to be: its SSH host key or its TLS certificate, as a SHA256 fingerprint.</summary>
/// <param name="Kind">"ssh-ed25519 256" for SSH, "TLS CN=…" for FTPS.</param>
public sealed record HostIdentity(string Host, int Port, string Kind, string Fingerprint)
{
    /// <summary>The key under which the fingerprint is remembered.</summary>
    public string Key => $"{Host}:{Port}";
}

public enum TrustChoice
{
    /// <summary>Connect and remember the fingerprint.</summary>
    Always,

    /// <summary>Connect this time only.</summary>
    Once,

    Reject,
}

public sealed record CredentialAnswer(string Secret, bool Remember);

/// <summary>Questions a connection asks the user. Called from background threads.</summary>
public interface IRemotePrompts
{
    /// <param name="known">The fingerprint remembered for this server when it differs (a changed key), else null.</param>
    Task<TrustChoice> ConfirmHostAsync(HostIdentity identity, string? known, CancellationToken cancellationToken);

    /// <summary>The password (or the key's passphrase when <paramref name="passphrase"/>); null when the user cancels.</summary>
    /// <param name="problem">Why it is asked again (wrong password), or null the first time.</param>
    Task<CredentialAnswer?> AskCredentialsAsync(RemoteSite site, bool passphrase, string? problem, CancellationToken cancellationToken);
}

/// <summary>Where remembered passwords live (the Windows Credential Manager in the app).</summary>
public interface ISecretStore
{
    string? Read(string key);

    void Write(string key, string secret);

    void Delete(string key);
}

/// <summary>Remembers nothing: for tests and when the credential store is unavailable.</summary>
public sealed class NoSecretStore : ISecretStore
{
    public string? Read(string key) => null;

    public void Write(string key, string secret)
    {
    }

    public void Delete(string key)
    {
    }
}
