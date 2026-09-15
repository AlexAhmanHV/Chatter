using Chatter.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Chatter.Server.Tests;

// Call signaling (offer/answer/ICE/hangup relay) - same Guid-isolated harness as the other test
// classes. The harness mocks SignalR delivery, so these tests confirm the hub methods accept
// valid calls and correctly reject invalid ones (no exception == the relay call went through, per
// the same pattern used for SetRecordingVoiceMessage).
public class ChatHubCallSignalingTests
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
    public async Task CallInvite_ToOnlineDmPartner_Succeeds()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        await aliceHub.CallInvite(chatId, "video", "fake-sdp-offer");
    }

    [Fact]
    public async Task CallInvite_RecipientOffline_ThrowsHubException()
    {
        var db = NewId();
        var aliceId = NewId();
        var aliceName = "Alice-" + NewId();
        var bobId = NewId();
        var bobName = "Bob-" + NewId();

        var aliceHub = ChatHubTestHarness.Create(db, aliceId);
        await aliceHub.OnConnectedAsync();
        await aliceHub.SetDisplayName(aliceName);

        // Bob needs a registered display name for CreateDm to resolve him by name, which only
        // happens via a connection - so connect him briefly, then disconnect, leaving him with no
        // live connections to ring.
        var bobHubTemp = ChatHubTestHarness.Create(db, bobId);
        await bobHubTemp.OnConnectedAsync();
        await bobHubTemp.SetDisplayName(bobName);
        await bobHubTemp.OnDisconnectedAsync(null);

        var chatId = await aliceHub.CreateDm(bobName);

        await Assert.ThrowsAsync<HubException>(() => aliceHub.CallInvite(chatId, "audio", "fake-sdp-offer"));
    }

    [Fact]
    public async Task CallInvite_InAGroupChat_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var groupId = await aliceHub.CreateGroupChat("Group", new List<string> { bobName });

        await Assert.ThrowsAsync<HubException>(() => aliceHub.CallInvite(groupId, "audio", "fake-sdp-offer"));
    }

    [Fact]
    public async Task CallInvite_BlockedUser_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);
        await aliceHub.BlockUser(bobName);

        await Assert.ThrowsAsync<HubException>(() => aliceHub.CallInvite(chatId, "audio", "fake-sdp-offer"));
    }

    [Fact]
    public async Task CallInvite_NonMemberOfChat_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var outsiderHub = ChatHubTestHarness.Create(NewId(), NewId());
        await Assert.ThrowsAsync<HubException>(() => outsiderHub.CallInvite(chatId, "audio", "fake-sdp-offer"));
    }

    [Fact]
    public async Task CallAnswer_DeclineIceHangup_AllSucceedForDmMembers()
    {
        var (_, _, _, _, bobName, aliceHub, bobHub) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        await bobHub.CallAnswer(chatId, "fake-sdp-answer");
        await bobHub.CallIceCandidate(chatId, "fake-ice-candidate");
        await aliceHub.CallIceCandidate(chatId, "another-fake-ice-candidate");
        await aliceHub.CallHangup(chatId);
        await bobHub.CallDecline(chatId);
    }

    [Fact]
    public async Task CallAnswer_NonMemberOfChat_ThrowsHubException()
    {
        var (_, _, _, _, bobName, aliceHub, _) = await SetUpAliceAndBob();
        var chatId = await aliceHub.CreateDm(bobName);

        var outsiderHub = ChatHubTestHarness.Create(NewId(), NewId());
        await Assert.ThrowsAsync<HubException>(() => outsiderHub.CallAnswer(chatId, "fake-sdp-answer"));
    }
}
