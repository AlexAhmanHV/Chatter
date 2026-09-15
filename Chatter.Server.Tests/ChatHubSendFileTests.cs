using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Generic document/file attachments - same Guid-isolated harness as the other test classes.
public class ChatHubSendFileTests
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

    private static byte[] FakeFile() => new byte[] { 5, 4, 3, 2, 1 };

    [Fact]
    public async Task SendFile_ThenGetAttachmentData_ReturnsTheSameBytes()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var bytes = FakeFile();
        var messageId = await aliceHub.SendFile(chatId, "report.pdf", "application/pdf", bytes);

        var history = await aliceHub.GetChatHistory(chatId);
        var msg = Assert.Single(history);
        Assert.NotNull(msg.Attachment);
        Assert.Equal("report.pdf", msg.Attachment!.FileName);
        Assert.Equal("application/pdf", msg.Attachment.ContentType);

        await bobHub.JoinChat(chatId);
        var data = await bobHub.GetAttachmentData(messageId);
        Assert.Equal(bytes, data.Data);
    }

    [Fact]
    public async Task SendFile_DisallowedExtension_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        await Assert.ThrowsAsync<HubException>(() =>
            aliceHub.SendFile(chatId, "totally-safe.exe", "application/octet-stream", FakeFile()));
    }

    [Fact]
    public async Task SendFile_TooLarge_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var tooBig = new byte[20 * 1024 * 1024 + 1];
        await Assert.ThrowsAsync<HubException>(() =>
            aliceHub.SendFile(chatId, "big.zip", "application/zip", tooBig));
    }

    [Fact]
    public async Task SendFile_EmptyData_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        await Assert.ThrowsAsync<HubException>(() =>
            aliceHub.SendFile(chatId, "empty.txt", "text/plain", Array.Empty<byte>()));
    }

    [Fact]
    public async Task SendFile_NonMember_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var outsiderHub = ChatHubTestHarness.Create(NewId(), NewId());
        await Assert.ThrowsAsync<HubException>(() =>
            outsiderHub.SendFile(chatId, "doc.txt", "text/plain", FakeFile()));
    }

    [Fact]
    public async Task SendFile_BlankContentType_DefaultsToOctetStream()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        await aliceHub.SendFile(chatId, "mystery.dat", "", FakeFile());

        var msg = Assert.Single(await aliceHub.GetChatHistory(chatId));
        Assert.Equal("application/octet-stream", msg.Attachment!.ContentType);
    }
}
