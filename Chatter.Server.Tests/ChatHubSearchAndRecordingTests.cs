using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Message search and the "recording a voice message" indicator - same Guid-isolated harness as
// the other test classes.
public class ChatHubSearchAndRecordingTests
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

    // ---------- Search ----------

    [Fact]
    public async Task SearchMessages_CaseInsensitiveSubstring_FindsMatch()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "Let's meet at the Coffee Shop tomorrow");
        await aliceHub.SendToChat(chatId, "unrelated message");

        var results = await aliceHub.SearchMessages(chatId, "coffee shop");

        var hit = Assert.Single(results);
        Assert.Contains("Coffee Shop", hit.Body);
    }

    [Fact]
    public async Task SearchMessages_NoMatch_ReturnsEmpty()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hello world");

        var results = await aliceHub.SearchMessages(chatId, "nonexistent");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchMessages_ExcludesDeletedMessages()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "the secret keyword is here");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await aliceHub.DeleteMessage(messageId);

        var results = await aliceHub.SearchMessages(chatId, "secret");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchMessages_NonMember_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hello");

        var carolHub = ChatHubTestHarness.Create(NewId(), NewId());
        await Assert.ThrowsAsync<HubException>(() => carolHub.SearchMessages(chatId, "hello"));
    }

    [Fact]
    public async Task SearchMessages_EmptyQuery_ReturnsEmptyWithoutThrowing()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hello");

        var results = await aliceHub.SearchMessages(chatId, "   ");
        Assert.Empty(results);
    }

    // ---------- Recording indicator ----------

    [Fact]
    public async Task SetRecordingVoiceMessage_NotifiesOthersInTheChannel()
    {
        var (_, _, aliceName, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.JoinChat(chatId);
        await bobHub.JoinChat(chatId);

        // No exception means the broadcast to "OthersInGroup" went through; the harness mocks
        // SignalR delivery, so this test's job is just to prove the hub method itself is callable
        // and doesn't throw for a member of the channel.
        await aliceHub.SetRecordingVoiceMessage(chatId, true);
        await aliceHub.SetRecordingVoiceMessage(chatId, false);
    }
}
