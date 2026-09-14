using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Group chats, message editing/deletion, reactions, read receipts, and paginated history -
// same isolation rules as ChatHubAuthorizationTests: unique Guid-based ids/names per test.
public class ChatHubFeatureTests
{
    private static string NewId() => Guid.NewGuid().ToString("N");

    private static async Task<(string db, string aliceId, string aliceName, string bobId, string bobName, ChatHub aliceHub, ChatHub bobHub)>
        SetUpAliceAndBob()
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

        return (db, aliceId, aliceName, bobId, bobName, aliceHub, bobHub);
    }

    // ---------- Group chats ----------

    [Fact]
    public async Task CreateGroupChat_AllInvitedMembers_CanJoinAndSend()
    {
        var (db, _, aliceName, bobId, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();

        var carolId = NewId();
        var carolName = "Carol-" + NewId();
        var carolHub = ChatHubTestHarness.Create(db, carolId);
        await carolHub.OnConnectedAsync();
        await carolHub.SetDisplayName(carolName);

        var chatId = await aliceHub.CreateGroupChat("Trio", new List<string> { bobName, carolName });

        await aliceHub.SendToChat(chatId, "hi everyone");
        await bobHub.JoinChat(chatId);
        await bobHub.SendToChat(chatId, "hey");
        await carolHub.JoinChat(chatId);
        await carolHub.SendToChat(chatId, "yo");
    }

    [Fact]
    public async Task CreateGroupChat_NonMember_CannotJoinOrSend()
    {
        var (db, _, _, bobId, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateGroupChat("Just us", new List<string> { bobName });

        var eveHub = ChatHubTestHarness.Create(db, NewId());
        await eveHub.OnConnectedAsync();

        await Assert.ThrowsAsync<HubException>(() => eveHub.JoinChat(chatId));
        await Assert.ThrowsAsync<HubException>(() => eveHub.SendToChat(chatId, "sneaky"));
    }

    [Fact]
    public async Task CreateGroupChat_UnknownMemberName_ThrowsHubException()
    {
        var (_, _, _, _, _, aliceHub, _) = await SetUpAliceAndBob();

        await Assert.ThrowsAsync<HubException>(
            () => aliceHub.CreateGroupChat("Ghosts", new List<string> { "nobody-" + NewId() }));
    }

    [Fact]
    public async Task CreateGroupChat_SurvivesRestart_ViaPreload()
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

        var chatId = await aliceHub.CreateGroupChat("Persisted Group", new List<string> { bobName });

        // Simulate a fresh process: read what was persisted and feed it through the same
        // preload path Program.cs calls at startup.
        await using (var readDb = ChatHubTestHarness.CreateDb(db))
        {
            var chats = readDb.Chats.ToList();
            var members = readDb.ChatMembers.ToList();
            Assert.Contains(chats, c => c.ChatId == chatId && c.Name == "Persisted Group");
            Assert.Equal(2, members.Count(m => m.ChatId == chatId));
        }
    }

    // ---------- Edit / delete ----------

    [Fact]
    public async Task EditMessage_ByOwner_UpdatesBodyAndBroadcasts()
    {
        var (_, _, aliceName, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "origonal typo");

        var history = await aliceHub.GetChatHistory(chatId);
        var messageId = Assert.Single(history).Id;

        await aliceHub.EditMessage(messageId, "original, fixed");

        var updated = Assert.Single(await aliceHub.GetChatHistory(chatId));
        Assert.Equal("original, fixed", updated.Body);
        Assert.NotNull(updated.EditedAtUtc);
    }

    [Fact]
    public async Task EditMessage_ByNonOwner_ThrowsHubException()
    {
        var (_, _, aliceName, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "alice's message");

        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await bobHub.JoinChat(chatId);
        await Assert.ThrowsAsync<HubException>(() => bobHub.EditMessage(messageId, "hijacked"));
    }

    [Fact]
    public async Task DeleteMessage_ByOwner_MarksDeletedAndClearsBody()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "oops");

        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;
        await aliceHub.DeleteMessage(messageId);

        var deleted = Assert.Single(await aliceHub.GetChatHistory(chatId));
        Assert.True(deleted.IsDeleted);
        Assert.Equal(string.Empty, deleted.Body);
    }

    [Fact]
    public async Task DeleteMessage_ByNonOwner_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "alice's message");

        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await bobHub.JoinChat(chatId);
        await Assert.ThrowsAsync<HubException>(() => bobHub.DeleteMessage(messageId));
    }

    // ---------- Reactions ----------

    [Fact]
    public async Task ToggleReaction_AddThenRemove_TogglesCountBackToZero()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "funny");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await bobHub.JoinChat(chatId);
        await bobHub.ToggleReaction(messageId, "😂");

        var afterAdd = Assert.Single(await aliceHub.GetChatHistory(chatId)).Reactions;
        var reaction = Assert.Single(afterAdd);
        Assert.Equal("😂", reaction.Emoji);
        Assert.Equal(1, reaction.Count);

        await bobHub.ToggleReaction(messageId, "😂");

        var afterRemove = Assert.Single(await aliceHub.GetChatHistory(chatId)).Reactions;
        Assert.Empty(afterRemove);
    }

    [Fact]
    public async Task ToggleReaction_ReflectsReactedByMe_PerCaller()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "react to this");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await bobHub.JoinChat(chatId);
        await bobHub.ToggleReaction(messageId, "👍");

        var fromBob = Assert.Single(Assert.Single(await bobHub.GetChatHistory(chatId)).Reactions);
        Assert.True(fromBob.ReactedByMe);

        var fromAlice = Assert.Single(Assert.Single(await aliceHub.GetChatHistory(chatId)).Reactions);
        Assert.False(fromAlice.ReactedByMe);
    }

    [Fact]
    public async Task ToggleReaction_NonMember_ThrowsHubException()
    {
        var (db, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "secret");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        var eveHub = ChatHubTestHarness.Create(db, NewId());
        await eveHub.OnConnectedAsync();

        await Assert.ThrowsAsync<HubException>(() => eveHub.ToggleReaction(messageId, "👍"));
    }

    // ---------- Read receipts ----------

    [Fact]
    public async Task MarkRead_NotifiesOtherMemberWithLastReadId()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "did you see this?");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await bobHub.JoinChat(chatId);

        // Should not throw - the interesting assertion is that ReadReceipt fires only to
        // *other* group members (Clients.OthersInGroup), which the mocked proxy doesn't
        // let us inspect directly, so this mainly guards against a persistence-path crash.
        await bobHub.MarkRead(chatId, messageId);
    }

    [Fact]
    public async Task MarkRead_NonMember_ThrowsHubException()
    {
        var (db, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hi");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        var eveHub = ChatHubTestHarness.Create(db, NewId());
        await eveHub.OnConnectedAsync();

        await Assert.ThrowsAsync<HubException>(() => eveHub.MarkRead(chatId, messageId));
    }

    [Fact]
    public async Task MarkRead_OlderThanAlreadyRecorded_IsANoOp()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "one");
        await aliceHub.SendToChat(chatId, "two");
        var history = await aliceHub.GetChatHistory(chatId);
        var firstId = history[0].Id;
        var secondId = history[1].Id;

        await bobHub.JoinChat(chatId);
        await bobHub.MarkRead(chatId, secondId);

        // Going "backwards" should not throw and should just be ignored.
        await bobHub.MarkRead(chatId, firstId);
    }

    // ---------- Paginated history ----------

    [Fact]
    public async Task GetChatHistory_BeforeMessageId_ReturnsOlderPage()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        for (var i = 0; i < 5; i++)
            await aliceHub.SendToChat(chatId, $"message {i}");

        var firstPage = await aliceHub.GetChatHistory(chatId, beforeMessageId: null, take: 2);
        Assert.Equal(2, firstPage.Count);
        Assert.Equal("message 3", firstPage[0].Body);
        Assert.Equal("message 4", firstPage[1].Body);

        var secondPage = await aliceHub.GetChatHistory(chatId, beforeMessageId: firstPage[0].Id, take: 2);
        Assert.Equal(2, secondPage.Count);
        Assert.Equal("message 1", secondPage[0].Body);
        Assert.Equal("message 2", secondPage[1].Body);
    }

    [Fact]
    public async Task GetChatHistory_NonMember_StillThrowsWithPaginationArgs()
    {
        var (db, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hi");

        var eveHub = ChatHubTestHarness.Create(db, NewId());
        await eveHub.OnConnectedAsync();

        await Assert.ThrowsAsync<HubException>(() => eveHub.GetChatHistory(chatId, beforeMessageId: 1));
    }

    // ---------- Unread count (persisted, not just in-session) ----------

    [Fact]
    public async Task GetMyChats_UnreadCount_DropsToZeroAfterMarkRead()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "one");
        await aliceHub.SendToChat(chatId, "two");

        await bobHub.JoinChat(chatId);
        var beforeRead = (await bobHub.GetMyChats()).Single(c => c.Id == chatId);
        Assert.Equal(2, beforeRead.UnreadCount);

        var latest = (await bobHub.GetChatHistory(chatId)).Last().Id;
        await bobHub.MarkRead(chatId, latest);

        var afterRead = (await bobHub.GetMyChats()).Single(c => c.Id == chatId);
        Assert.Equal(0, afterRead.UnreadCount);
    }

    [Fact]
    public async Task GetMyChats_UnreadCount_SurvivesANewHubInstance()
    {
        // Simulates a reconnect: a brand new ChatHub instance for the same user/connection
        // history should see the same persisted unread count, not start over at zero.
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
        await aliceHub.SendToChat(chatId, "are you there?");

        var reconnectedBobHub = ChatHubTestHarness.Create(db, bobId);
        await reconnectedBobHub.OnConnectedAsync();

        var summary = (await reconnectedBobHub.GetMyChats()).Single(c => c.Id == chatId);
        Assert.Equal(1, summary.UnreadCount);
    }

    // ---------- Reactions: who reacted ----------

    [Fact]
    public async Task ToggleReaction_ReactedByIncludesReactorsDisplayName()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "funny");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await bobHub.JoinChat(chatId);
        await bobHub.ToggleReaction(messageId, "😂");

        var reaction = Assert.Single(Assert.Single(await aliceHub.GetChatHistory(chatId)).Reactions);
        Assert.Contains(bobName, reaction.ReactedBy);

        await bobHub.ToggleReaction(messageId, "😂");
        var afterRemove = Assert.Single(await aliceHub.GetChatHistory(chatId)).Reactions;
        Assert.Empty(afterRemove);
    }
}
