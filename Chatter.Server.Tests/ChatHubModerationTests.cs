using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Site-admin moderation: reporting, reviewing reports, deleting any message, and ban/unban - same
// Guid-isolated harness as the other test classes.
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

    // ---------- Reporting ----------

    [Fact]
    public async Task ReportMessage_MemberOfChat_Succeeds()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hello");
        var messageId = (await aliceHub.GetChatHistory(chatId)).Single().Id;

        await bobHub.ReportMessage(messageId, "spam");
        // No exception means it was recorded - GetReports (admin-only) proves it below.
    }

    [Fact]
    public async Task ReportMessage_NonMember_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hello");
        var messageId = (await aliceHub.GetChatHistory(chatId)).Single().Id;

        var carolHub = ChatHubTestHarness.Create(NewId(), NewId());
        await Assert.ThrowsAsync<HubException>(() => carolHub.ReportMessage(messageId, "spam"));
    }

    [Fact]
    public async Task ReportMessage_EmptyReason_Throws()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hello");
        var messageId = (await aliceHub.GetChatHistory(chatId)).Single().Id;

        await Assert.ThrowsAsync<HubException>(() => aliceHub.ReportMessage(messageId, "   "));
    }

    // ---------- Reviewing reports (admin-only) ----------

    [Fact]
    public async Task GetReports_NonAdmin_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        await aliceHub.CreateDm(bobName);

        await Assert.ThrowsAsync<HubException>(() => aliceHub.GetReports());
    }

    [Fact]
    public async Task GetReports_Admin_SeesPendingReport()
    {
        var (db, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "buy my crypto course");
        var messageId = (await aliceHub.GetChatHistory(chatId)).Single().Id;
        await bobHub.ReportMessage(messageId, "spam");

        var adminHub = ChatHubTestHarness.Create(db, NewId(), isAdmin: true);
        var reports = await adminHub.GetReports();

        var report = Assert.Single(reports);
        Assert.Equal(messageId, report.MessageId);
        Assert.Equal("spam", report.Reason);
        Assert.Contains("crypto", report.MessageSnippet);
    }

    [Fact]
    public async Task DismissReport_Admin_RemovesItFromQueue()
    {
        var (db, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hello");
        var messageId = (await aliceHub.GetChatHistory(chatId)).Single().Id;
        await bobHub.ReportMessage(messageId, "not actually spam");

        var adminHub = ChatHubTestHarness.Create(db, NewId(), isAdmin: true);
        var report = Assert.Single(await adminHub.GetReports());

        await adminHub.DismissReport(report.ReportId);

        Assert.Empty(await adminHub.GetReports());
    }

    [Fact]
    public async Task DismissReport_NonAdmin_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        await aliceHub.CreateDm(bobName);

        await Assert.ThrowsAsync<HubException>(() => aliceHub.DismissReport(1));
    }

    // ---------- Deleting any message (admin-only bypass of ownership) ----------

    [Fact]
    public async Task DeleteMessage_Admin_CanDeleteSomeoneElsesMessage()
    {
        var (db, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hello");
        var messageId = (await aliceHub.GetChatHistory(chatId)).Single().Id;

        var adminHub = ChatHubTestHarness.Create(db, NewId(), isAdmin: true);
        await adminHub.DeleteMessage(messageId);

        var history = await aliceHub.GetChatHistory(chatId);
        Assert.True(Assert.Single(history).IsDeleted);
    }

    [Fact]
    public async Task DeleteMessage_NonAdminNonOwner_StillThrows()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hello");
        var messageId = (await aliceHub.GetChatHistory(chatId)).Single().Id;

        await Assert.ThrowsAsync<HubException>(() => bobHub.DeleteMessage(messageId));
    }

    // ---------- Ban / unban ----------

    [Fact]
    public async Task BanUser_NonAdmin_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();

        await Assert.ThrowsAsync<HubException>(() => aliceHub.BanUser(bobName));
    }

    [Fact]
    public async Task BanUser_Admin_SetsIsBannedOnTheAccount()
    {
        var (db, _, _, bobId, bobName, _, _) = await SetUpAliceAndBob();

        var adminHub = ChatHubTestHarness.Create(db, NewId(), isAdmin: true);
        await adminHub.BanUser(bobName);

        using var checkDb = ChatHubTestHarness.CreateDb(db);
        var bob = checkDb.Users.Single(u => u.Id == bobId);
        Assert.True(bob.IsBanned);
        Assert.NotNull(bob.BannedAtUtc);
    }

    [Fact]
    public async Task UnbanUser_Admin_ClearsIsBanned()
    {
        var (db, _, _, bobId, bobName, _, _) = await SetUpAliceAndBob();
        var adminHub = ChatHubTestHarness.Create(db, NewId(), isAdmin: true);
        await adminHub.BanUser(bobName);

        await adminHub.UnbanUser(bobName);

        using var checkDb = ChatHubTestHarness.CreateDb(db);
        var bob = checkDb.Users.Single(u => u.Id == bobId);
        Assert.False(bob.IsBanned);
        Assert.Null(bob.BannedAtUtc);
    }
}
