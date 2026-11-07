using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Crypto;

/// <summary>
/// The same visible password in Unicode NFC (typed on Windows) and NFD (pasted from macOS) opens a .pce file:
/// new files carry <see cref="PceLayout.NfcPassword"/> and key from NFC; files from 1.13.x try the typed text,
/// then its NFC and NFD forms.
/// </summary>
public sealed class PcePasswordNormalizationTests : IDisposable
{
    private static readonly PceOptions Fast = PceOptions.Fast;
    private static readonly PceOptions Legacy = PceOptions.Fast with { LegacyPasswordForm = true };
    private static readonly string Nfc = "Zażółć".Normalize(NormalizationForm.FormC);
    private static readonly string Nfd = Nfc.Normalize(NormalizationForm.FormD);

    // Written by the 1.13.x code (before the flag existed), password "Zażółć" in NFC and in NFD; content "written by 1.13.x".
    private const string LegacyNfcFile = "UENFMQAAAAAAIAAAAQEJmNoUvIy/yNG+7j/byB10AAABAKrNRwHUL7wdJQAAADiReA2mhMovHFQHYmTvhOtioDJU8+jhTIFpCV4uQeUI8OydE0cM5Qt2yLfUdJ5mMhr0+SBHEQAAAO22DrqCN9RGe8nsaPZ0BIlshSE8Z4tLDNMT2g2VLMpWFQ==";
    private const string LegacyNfdFile = "UENFMQAAAAAAIAAAAQGXzCkSLXavVR5RNJc/nW1gAAABAKKaOqeYT8GuJQAAAJODAQEZbGtwNE5R0KBa3r/Du/D7Zs/tqgiXZtLUDU/BXK4E/GllYA/Qzlt3+1s+4TJXuShdEQAAAP0ZoZVNtczhXmPvWea2bWZwsS1yb2LLYKR3yMN+oz9fcg==";

    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Encrypted(string password, PceOptions options, string name = "n.txt")
    {
        var source = _s.P(name);
        File.WriteAllText(source, "secret " + name);
        PceFile.Encrypt(source, source + PceFormat.Extension, password, options, PceCompression.None, null, Token);
        return source + PceFormat.Extension;
    }

    private string Fixture(string base64, string name)
    {
        var path = _s.P(name);
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        return path;
    }

    private string DecryptedText(string pce, string password)
    {
        var back = _s.P("back-" + Guid.NewGuid().ToString("N"));
        PceFile.Decrypt(pce, password, back, null, Token);
        return File.ReadAllText(back);
    }

    private static Action Peeking(string pce, string password) => () => PceFile.Peek(pce, password);

    [Fact]
    public void The_two_spellings_differ()
    {
        Nfd.Should().NotBe(Nfc);
        Encoding.UTF8.GetBytes(Nfd).Should().NotEqual(Encoding.UTF8.GetBytes(Nfc));
    }

    [Fact]
    public void New_files_carry_the_nfc_flag_and_legacy_option_does_not()
    {
        PceFile.Peek(Encrypted(Nfc, Fast, "a.txt"), Nfc).Header.Flags.Should().HaveFlag(PceLayout.NfcPassword);
        PceFile.Peek(Encrypted(Nfc, Legacy, "b.txt"), Nfc).Header.Flags.Should().NotHaveFlag(PceLayout.NfcPassword);
        File.ReadAllBytes(Fixture(LegacyNfcFile, "f.pce"))[4].Should().Be(0, "1.13.x wrote no flags for a plain file");
    }

    [Fact]
    public void Nfc_encrypted_opens_with_nfd_and_nfd_encrypted_opens_with_nfc()
    {
        var fromNfc = Encrypted(Nfc, Fast, "c.txt");
        DecryptedText(fromNfc, Nfd).Should().Be("secret c.txt");
        DecryptedText(fromNfc, Nfc).Should().Be("secret c.txt");

        var fromNfd = Encrypted(Nfd, Fast, "d.txt");
        DecryptedText(fromNfd, Nfc).Should().Be("secret d.txt");
        DecryptedText(fromNfd, Nfd).Should().Be("secret d.txt");
    }

    [Fact]
    public void Legacy_files_from_1_13_open_with_either_spelling()
    {
        var nfc = Fixture(LegacyNfcFile, "legacy-nfc.txt.pce");
        var nfd = Fixture(LegacyNfdFile, "legacy-nfd.txt.pce");
        foreach (var file in new[] { nfc, nfd })
        {
            PceFile.Peek(file, Nfc).Header.Flags.Should().NotHaveFlag(PceLayout.NfcPassword);
            DecryptedText(file, Nfc).Should().Be("written by 1.13.x");
            DecryptedText(file, Nfd).Should().Be("written by 1.13.x");
        }
    }

    [Fact]
    public void Legacy_option_writes_files_that_open_with_either_spelling()
    {
        var nfd = Encrypted(Nfd, Legacy, "e.txt");
        DecryptedText(nfd, Nfd).Should().Be("secret e.txt");
        DecryptedText(nfd, Nfc).Should().Be("secret e.txt");
        var nfc = Encrypted(Nfc, Legacy, "f.txt");
        DecryptedText(nfc, Nfd).Should().Be("secret f.txt");
    }

    [Fact]
    public void A_wrong_password_still_fails_for_new_and_legacy_files()
    {
        foreach (var file in new[] { Encrypted(Nfc, Fast, "g.txt"), Fixture(LegacyNfdFile, "h.pce") })
        {
            Peeking(file, "Zazolc").Should().Throw<PceWrongPasswordException>();
            Peeking(file, Nfc + " ").Should().Throw<PceWrongPasswordException>();
            Peeking(file, Nfc.ToUpperInvariant()).Should().Throw<PceWrongPasswordException>();
        }
    }

    [Fact]
    public void Flipping_the_flag_bit_fails_authentication()
    {
        var fresh = Encrypted(Nfc, Fast, "i.txt");
        var bytes = File.ReadAllBytes(fresh);
        bytes[4] ^= (byte)PceLayout.NfcPassword;
        File.WriteAllBytes(fresh, bytes);
        Peeking(fresh, Nfc).Should().Throw<PceWrongPasswordException>("the header is the associated data of every block");
        Peeking(fresh, Nfd).Should().Throw<PceWrongPasswordException>();

        var legacy = Fixture(LegacyNfcFile, "j.pce");
        bytes = File.ReadAllBytes(legacy);
        bytes[4] ^= (byte)PceLayout.NfcPassword;
        File.WriteAllBytes(legacy, bytes);
        Peeking(legacy, Nfc).Should().Throw<PceWrongPasswordException>();
    }

    [Theory]
    [InlineData("a\uD800b")]
    [InlineData("\uDC00")]
    [InlineData("ż\uD83D")]
    public void Lone_surrogate_passwords_do_not_crash(string password)
    {
        var fresh = Encrypted(password, Fast, "k.txt");
        DecryptedText(fresh, password).Should().Be("secret k.txt");
        Peeking(fresh, "other").Should().Throw<PceWrongPasswordException>();

        var legacy = Encrypted(password, Legacy, "l.txt");
        DecryptedText(legacy, password).Should().Be("secret l.txt");
        Peeking(legacy, "other").Should().Throw<PceWrongPasswordException>();
    }

    [Theory]
    [InlineData("tajne", 1)] // ASCII: every form is the same bytes
    [InlineData("Zażółć", 2)] // NFC as typed, then NFD
    [InlineData("Zażółć", 3)] // mixed: as typed, NFC, NFD
    [InlineData("a\uD800b", 1)] // cannot be normalized: only as typed
    public void Legacy_files_derive_each_distinct_spelling_once(string password, int expected)
    {
        var header = PceFormat.NewHeader(Legacy, PceLayout.None);
        var seen = new List<byte[]>();
        PasswordKdf.DeriveMatching(password, header, key =>
        {
            seen.Add((byte[])key.Clone());
            return false;
        }).Should().BeNull();
        seen.Should().HaveCount(expected);
        seen.Select(Convert.ToHexString).Should().OnlyHaveUniqueItems();

        var flagged = PceFormat.NewHeader(Fast, PceLayout.None);
        var calls = 0;
        PasswordKdf.DeriveMatching(password, flagged, _ => ++calls > 0 && false).Should().BeNull();
        calls.Should().Be(1, "a flagged file has one spelling, NFC");
    }

    [Fact]
    public void Rejected_keys_are_wiped()
    {
        var header = PceFormat.NewHeader(Legacy, PceLayout.None);
        var keys = new List<byte[]>();
        PasswordKdf.DeriveMatching(Nfc, header, key =>
        {
            keys.Add(key);
            return false;
        });
        keys.Should().HaveCount(2).And.AllSatisfy(k => k.Should().OnlyContain(b => b == 0));
    }
}
