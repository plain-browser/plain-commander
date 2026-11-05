using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace PlainCommander.Core.Crypto;

/// <summary>Password → 32-byte key, with the parameters the header carries. The password bytes are wiped after use.</summary>
internal static class PasswordKdf
{
    public static byte[] Derive(string password, PceHeader header)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            switch (header.Kdf)
            {
                case PceKdf.Argon2id:
                    if (header.KdfCost is < 8 or > 4 * 1024 * 1024 || header.KdfTime == 0 || header.KdfParallelism == 0)
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
                    if (header.KdfCost is < 1000 or > 50_000_000)
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
