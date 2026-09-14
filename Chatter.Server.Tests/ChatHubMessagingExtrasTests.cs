using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Last-seen presence, message forwarding, and voice messages - same Guid-isolated harness as the
// other test classes.
public class ChatHubMessagingExtrasTests
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

    private static byte[] FakeImage() => new byte[] { 1, 2, 3, 4 };
    private static byte[] FakeAudio() => new byte[] { 9, 9, 9, 9, 9 };

    // ---------- Last seen ----------

    [Fact]
    public async Task GetLastSeen_ForOnlineUser_ReturnsNoEntry()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();

        var result = await aliceHub.GetLastSeen(new List<string> { bobName });

        Assert.False(result.ContainsKey(bobName));
    }

    [Fact]
    public async Task GetLastSeen_AfterDisconnect_ReturnsATimestamp()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();

        await bobHub.OnDisconnectedAsync(null);

        var result = await aliceHub.GetLastSeen(new List<string> { bobName });

        Assert.True(result.ContainsKey(bobName));
    }

    // ---------- Forwarding ----------

    [Fact]
    public async Task ForwardMessage_TextMessage_AppearsInTargetChatAsForwarded()
    {
        var (_, _, aliceName, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var dmId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(dmId, "Hello there");
        var originalId = Assert.Single(await aliceHub.GetChatHistory(dmId)).Id;

        var groupId = await aliceHub.CreateGroupChat("Group", new List<string> { bobName });
        var newId = await aliceHub.ForwardMessage(originalId, groupId);

        var history = await aliceHub.GetChatHistory(groupId);
        var forwarded = Assert.Single(history);
        Assert.Equal(newId, forwarded.Id);
        Assert.Equal("Hello there", forwarded.Body);
        Assert.True(forwarded.IsForwarded);
        Assert.Equal(aliceName, forwarded.Sender);
    }

    [Fact]
    public async Task ForwardMessage_WithAttachment_CopiesAttachmentMetadataAndBytes()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var dmId = await aliceHub.CreateDm(bobName);
        var bytes = FakeImage();
        var originalId = await aliceHub.SendAttachment(dmId, "photo.png", "image/png", bytes, null);

        var groupId = await aliceHub.CreateGroupChat("Group", new List<string> { bobName });
        var newId = await aliceHub.ForwardMessage(originalId, groupId);

        var history = await aliceHub.GetChatHistory(groupId);
        var forwarded = Assert.Single(history);
        Assert.NotNull(forwarded.Attachment);
        Assert.Equal("image/png", forwarded.Attachment!.ContentType);

        await bobHub.JoinChat(groupId);
        var data = await bobHub.GetAttachmentData(newId);
        Assert.Equal(bytes, data.Data);
    }

    [Fact]
    public async Task ForwardMessage_ToChatCallerIsNotMemberOf_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var dmId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(dmId, "Hi");
        var originalId = Assert.Single(await aliceHub.GetChatHistory(dmId)).Id;

        // A group id Alice was never added to.
        await Assert.ThrowsAsync<HubException>(() => aliceHub.ForwardMessage(originalId, "group:" + NewId()));
    }

    [Fact]
    public async Task ForwardMessage_SourceMessageCallerCannotAccess_ThrowsHubException()
    {
        var db = NewId();
        var aliceId = NewId();
        var aliceName = "Alice-" + NewId();
        var bobId = NewId();
        var bobName = "Bob-" + NewId();
        var carolId = NewId();
        var carolName = "Carol-" + NewId();

        var aliceHub = ChatHubTestHarness.Create(db, aliceId);
        await aliceHub.OnConnectedAsync();
        await aliceHub.SetDisplayName(aliceName);

        var bobHub = ChatHubTestHarness.Create(db, bobId);
        await bobHub.OnConnectedAsync();
        await bobHub.SetDisplayName(bobName);

        var carolHub = ChatHubTestHarness.Create(db, carolId);
        await carolHub.OnConnectedAsync();
        await carolHub.SetDisplayName(carolName);

        var aliceBobDm = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(aliceBobDm, "Secret");
        var secretId = Assert.Single(await aliceHub.GetChatHistory(aliceBobDm)).Id;

        var carolGroup = await carolHub.CreateGroupChat("CarolsGroup", new List<string> { aliceName });

        await Assert.ThrowsAsync<HubException>(() => aliceHub.ForwardMessage(secretId + 999_999, carolGroup));
    }

    // ---------- Voice messages ----------

    [Fact]
    public async Task SendVoiceMessage_ThenGetAttachmentData_ReturnsTheSameBytes()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var bytes = FakeAudio();
        var messageId = await aliceHub.SendVoiceMessage(chatId, "audio/wav", bytes, durationSeconds: 12);

        var history = await aliceHub.GetChatHistory(chatId);
        var msg = Assert.Single(history);
        Assert.NotNull(msg.Attachment);
        Assert.Equal("audio/wav", msg.Attachment!.ContentType);
        Assert.Equal(12, msg.Attachment.DurationSeconds);

        await bobHub.JoinChat(chatId);
        var data = await bobHub.GetAttachmentData(messageId);
        Assert.Equal(bytes, data.Data);
    }

    [Fact]
    public async Task SendVoiceMessage_DisallowedContentType_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        await Assert.ThrowsAsync<HubException>(() =>
            aliceHub.SendVoiceMessage(chatId, "image/png", FakeAudio(), durationSeconds: 5));
    }

    [Fact]
    public async Task SendVoiceMessage_TooLarge_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var tooBig = new byte[2 * 1024 * 1024 + 1];
        await Assert.ThrowsAsync<HubException>(() =>
            aliceHub.SendVoiceMessage(chatId, "audio/wav", tooBig, durationSeconds: 90));
    }
}
