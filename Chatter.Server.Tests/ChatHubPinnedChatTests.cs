using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Pinning a chat to the top of your own chat list - same Guid-isolated harness as the other test
// classes.
public class ChatHubPinnedChatTests
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

    [Fact]
    public async Task SetChatPinned_True_MarksItPinnedInGetMyChats()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        await aliceHub.SetChatPinned(chatId, true);

        var chats = await aliceHub.GetMyChats();
        Assert.True(chats.Single(c => c.Id == chatId).IsPinned);
    }

    [Fact]
    public async Task SetChatPinned_PinnedChatsSortAboveUnpinnedOnes()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var dmWithBob = await aliceHub.CreateDm(bobName);
        var groupChat = await aliceHub.CreateGroupChat("Extra", new List<string> { bobName });

        await aliceHub.SetChatPinned(groupChat, true);

        var chats = await aliceHub.GetMyChats();
        var pinnedIndex = chats.FindIndex(c => c.Id == groupChat);
        var unpinnedIndex = chats.FindIndex(c => c.Id == dmWithBob);

        Assert.True(pinnedIndex < unpinnedIndex, "Pinned chat should sort above an unpinned one.");
    }

    [Fact]
    public async Task SetChatPinned_LobbyStaysFirstEvenWhenAnotherChatIsPinned()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SetChatPinned(chatId, true);

        var chats = await aliceHub.GetMyChats();
        Assert.Equal("Lobby", chats[0].Id);
    }

    [Fact]
    public async Task SetChatPinned_False_UnpinsIt()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SetChatPinned(chatId, true);

        await aliceHub.SetChatPinned(chatId, false);

        var chats = await aliceHub.GetMyChats();
        Assert.False(chats.Single(c => c.Id == chatId).IsPinned);
    }

    [Fact]
    public async Task SetChatPinned_IsPerUser_DoesNotAffectTheOtherParticipant()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        await aliceHub.SetChatPinned(chatId, true);

        var bobChats = await bobHub.GetMyChats();
        Assert.False(bobChats.Single(c => c.Id == chatId).IsPinned);
    }

    [Fact]
    public async Task SetChatPinned_NonMember_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var outsiderHub = ChatHubTestHarness.Create(NewId(), NewId());
        await Assert.ThrowsAsync<HubException>(() => outsiderHub.SetChatPinned(chatId, true));
    }
}
