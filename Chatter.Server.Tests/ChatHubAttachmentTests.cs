using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Image attachments (chat) and avatars (profile picture) - both stored as blobs in SQLite.
// Same Guid-isolated harness as the other test classes.
public class ChatHubAttachmentTests
{
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static byte[] FakeImage(int size = 16) => Enumerable.Range(0, size).Select(i => (byte)i).ToArray();

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

    // ---------- Attachments ----------

    [Fact]
    public async Task SendAttachment_ThenGetAttachmentData_ReturnsTheSameBytes()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var bytes = FakeImage();
        var messageId = await aliceHub.SendAttachment(chatId, "photo.png", "image/png", bytes, "look at this");

        var history = await aliceHub.GetChatHistory(chatId);
        var msg = Assert.Single(history);
        Assert.Equal("photo.png", msg.AttachmentFileName);
        Assert.Equal("image/png", msg.AttachmentContentType);
        Assert.Equal(bytes.Length, msg.AttachmentSizeBytes);
        Assert.Equal("look at this", msg.Body);

        await bobHub.JoinChat(chatId);
        var data = await bobHub.GetAttachmentData(messageId);
        Assert.Equal("image/png", data.ContentType);
        Assert.Equal(bytes, data.Data);
    }

    [Fact]
    public async Task SendAttachment_NonMember_ThrowsHubException()
    {
        var (db, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var eveHub = ChatHubTestHarness.Create(db, NewId());
        await eveHub.OnConnectedAsync();

        await Assert.ThrowsAsync<HubException>(
            () => eveHub.SendAttachment(chatId, "photo.png", "image/png", FakeImage(), null));
    }

    [Fact]
    public async Task SendAttachment_TooLarge_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var tooBig = new byte[5 * 1024 * 1024 + 1];
        await Assert.ThrowsAsync<HubException>(
            () => aliceHub.SendAttachment(chatId, "huge.png", "image/png", tooBig, null));
    }

    [Fact]
    public async Task SendAttachment_DisallowedContentType_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        await Assert.ThrowsAsync<HubException>(
            () => aliceHub.SendAttachment(chatId, "doc.pdf", "application/pdf", FakeImage(), null));
    }

    [Fact]
    public async Task SendAttachment_EmptyData_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        await Assert.ThrowsAsync<HubException>(
            () => aliceHub.SendAttachment(chatId, "empty.png", "image/png", Array.Empty<byte>(), null));
    }

    [Fact]
    public async Task GetAttachmentData_NonMember_ThrowsHubException()
    {
        var (db, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        var messageId = await aliceHub.SendAttachment(chatId, "photo.png", "image/png", FakeImage(), null);

        var eveHub = ChatHubTestHarness.Create(db, NewId());
        await eveHub.OnConnectedAsync();

        await Assert.ThrowsAsync<HubException>(() => eveHub.GetAttachmentData(messageId));
    }

    [Fact]
    public async Task GetAttachmentData_MessageWithoutAttachment_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.SendToChat(chatId, "just text, no picture");

        var messageId = Assert.Single(await aliceHub.GetChatHistory(chatId)).Id;

        await Assert.ThrowsAsync<HubException>(() => aliceHub.GetAttachmentData(messageId));
    }

    [Fact]
    public async Task SendAttachment_BlockedDm_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.BlockUser(bobName);

        await Assert.ThrowsAsync<HubException>(
            () => aliceHub.SendAttachment(chatId, "photo.png", "image/png", FakeImage(), null));
    }

    // ---------- Avatars ----------

    [Fact]
    public async Task UpdateAvatar_ThenGetAvatarUrls_ResolvesAUrlForThatUser()
    {
        var (_, _, aliceName, _, _, aliceHub, bobHub) = await SetUpAliceAndBob();

        await aliceHub.UpdateAvatar(FakeImage(), "image/png");

        var urls = await bobHub.GetAvatarUrls(new List<string> { aliceName });
        Assert.True(urls.TryGetValue(aliceName, out var url));
        Assert.StartsWith("/avatars/", url);
    }

    [Fact]
    public async Task GetAvatarUrls_UnknownName_IsOmittedNotThrown()
    {
        var (_, _, _, _, _, aliceHub, _) = await SetUpAliceAndBob();

        var urls = await aliceHub.GetAvatarUrls(new List<string> { "nobody-" + NewId() });

        Assert.Empty(urls);
    }

    [Fact]
    public async Task UpdateAvatar_TooLarge_ThrowsHubException()
    {
        var (_, _, _, _, _, aliceHub, _) = await SetUpAliceAndBob();

        var tooBig = new byte[512 * 1024 + 1];
        await Assert.ThrowsAsync<HubException>(() => aliceHub.UpdateAvatar(tooBig, "image/png"));
    }

    [Fact]
    public async Task UpdateAvatar_DisallowedContentType_ThrowsHubException()
    {
        var (_, _, _, _, _, aliceHub, _) = await SetUpAliceAndBob();

        await Assert.ThrowsAsync<HubException>(() => aliceHub.UpdateAvatar(FakeImage(), "application/pdf"));
    }
}
