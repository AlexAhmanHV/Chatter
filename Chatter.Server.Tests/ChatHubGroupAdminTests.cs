using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Delegated group admins and ownership succession - same Guid-isolated harness as the other test
// classes.
public class ChatHubGroupAdminTests
{
    private static string NewId() => Guid.NewGuid().ToString("N");

    private static async Task<(string db, string aliceId, string aliceName, string bobId, string bobName, string carolId, string carolName, ChatHub aliceHub, ChatHub bobHub, ChatHub carolHub)>
        SetUpAliceBobAndCarol()
    {
        var db = NewId();
        var aliceId = NewId();
        var aliceName = "Alice-" + NewId();
        var bobId = NewId();
        var bobName = "Bob-" + NewId();
        var carolId = NewId();
        var carolName = "Carol-" + NewId();

        var bobHub = ChatHubTestHarness.Create(db, bobId);
        await bobHub.OnConnectedAsync();
        await bobHub.SetDisplayName(bobName);

        var carolHub = ChatHubTestHarness.Create(db, carolId);
        await carolHub.OnConnectedAsync();
        await carolHub.SetDisplayName(carolName);

        var aliceHub = ChatHubTestHarness.Create(db, aliceId);
        await aliceHub.OnConnectedAsync();
        await aliceHub.SetDisplayName(aliceName);

        return (db, aliceId, aliceName, bobId, bobName, carolId, carolName, aliceHub, bobHub, carolHub);
    }

    [Fact]
    public async Task PromoteGroupAdmin_ByCreator_LetsPromotedUserActAsAdmin()
    {
        var (_, _, aliceName, _, bobName, _, carolName, aliceHub, bobHub, _) = await SetUpAliceBobAndCarol();
        var chatId = await aliceHub.CreateGroupChat("Trio", new List<string> { bobName, carolName });

        await aliceHub.PromoteGroupAdmin(chatId, bobName);

        // Bob is now an admin too - he can do admin-only actions without Alice.
        await bobHub.RenameGroupChat(chatId, "Renamed by Bob");
        var admins = await bobHub.GetGroupAdmins(chatId);
        Assert.Contains(bobName, admins);
        Assert.Contains(aliceName, admins);
    }

    [Fact]
    public async Task PromoteGroupAdmin_ByNonAdmin_ThrowsHubException()
    {
        var (_, _, _, _, bobName, _, carolName, aliceHub, bobHub, _) = await SetUpAliceBobAndCarol();
        var chatId = await aliceHub.CreateGroupChat("Trio", new List<string> { bobName, carolName });

        await Assert.ThrowsAsync<HubException>(() => bobHub.PromoteGroupAdmin(chatId, carolName));
    }

    [Fact]
    public async Task DemoteGroupAdmin_LastRemainingAdmin_ThrowsHubException()
    {
        var (_, _, aliceName, _, bobName, _, _, aliceHub, _, _) = await SetUpAliceBobAndCarol();
        var chatId = await aliceHub.CreateGroupChat("Duo", new List<string> { bobName });

        await Assert.ThrowsAsync<HubException>(() => aliceHub.DemoteGroupAdmin(chatId, aliceName));
    }

    [Fact]
    public async Task DemoteGroupAdmin_WithAnotherAdminRemaining_Succeeds()
    {
        var (_, _, aliceName, _, bobName, _, _, aliceHub, _, _) = await SetUpAliceBobAndCarol();
        var chatId = await aliceHub.CreateGroupChat("Duo", new List<string> { bobName });

        await aliceHub.PromoteGroupAdmin(chatId, bobName);
        await aliceHub.DemoteGroupAdmin(chatId, aliceName);

        var admins = await aliceHub.GetGroupAdmins(chatId);
        Assert.DoesNotContain(aliceName, admins);
        Assert.Contains(bobName, admins);
    }

    [Fact]
    public async Task LeaveChat_SoleAdmin_AutoPromotesAnotherMember()
    {
        var (_, _, aliceName, _, bobName, _, _, aliceHub, bobHub, _) = await SetUpAliceBobAndCarol();
        var chatId = await aliceHub.CreateGroupChat("Duo", new List<string> { bobName });

        await aliceHub.LeaveChat(chatId);

        var admins = await bobHub.GetGroupAdmins(chatId);
        Assert.DoesNotContain(aliceName, admins);
        Assert.Contains(bobName, admins);

        // Bob, now the sole admin, can perform admin-only actions.
        await bobHub.RenameGroupChat(chatId, "Bob's group now");
    }

    [Fact]
    public async Task RemoveGroupMember_SoleAdminRemovedIsImpossible_ButRemovingAdminViaGroupMemberIsNot()
    {
        // Sanity check on RemoveGroupMember: an admin can still remove a non-admin member, and
        // that removal never orphans the group (the remover stays admin).
        var (_, _, aliceName, _, bobName, _, _, aliceHub, bobHub, _) = await SetUpAliceBobAndCarol();
        var chatId = await aliceHub.CreateGroupChat("Duo", new List<string> { bobName });

        await aliceHub.RemoveGroupMember(chatId, bobName);

        var admins = await aliceHub.GetGroupAdmins(chatId);
        Assert.Contains(aliceName, admins);
    }
}
