using AwesomeAssertions;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Remote;

public sealed class RemoteConnectionsTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private static Task<IReadOnlyList<RemoteItem>> List(FolderServer server) =>
        server.Connections.UseAsync(FolderServer.Root, c => c.ListAsync("/", CancellationToken.None), CancellationToken.None);

    [Fact]
    public async Task Asks_for_the_password_once_and_remembers_it_when_asked_to()
    {
        await using var server = new FolderServer(_box.Root);
        server.Secrets.Values.Clear();
        server.Prompts.Answers.Enqueue(new CredentialAnswer(FolderServer.Password, Remember: true));

        await List(server);
        await List(server);

        server.Prompts.CredentialProblems.Should().Equal([null]);
        server.Secrets.Values.Should().ContainKey(RemoteConnections.SecretKey(FolderServer.Root)).WhoseValue.Should().Be(FolderServer.Password);
        server.Connections.IsOpen(FolderServer.Root).Should().BeTrue();
    }

    [Fact]
    public async Task A_wrong_password_is_asked_again_with_the_reason_and_not_stored()
    {
        await using var server = new FolderServer(_box.Root);
        server.Secrets.Values.Clear();
        server.Prompts.Answers.Enqueue(new CredentialAnswer("wrong", Remember: true));
        server.Prompts.Answers.Enqueue(new CredentialAnswer(FolderServer.Password, Remember: false));

        await List(server);

        server.Prompts.CredentialProblems.Should().HaveCount(2);
        server.Prompts.CredentialProblems[1].Should().Contain("Permission denied");
        server.Secrets.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stored_password_that_stopped_working_is_asked_for_and_forgotten()
    {
        await using var server = new FolderServer(_box.Root);
        server.Secrets.Values[RemoteConnections.SecretKey(FolderServer.Root)] = "old";
        server.Prompts.Answers.Enqueue(new CredentialAnswer(FolderServer.Password, Remember: false));

        await List(server);

        server.Prompts.CredentialProblems.Should().ContainSingle().Which.Should().NotBeNull();
        server.Secrets.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelling_the_password_dialog_fails_the_listing_with_a_cancelled_login()
    {
        await using var server = new FolderServer(_box.Root);
        server.Secrets.Values.Clear();

        var act = () => List(server);

        (await act.Should().ThrowAsync<RemoteException>()).Which.IsCancelled.Should().BeTrue();
        server.Connections.IsOpen(FolderServer.Root).Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_host_keys_are_asked_once_when_trusted_always()
    {
        await using var server = new FolderServer(_box.Root);

        await List(server);
        await server.Connections.DisconnectAsync(FolderServer.Root);
        await List(server);

        server.Prompts.HostQuestions.Should().ContainSingle().Which.Known.Should().BeNull();
        server.Trusted.Should().ContainKey("test.local:22").WhoseValue.Should().Be("SHA256:abc");
    }

    [Fact]
    public async Task Trusting_this_time_only_covers_the_session_but_is_not_saved()
    {
        await using var server = new FolderServer(_box.Root);
        server.Prompts.Trust = TrustChoice.Once;

        await List(server);
        var copy = await server.Connections.OpenAsync(FolderServer.Root, CancellationToken.None);
        await copy.DisposeAsync();

        server.Prompts.HostQuestions.Should().ContainSingle("the operation's own connection is covered by the first answer");
        server.Trusted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_changed_host_key_is_shown_with_the_old_one_and_can_be_rejected()
    {
        await using var server = new FolderServer(_box.Root, fingerprint: "SHA256:new");
        server.Trusted["test.local:22"] = "SHA256:old";
        server.Prompts.Trust = TrustChoice.Reject;

        var act = () => List(server);

        (await act.Should().ThrowAsync<RemoteException>()).Which.IsUntrusted.Should().BeTrue();
        server.Prompts.HostQuestions.Should().ContainSingle().Which.Known.Should().Be("SHA256:old");
        server.Trusted["test.local:22"].Should().Be("SHA256:old");
    }

    [Fact]
    public async Task A_dropped_browsing_connection_is_reopened_transparently()
    {
        await using var server = new FolderServer(_box.Root);
        _box.File("a.txt");
        await List(server);

        server.DropNextCalls = 1;
        var items = await List(server);

        items.Select(i => i.Name).Should().Equal("a.txt");
    }

    [Fact]
    public async Task Home_is_asked_once_per_login()
    {
        await using var server = new FolderServer(_box.Root);

        (await server.Connections.HomeAsync(FolderServer.Root, CancellationToken.None)).Should().Be("/home");
    }
}
