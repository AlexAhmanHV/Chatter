using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Pinning, video attachments, and message edit history - same Guid-isolated harness as the other
// test classes.
public class ChatHubPinVideoEditHistoryTests
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

    private static byte[] FakeVideo() => new byte[] { 7, 7, 7, 7, 7, 7 };

    // ---------- Pinning ----------

    [Fact]
    public async Task PinMessage_ThenGetPinnedMessages_ReturnsIt()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "pin me");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await bobHub.PinMessage(messageId);

        var pinned = await aliceHub.GetPinnedMessages(chatId);
        var pinnedMsg = Assert.Single(pinned);
        Assert.Equal(messageId, pinnedMsg.Id);
        Assert.True(pinnedMsg.IsPinned);
    }

    [Fact]
    public async Task UnpinMessage_RemovesItFromPinnedList()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "pin me");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await aliceHub.PinMessage(messageId);
        await aliceHub.UnpinMessage(messageId);

        Assert.Empty(await aliceHub.GetPinnedMessages(chatId));
    }

    [Fact]
    public async Task PinMessage_NonMember_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hello");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        var outsiderHub = ChatHubTestHarness.Create(NewId(), NewId());
        await Assert.ThrowsAsync<HubException>(() => outsiderHub.PinMessage(messageId));
    }

    [Fact]
    public async Task PinMessage_BeyondTheCap_ThrowsHubException()
    {
        var (db, aliceId, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        // SendToChat is rate-limited per connection (10/10s) - use a fresh connection per send so
        // this test's 21 sends aren't themselves throttled before ever reaching the pin cap.
        for (var i = 0; i < 20; i++)
        {
            var senderHub = ChatHubTestHarness.Create(db, aliceId);
            await senderHub.SendToChat(chatId, $"message {i}");
        }
        var history = await aliceHub.GetChatHistory(chatId, take: 20);
        foreach (var m in history)
        {
            await aliceHub.PinMessage(m.Id);
        }

        var lastSenderHub = ChatHubTestHarness.Create(db, aliceId);
        await lastSenderHub.SendToChat(chatId, "one too many");
        var extra = (await aliceHub.GetChatHistory(chatId)).Last();

        await Assert.ThrowsAsync<HubException>(() => aliceHub.PinMessage(extra.Id));
    }

    [Fact]
    public async Task DeleteMessage_ThatWasPinned_UnpinsIt()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "pin then delete");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await aliceHub.PinMessage(messageId);
        await aliceHub.DeleteMessage(messageId);

        Assert.Empty(await aliceHub.GetPinnedMessages(chatId));
    }

    // ---------- Video ----------

    [Fact]
    public async Task SendVideo_ThenGetAttachmentData_ReturnsTheSameBytes()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var bytes = FakeVideo();
        var messageId = await aliceHub.SendVideo(chatId, "clip.mp4", "video/mp4", bytes, durationSeconds: 8);

        var history = await aliceHub.GetChatHistory(chatId);
        var msg = Assert.Single(history);
        Assert.NotNull(msg.Attachment);
        Assert.Equal("video/mp4", msg.Attachment!.ContentType);
        Assert.Equal(8, msg.Attachment.DurationSeconds);

        await bobHub.JoinChat(chatId);
        var data = await bobHub.GetAttachmentData(messageId);
        Assert.Equal(bytes, data.Data);
    }

    [Fact]
    public async Task SendVideo_DisallowedContentType_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        await Assert.ThrowsAsync<HubException>(() =>
            aliceHub.SendVideo(chatId, "clip.png", "image/png", FakeVideo()));
    }

    [Fact]
    public async Task SendVideo_TooLarge_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var tooBig = new byte[20 * 1024 * 1024 + 1];
        await Assert.ThrowsAsync<HubException>(() =>
            aliceHub.SendVideo(chatId, "clip.mp4", "video/mp4", tooBig));
    }

    // ---------- Edit history ----------

    [Fact]
    public async Task EditMessage_ThenGetMessageEditHistory_ReturnsPreviousBodies()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "version one");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await aliceHub.EditMessage(messageId, "version two");
        await aliceHub.EditMessage(messageId, "version three");

        var history = await bobHub.GetMessageEditHistory(messageId);
        Assert.Equal(2, history.Count);
        Assert.Equal("version one", history[0].PreviousBody);
        Assert.Equal("version two", history[1].PreviousBody);

        var current = Assert.Single(await aliceHub.GetChatHistory(chatId));
        Assert.Equal("version three", current.Body);
    }

    [Fact]
    public async Task EditMessage_ToTheSameBody_DoesNotAddAHistoryEntry()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "same text");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await aliceHub.EditMessage(messageId, "same text");

        Assert.Empty(await aliceHub.GetMessageEditHistory(messageId));
    }

    [Fact]
    public async Task GetMessageEditHistory_NonMember_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "hello");
        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;
        await aliceHub.EditMessage(messageId, "hello edited");

        var outsiderHub = ChatHubTestHarness.Create(NewId(), NewId());
        await Assert.ThrowsAsync<HubException>(() => outsiderHub.GetMessageEditHistory(messageId));
    }
}
