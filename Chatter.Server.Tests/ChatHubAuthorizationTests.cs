using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Covers the guarantees the auth/identity rewrite is supposed to provide: a caller can only
// join or post to a chat it's actually a member of, and identity comes from the connection's
// authenticated user id, not from anything a caller can pass as a parameter.
//
// ChatHub keeps its state in static dictionaries shared by the whole process, so every test
// here uses fresh Guid-based user ids/display names to avoid colliding with other tests.
public class ChatHubAuthorizationTests
{
    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task JoinChat_Lobby_IsAlwaysAllowed()
    {
        var db = NewId();
        var hub = ChatHubTestHarness.Create(db, NewId());

        await hub.OnConnectedAsync();

        // Should not throw.
        await hub.JoinChat("Lobby");
    }

    [Fact]
    public async Task JoinChat_DmYouAreNotAMemberOf_ThrowsHubException()
    {
        var db = NewId();
        var hub = ChatHubTestHarness.Create(db, NewId());
        await hub.OnConnectedAsync();

        // Nobody has created this chat, so the caller isn't a member of it.
        var guessedChatId = $"dm:{NewId()}|{NewId()}";

        await Assert.ThrowsAsync<HubException>(() => hub.JoinChat(guessedChatId));
    }

    [Fact]
    public async Task SendToChat_DmYouAreNotAMemberOf_ThrowsHubException()
    {
        var db = NewId();
        var hub = ChatHubTestHarness.Create(db, NewId());
        await hub.OnConnectedAsync();

        var guessedChatId = $"dm:{NewId()}|{NewId()}";

        await Assert.ThrowsAsync<HubException>(() => hub.SendToChat(guessedChatId, "hi"));
    }

    [Fact]
    public async Task CreateDm_ThenBothParticipants_CanJoinAndSend()
    {
        var db = NewId();
        var aliceId = NewId();
        var aliceName = "Alice-" + NewId();
        var bobId = NewId();
        var bobName = "Bob-" + NewId();

        // Bob has to have connected at least once so his display name is resolvable.
        var bobHub = ChatHubTestHarness.Create(db, bobId);
        await bobHub.OnConnectedAsync();
        await bobHub.SetDisplayName(bobName);

        var aliceHub = ChatHubTestHarness.Create(db, aliceId);
        await aliceHub.OnConnectedAsync();
        await aliceHub.SetDisplayName(aliceName);

        var chatId = await aliceHub.CreateDm(bobName);

        // Both the creator and the invited participant should now be able to join/post.
        await aliceHub.JoinChat(chatId);
        await aliceHub.SendToChat(chatId, "hello bob");

        var bobHub2 = ChatHubTestHarness.Create(db, bobId);
        await bobHub2.OnConnectedAsync();
        await bobHub2.JoinChat(chatId);
        await bobHub2.SendToChat(chatId, "hi alice");
    }

    [Fact]
    public async Task CreateDm_WithUnknownDisplayName_ThrowsHubException()
    {
        var db = NewId();
        var hub = ChatHubTestHarness.Create(db, NewId());
        await hub.OnConnectedAsync();

        await Assert.ThrowsAsync<HubException>(() => hub.CreateDm("someone-who-never-connected-" + NewId()));
    }

    [Fact]
    public async Task ThirdParty_CannotJoinSomeoneElsesDm()
    {
        var db = NewId();
        var aliceId = NewId();
        var aliceName = "Alice-" + NewId();
        var bobId = NewId();
        var bobName = "Bob-" + NewId();
        var eveId = NewId();

        var bobHub = ChatHubTestHarness.Create(db, bobId);
        await bobHub.OnConnectedAsync();
        await bobHub.SetDisplayName(bobName);

        var aliceHub = ChatHubTestHarness.Create(db, aliceId);
        await aliceHub.OnConnectedAsync();
        await aliceHub.SetDisplayName(aliceName);

        var chatId = await aliceHub.CreateDm(bobName);

        // Eve knows (or guessed) the chat id but was never added as a participant.
        var eveHub = ChatHubTestHarness.Create(db, eveId);
        await eveHub.OnConnectedAsync();

        await Assert.ThrowsAsync<HubException>(() => eveHub.JoinChat(chatId));
        await Assert.ThrowsAsync<HubException>(() => eveHub.SendToChat(chatId, "sneaky"));
    }

    [Fact]
    public async Task ClaimingSomeonesDisplayName_DoesNotGrantAccessToTheirExistingDms()
    {
        var db = NewId();
        var aliceId = NewId();
        var sharedName = "Shared-" + NewId();
        var bobId = NewId();
        var bobName = "Bob-" + NewId();
        var malloryId = NewId();

        var bobHub = ChatHubTestHarness.Create(db, bobId);
        await bobHub.OnConnectedAsync();
        await bobHub.SetDisplayName(bobName);

        var aliceHub = ChatHubTestHarness.Create(db, aliceId);
        await aliceHub.OnConnectedAsync();
        await aliceHub.SetDisplayName(sharedName);

        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "just between us");

        // Mallory later claims the exact same display name Alice used - but she's a distinct
        // authenticated user (distinct "sub" claim), so this must not retroactively give her
        // access to a DM that was created against Alice's user id.
        var malloryHub = ChatHubTestHarness.Create(db, malloryId);
        await malloryHub.OnConnectedAsync();
        await malloryHub.SetDisplayName(sharedName);

        await Assert.ThrowsAsync<HubException>(() => malloryHub.JoinChat(chatId));
        await Assert.ThrowsAsync<HubException>(() => malloryHub.GetChatHistory(chatId));
    }

    [Fact]
    public async Task GetChatHistory_NonMember_ThrowsHubException()
    {
        var db = NewId();
        var hub = ChatHubTestHarness.Create(db, NewId());
        await hub.OnConnectedAsync();

        var guessedChatId = $"dm:{NewId()}|{NewId()}";

        await Assert.ThrowsAsync<HubException>(() => hub.GetChatHistory(guessedChatId));
    }

    [Fact]
    public async Task SendToChat_BeyondTheRateLimit_ThrowsHubException()
    {
        var db = NewId();
        var aliceId = NewId();
        var aliceName = "Alice-" + NewId();
        var bobId = NewId();
        var bobName = "Bob-" + NewId();

        var bobHub = ChatHubTestHarness.Create(db, bobId);
        await bobHub.OnConnectedAsync();
        await bobHub.SetDisplayName(bobName);

        var aliceHub = ChatHubTestHarness.Create(db, aliceId);
        await aliceHub.OnConnectedAsync();
        await aliceHub.SetDisplayName(aliceName);

        var chatId = await aliceHub.CreateDm(bobName);

        // SendToChat currently allows 10 messages per 10-second window per connection.
        for (var i = 0; i < 10; i++)
            await aliceHub.SendToChat(chatId, $"message {i}");

        await Assert.ThrowsAsync<HubException>(() => aliceHub.SendToChat(chatId, "one too many"));
    }

    [Fact]
    public async Task SendToChat_ThenGetChatHistory_ReturnsThePersistedMessage()
    {
        var db = NewId();
        var aliceId = NewId();
        var aliceName = "Alice-" + NewId();
        var bobId = NewId();
        var bobName = "Bob-" + NewId();

        var bobHub = ChatHubTestHarness.Create(db, bobId);
        await bobHub.OnConnectedAsync();
        await bobHub.SetDisplayName(bobName);

        var aliceHub = ChatHubTestHarness.Create(db, aliceId);
        await aliceHub.OnConnectedAsync();
        await aliceHub.SetDisplayName(aliceName);

        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "persisted message");

        var history = await aliceHub.GetChatHistory(chatId);

        Assert.Contains(history, m => m.Sender == aliceName && m.Body == "persisted message");
    }
}
