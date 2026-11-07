using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace PlainCommander.Core.Crypto;

/// <summary>Password → 32-byte key, with the parameters the header carries. The password bytes are wiped after use.</summary>
internal static class PasswordKdf
{
    // The header is not authenticated before the key exists, so its cost is capped: well above what the app
    // writes (64 MiB, 3 passes, 4 lanes; 600 000 iterations), low enough that a crafted file cannot hang it.
    internal const uint MaxArgon2MemoryKiB = 1024 * 1024;
    internal const byte MaxArgon2Time = 16;
    internal const byte MaxArgon2Parallelism = 16;
    internal const uint MaxPbkdf2Iterations = 5_000_000;

    /// <summary>The key for a new file, or for a file whose header carries <see cref="PceLayout.NfcPassword"/>.</summary>
    public static byte[] Derive(string password, PceHeader header)
    {
        var bytes = (header.Flags & PceLayout.NfcPassword) != 0 ? Encode(password, NormalizationForm.FormC) : null;
        return DeriveBytes(bytes ?? Encoding.UTF8.GetBytes(password), header);
    }

    /// <summary>
    /// Derives keys for the spellings of <paramref name="password"/> the file may have been made with, one at a time,
    /// until <paramref name="accept"/> takes one (it opens block 0); returns that key, or null when none fits.
    /// A file with <see cref="PceLayout.NfcPassword"/> has a single spelling, NFC. An older file was keyed with the
    /// bytes as typed then, so the typed text goes first, then its NFC and NFD forms; a form that comes out as the
    /// same bytes as an earlier one is not derived again. Text that cannot be normalized (a lone surrogate) is used
    /// as typed. Rejected keys and every password buffer are wiped.
    /// </summary>
    public static byte[]? DeriveMatching(string password, PceHeader header, Func<byte[], bool> accept)
    {
        if ((header.Flags & PceLayout.NfcPassword) != 0)
        {
            return Offer(Derive(password, header), accept);
        }

        var tried = new List<byte[]>(3);
        try
        {
            foreach (var form in new NormalizationForm?[] { null, NormalizationForm.FormC, NormalizationForm.FormD })
            {
                var bytes = form is { } f ? Encode(password, f) : Encoding.UTF8.GetBytes(password);
                if (bytes is null)
                {
                    continue;
                }

                if (tried.Exists(t => t.AsSpan().SequenceEqual(bytes)))
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    continue;
                }

                tried.Add(bytes);
                if (Offer(DeriveBytes((byte[])bytes.Clone(), header), accept) is { } key)
                {
                    return key;
                }
            }

            return null;
        }
        finally
        {
            foreach (var bytes in tried)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    /// <summary>The key when <paramref name="accept"/> takes it; otherwise (or when it throws) the key is wiped.</summary>
    private static byte[]? Offer(byte[] key, Func<byte[], bool> accept)
    {
        var taken = false;
        try
        {
            taken = accept(key);
            return taken ? key : null;
        }
        finally
        {
            if (!taken)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    /// <summary>UTF-8 of the password in <paramref name="form"/>; null when it cannot be normalized (a lone surrogate).</summary>
    private static byte[]? Encode(string password, NormalizationForm form)
    {
        char[]? chars = null;
        try
        {
            var span = password.AsSpan();
            if (span.IsNormalized(form))
            {
                return Encoding.UTF8.GetBytes(password);
            }

            chars = new char[span.GetNormalizedLength(form)];
            return span.TryNormalize(chars, out var written, form) ? Encoding.UTF8.GetBytes(chars, 0, written) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            if (chars is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
            }
        }
    }

    /// <summary>Runs the header's key derivation over <paramref name="bytes"/>, then wipes them.</summary>
    private static byte[] DeriveBytes(byte[] bytes, PceHeader header)
    {
        try
        {
            switch (header.Kdf)
            {
                case PceKdf.Argon2id:
                    if (header.KdfCost < 8u * header.KdfParallelism || header.KdfCost > MaxArgon2MemoryKiB
                        || header.KdfTime is 0 or > MaxArgon2Time || header.KdfParallelism is 0 or > MaxArgon2Parallelism)
                    {
                        throw new PceFormatException("Unreasonable Argon2 parameters in the header.");
                    }

                    using (var argon = new Argon2id(bytes)
                    {
                        Salt = header.Salt,
                        MemorySize = (int)header.KdfCost,
                        Iterations = header.KdfTime,
                        DegreeOfParallelism = header.KdfParallelism,
                    })
                    {
                        return argon.GetBytes(PceFormat.KeySize);
                    }

                case PceKdf.Pbkdf2Sha512:
                    if (header.KdfCost is < 1000 or > MaxPbkdf2Iterations)
                    {
                        throw new PceFormatException("Unreasonable PBKDF2 iteration count in the header.");
                    }

                    return Rfc2898DeriveBytes.Pbkdf2(bytes, header.Salt, (int)header.KdfCost, HashAlgorithmName.SHA512, PceFormat.KeySize);
                default:
                    throw new PceFormatException("Unknown key derivation.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

/// <summary>One AEAD over the whole file: AES-256-GCM or ChaCha20-Poly1305, both with 12-byte nonces and 16-byte tags.</summary>
internal sealed class PceCipher : IDisposable
{
    private readonly AesGcm? _aes;
    private readonly ChaCha20Poly1305? _chacha;

    public PceCipher(PceCipherKind kind, byte[] key)
    {
        switch (kind)
        {
            case PceCipherKind.AesGcm:
                _aes = new AesGcm(key, PceFormat.TagSize);
                break;
            case PceCipherKind.ChaCha20Poly1305:
                if (!ChaCha20Poly1305.IsSupported)
                {
                    throw new PlatformNotSupportedException("ChaCha20-Poly1305 is not available on this system.");
                }

                _chacha = new ChaCha20Poly1305(key);
                break;
            default:
                throw new PceFormatException("Unknown cipher.");
        }
    }

    public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> aad)
    {
        if (_aes is not null)
        {
            _aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }
        else
        {
            _chacha!.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }
    }

    /// <summary>Throws <see cref="CryptographicException"/> (AuthenticationTagMismatchException) when the block does not authenticate.</summary>
    public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        if (_aes is not null)
        {
            _aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        else
        {
            _chacha!.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
    }

    public void Dispose()
    {
        _aes?.Dispose();
        _chacha?.Dispose();
    }
}
