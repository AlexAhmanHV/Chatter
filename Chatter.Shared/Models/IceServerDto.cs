namespace Chatter.Shared.Models;

// One entry of an RTCPeerConnection's iceServers config (see ChatHub.GetIceServers and
// call.html's set-ice-servers handler). Username/Credential are null for the public STUN entry -
// STUN needs no auth - and set to a short-lived, HMAC-signed pair for a configured TURN server
// (see Auth/TurnCredentialSigner).
public sealed record IceServerDto(string Urls, string? Username, string? Credential);
