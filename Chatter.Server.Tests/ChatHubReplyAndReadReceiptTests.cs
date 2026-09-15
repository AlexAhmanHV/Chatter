using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Reply-to previews and group read receipts - same Guid-isolated harness as the other test
// classes.
public class ChatHubReplyAndReadReceiptTests
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

    // ---------- Reply-to ----------

    [Fact]
    public async Task SendToChat_WithReplyToMessageId_IncludesReplyPreview()
    {
        var (_, _, aliceName, _, bobName, _, _, aliceHub, bobHub, _) = await SetUpAliceBobAndCarol();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "original message");
        var originalId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await bobHub.SendToChat(chatId, "a reply", replyToMessageId: originalId);

        var history = await aliceHub.GetChatHistory(chatId);
        var reply = history.Single(m => m.Body == "a reply");
        Assert.NotNull(reply.ReplyTo);
        Assert.Equal(originalId, reply.ReplyTo!.MessageId);
        Assert.Equal(aliceName, reply.ReplyTo.Sender);
        Assert.Equal("original message", reply.ReplyTo.Snippet);
        Assert.False(reply.ReplyTo.IsDeleted);
    }

    [Fact]
    public async Task SendToChat_ReplyToMessageInDifferentChat_ThrowsHubException()
    {
        var (_, _, _, _, bobName, _, carolName, aliceHub, bobHub, carolHub) = await SetUpAliceBobAndCarol();
        var aliceBobDm = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(aliceBobDm, "in the alice/bob dm");
        var messageInOtherChat = Assert.Single(await aliceHub.GetChatHistory(aliceBobDm)).Id;

        var aliceCarolDm = await aliceHub.CreateDm(carolName);

        await Assert.ThrowsAsync<HubException>(() =>
            aliceHub.SendToChat(aliceCarolDm, "trying to leak", replyToMessageId: messageInOtherChat));
    }

    [Fact]
    public async Task SendToChat_ReplyToDeletedMessage_PreviewFlagsDeletedWithNoSnippet()
    {
        var (_, _, _, _, bobName, _, _, aliceHub, bobHub, _) = await SetUpAliceBobAndCarol();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "will be deleted");
        var originalId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;
        await aliceHub.DeleteMessage(originalId);

        await bobHub.SendToChat(chatId, "replying anyway", replyToMessageId: originalId);

        var reply = (await aliceHub.GetChatHistory(chatId)).Single(m => m.Body == "replying anyway");
        Assert.NotNull(reply.ReplyTo);
        Assert.True(reply.ReplyTo!.IsDeleted);
        Assert.Equal(string.Empty, reply.ReplyTo.Snippet);
    }

    [Fact]
    public async Task SendAttachment_WithReplyToMessageId_IncludesReplyPreview()
    {
        var (_, _, _, _, bobName, _, _, aliceHub, bobHub, _) = await SetUpAliceBobAndCarol();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "text to reply to");
        var originalId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await bobHub.SendAttachment(chatId, "photo.png", "image/png", new byte[] { 1, 2, 3 }, null, replyToMessageId: originalId);

        var attachmentMsg = (await aliceHub.GetChatHistory(chatId)).Single(m => m.Attachment is not null);
        Assert.NotNull(attachmentMsg.ReplyTo);
        Assert.Equal(originalId, attachmentMsg.ReplyTo!.MessageId);
    }

    [Fact]
    public async Task ForwardMessage_DoesNotCarryOverAReplyLink()
    {
        var (_, _, _, _, bobName, _, _, aliceHub, bobHub, _) = await SetUpAliceBobAndCarol();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "original");
        var originalId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;
        await bobHub.SendToChat(chatId, "a reply", replyToMessageId: originalId);
        var replyId = (await aliceHub.GetChatHistory(chatId)).Single(m => m.Body == "a reply").Id;

        var groupId = await aliceHub.CreateGroupChat("Group", new List<string> { bobName });
        var forwardedId = await aliceHub.ForwardMessage(replyId, groupId);

        var forwarded = (await aliceHub.GetChatHistory(groupId)).Single(m => m.Id == forwardedId);
        Assert.Null(forwarded.ReplyTo);
    }

    // ---------- Group read receipts ----------

    [Fact]
    public async Task GetChatReadReceipts_ReflectsMarkReadCalls()
    {
        var (_, _, aliceName, _, bobName, _, carolName, aliceHub, bobHub, carolHub) = await SetUpAliceBobAndCarol();
        var groupId = await aliceHub.CreateGroupChat("Trio", new List<string> { bobName, carolName });
        await aliceHub.SendToChat(groupId, "hello everyone");
        var messageId = (await aliceHub.GetChatHistory(groupId)).Single().Id;

        await bobHub.JoinChat(groupId);
        await carolHub.JoinChat(groupId);
        await bobHub.MarkRead(groupId, messageId);

        var receipts = await aliceHub.GetChatReadReceipts(groupId);

        Assert.True(receipts.ContainsKey(bobName));
        Assert.Equal(messageId, receipts[bobName]);
        Assert.False(receipts.ContainsKey(carolName));
    }

    [Fact]
    public async Task GetChatReadReceipts_NonMember_ThrowsHubException()
    {
        var (_, _, _, _, bobName, _, _, aliceHub, _, _) = await SetUpAliceBobAndCarol();
        var chatId = await aliceHub.CreateDm(bobName);

        var outsiderHub = ChatHubTestHarness.Create(NewId(), NewId());
        await Assert.ThrowsAsync<HubException>(() => outsiderHub.GetChatReadReceipts(chatId));
    }
}
