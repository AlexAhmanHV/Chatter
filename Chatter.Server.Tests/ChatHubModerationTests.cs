using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Blocking/muting and group admin (add/remove member, rename) - same Guid-isolated harness as
// the other test classes.
public class ChatHubModerationTests
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

    // ---------- Blocking ----------

    [Fact]
    public async Task BlockUser_ThenCreateDm_ThrowsHubExceptionBothDirections()
    {
        var (_, _, aliceName, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();

        await aliceHub.BlockUser(bobName);

        await Assert.ThrowsAsync<HubException>(() => aliceHub.CreateDm(bobName));
        await Assert.ThrowsAsync<HubException>(() => bobHub.CreateDm(aliceName));
    }

    [Fact]
    public async Task BlockUser_AfterDmExists_StopsFurtherMessages()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "before block");

        await aliceHub.BlockUser(bobName);

        await Assert.ThrowsAsync<HubException>(() => aliceHub.SendToChat(chatId, "after block"));
        await Assert.ThrowsAsync<HubException>(() => bobHub.SendToChat(chatId, "bob tries too"));
    }

    [Fact]
    public async Task UnblockUser_RestoresAbilityToDm()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();

        await aliceHub.BlockUser(bobName);
        await Assert.ThrowsAsync<HubException>(() => aliceHub.CreateDm(bobName));

        await aliceHub.UnblockUser(bobName);
        var chatId = await aliceHub.CreateDm(bobName); // should not throw
        Assert.NotNull(chatId);
    }

    [Fact]
    public async Task GetBlockedUsers_ReflectsCurrentBlockList()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();

        Assert.Empty(await aliceHub.GetBlockedUsers());

        await aliceHub.BlockUser(bobName);
        Assert.Contains(bobName, await aliceHub.GetBlockedUsers());

        await aliceHub.UnblockUser(bobName);
        Assert.Empty(await aliceHub.GetBlockedUsers());
    }

    [Fact]
    public async Task BlockUser_UnknownName_ThrowsHubException()
    {
        var (_, _, _, _, _, aliceHub, _) = await SetUpAliceAndBob();
        await Assert.ThrowsAsync<HubException>(() => aliceHub.BlockUser("nobody-" + NewId()));
    }

    // ---------- Muting ----------

    [Fact]
    public async Task SetChatMuted_ExcludesChatFromUnreadCountLogicButNotDelivery()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        await bobHub.JoinChat(chatId);
        await bobHub.SetChatMuted(chatId, true);

        await aliceHub.SendToChat(chatId, "still delivered even though muted");

        // Muting doesn't block delivery/persistence - the message is still there.
        var history = await bobHub.GetChatHistory(chatId);
        Assert.Contains(history, m => m.Body == "still delivered even though muted");

        var summary = (await bobHub.GetMyChats()).Single(c => c.Id == chatId);
        Assert.True(summary.IsMuted);
    }

    [Fact]
    public async Task SetChatMuted_NonMember_ThrowsHubException()
    {
        var (db, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var eveHub = ChatHubTestHarness.Create(db, NewId());
        await eveHub.OnConnectedAsync();

        await Assert.ThrowsAsync<HubException>(() => eveHub.SetChatMuted(chatId, true));
    }

    // ---------- Group admin ----------

    [Fact]
    public async Task AddGroupMember_ByCreator_GrantsAccess()
    {
        var (db, _, _, bobId, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();

        var carolId = NewId();
        var carolName = "Carol-" + NewId();
        var carolHub = ChatHubTestHarness.Create(db, carolId);
        await carolHub.OnConnectedAsync();
        await carolHub.SetDisplayName(carolName);

        var chatId = await aliceHub.CreateGroupChat("Duo", new List<string> { bobName });

        await Assert.ThrowsAsync<HubException>(() => carolHub.JoinChat(chatId));

        await aliceHub.AddGroupMember(chatId, carolName);

        await carolHub.JoinChat(chatId); // should not throw now
        await carolHub.SendToChat(chatId, "hi, just joined");
    }

    [Fact]
    public async Task AddGroupMember_ByNonCreator_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateGroupChat("Duo", new List<string> { bobName });

        await bobHub.JoinChat(chatId);
        await Assert.ThrowsAsync<HubException>(() => bobHub.AddGroupMember(chatId, "someone-" + NewId()));
    }

    [Fact]
    public async Task RemoveGroupMember_ByCreator_RevokesAccess()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateGroupChat("Duo", new List<string> { bobName });
        await bobHub.JoinChat(chatId);

        await aliceHub.RemoveGroupMember(chatId, bobName);

        await Assert.ThrowsAsync<HubException>(() => bobHub.SendToChat(chatId, "can I still talk?"));
    }

    [Fact]
    public async Task RemoveGroupMember_TheCreatorThemselves_ThrowsHubException()
    {
        var (_, aliceId, aliceName, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateGroupChat("Duo", new List<string> { bobName });

        await Assert.ThrowsAsync<HubException>(() => aliceHub.RemoveGroupMember(chatId, aliceName));
    }

    [Fact]
    public async Task RenameGroupChat_ByCreator_UpdatesLabelForAllMembers()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateGroupChat("Old Name", new List<string> { bobName });

        await aliceHub.RenameGroupChat(chatId, "New Name");

        var aliceSummary = (await aliceHub.GetMyChats()).Single(c => c.Id == chatId);
        var bobSummary = (await bobHub.GetMyChats()).Single(c => c.Id == chatId);
        Assert.Equal("New Name", aliceSummary.Label);
        Assert.Equal("New Name", bobSummary.Label);
    }

    [Fact]
    public async Task RenameGroupChat_ByNonCreator_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateGroupChat("Duo", new List<string> { bobName });
        await bobHub.JoinChat(chatId);

        await Assert.ThrowsAsync<HubException>(() => bobHub.RenameGroupChat(chatId, "Hijacked"));
    }

    [Fact]
    public async Task CreatorLeavingGroup_LosesAdminRights()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateGroupChat("Duo", new List<string> { bobName });

        await aliceHub.LeaveChat(chatId);

        await Assert.ThrowsAsync<HubException>(() => aliceHub.RenameGroupChat(chatId, "Still mine?"));
    }

    [Fact]
    public async Task GetGroupMembers_ReturnsAllCurrentMembers()
    {
        var (_, _, aliceName, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateGroupChat("Duo", new List<string> { bobName });

        var members = await aliceHub.GetGroupMembers(chatId);
        Assert.Contains(aliceName, members);
        Assert.Contains(bobName, members);
    }
}
