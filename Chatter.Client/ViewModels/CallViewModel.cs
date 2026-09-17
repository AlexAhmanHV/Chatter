/*
File: CallViewModel.cs

What this does:
- Purpose: Drives a single 1:1 audio/video call - owns the call's state machine and bridges
  ChatService's call-signaling events to the WebRTC page hosted in CallPage's HybridWebView (see
  Resources/Raw/wwwroot/call.html for the actual media/RTCPeerConnection logic and the JSON
  message protocol used between the two).
- How: CallPage calls AttachWebView once its HybridWebView is loaded, then StartAsCaller or
  StartAsCallee to kick things off. From there, messages flow in both directions: JS events
  (offer/answer created, ICE candidates, connection state) arrive via HybridWebView's
  RawMessageReceived and get relayed to the server through ChatService; server-relayed messages
  from the other party (answer, remote ICE candidates, hangup) arrive via ChatService's events and
  get forwarded into the page via SendRawMessage.
- ICE servers: fetched fresh from ChatHub.GetIceServers on every call ("ready" handler below) and
  pushed into the page before start-caller/start-callee - always includes public STUN, plus a
  short-lived TURN credential if the server has one configured (see README's "Configure"). Not
  cached client-side since a TURN credential expires and shouldn't outlive one call attempt.
*/

using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Chatter.Client.Services;

namespace Chatter.Client.ViewModels;

public enum CallStatus { Connecting, Ringing, Connected, Ended, Failed }

public partial class CallViewModel : ObservableObject
{
    private readonly ChatService _chat;
    private HybridWebView? _webView;
    private bool _jsReady;
    private string? _pendingOfferForCallee;
    private bool _hungUp;

    public string ChatId { get; private set; } = string.Empty;
    public string PeerDisplayName { get; private set; } = string.Empty;
    public bool IsVideoCall { get; private set; }
    public bool IsCaller { get; private set; }

    [ObservableProperty] public partial CallStatus Status { get; set; } = CallStatus.Connecting;
    [ObservableProperty] public partial bool IsMuted { get; set; }
    [ObservableProperty] public partial bool IsCameraOff { get; set; }

    public string StatusText => Status switch
    {
        CallStatus.Connecting => "Connecting…",
        CallStatus.Ringing => $"Calling {PeerDisplayName}…",
        CallStatus.Connected => "Connected",
        CallStatus.Failed => "Call failed",
        _ => "Call ended",
    };
    partial void OnStatusChanged(CallStatus value) => OnPropertyChanged(nameof(StatusText));

    public IRelayCommand ToggleMuteCommand { get; }
    public IRelayCommand ToggleCameraCommand { get; }
    public IRelayCommand HangupCommand { get; }

    // Raised when the call ends for any reason (hangup, decline, connection failure) so the page
    // knows to navigate itself back - the view model has no navigation dependency of its own.
    public event Action? CallEnded;

    public CallViewModel(ChatService chat)
    {
        _chat = chat;

        ToggleMuteCommand = new RelayCommand(() =>
        {
            IsMuted = !IsMuted;
            SendToJs(new { type = "toggle-mute", muted = IsMuted });
        });

        ToggleCameraCommand = new RelayCommand(() =>
        {
            IsCameraOff = !IsCameraOff;
            SendToJs(new { type = "toggle-camera", off = IsCameraOff });
        });

        HangupCommand = new RelayCommand(() => EndCall(notifyPeer: true));

        _chat.CallAnswered += (chatId, sdpAnswer) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (chatId != ChatId) return;
                Status = CallStatus.Connecting;
                SendToJs(new { type = "remote-answer", sdp = sdpAnswer });
            });

        _chat.CallDeclined += chatId =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (chatId != ChatId) return;
                Status = CallStatus.Ended;
                EndCall(notifyPeer: false);
            });

        _chat.CallIceCandidateReceived += (chatId, candidateJson) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (chatId != ChatId) return;
                SendToJs(new { type = "ice-candidate", candidate = candidateJson });
            });

        _chat.CallEnded += chatId =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (chatId != ChatId) return;
                Status = CallStatus.Ended;
                EndCall(notifyPeer: false);
            });
    }

    public void AttachWebView(HybridWebView webView)
    {
        _webView = webView;
        _webView.RawMessageReceived += OnRawMessageReceived;
    }

    public void StartAsCaller(string chatId, string peerDisplayName, bool video)
    {
        ChatId = chatId;
        PeerDisplayName = peerDisplayName;
        IsVideoCall = video;
        IsCaller = true;
        Status = CallStatus.Connecting;
    }

    public void StartAsCallee(string chatId, string peerDisplayName, bool video, string sdpOffer)
    {
        ChatId = chatId;
        PeerDisplayName = peerDisplayName;
        IsVideoCall = video;
        IsCaller = false;
        Status = CallStatus.Connecting;
        _pendingOfferForCallee = sdpOffer;
    }

    private void OnRawMessageReceived(object? sender, HybridWebViewRawMessageReceivedEventArgs e)
    {
        if (e.Message is null) return;
        MainThread.BeginInvokeOnMainThread(() => HandleJsMessage(e.Message));
    }

    private void HandleJsMessage(string json)
    {
        JsonElement data;
        try { data = JsonDocument.Parse(json).RootElement; }
        catch { return; }

        var type = data.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "ready":
                _jsReady = true;
                _ = SendIceServersThenStartAsync();
                break;

            case "offer-created":
                _ = _chat.CallInviteAsync(ChatId, IsVideoCall ? "video" : "audio", GetString(data, "sdp"));
                break;

            case "answer-created":
                _ = _chat.CallAnswerAsync(ChatId, GetString(data, "sdp"));
                break;

            case "ice-candidate":
                _ = _chat.CallIceCandidateAsync(ChatId, GetString(data, "candidate"));
                break;

            case "connected":
                Status = CallStatus.Connected;
                break;

            case "disconnected":
                Status = CallStatus.Failed;
                EndCall(notifyPeer: true);
                break;

            case "error":
                Status = CallStatus.Failed;
                EndCall(notifyPeer: true);
                break;
        }
    }

    // Pushes this call's ICE server config into the page before telling it to actually start -
    // call.html falls back to its own hardcoded public-STUN default if this fails or is skipped,
    // so a transient GetIceServers error degrades to "no TURN this call" rather than no call.
    private async Task SendIceServersThenStartAsync()
    {
        try
        {
            var servers = await _chat.GetIceServersAsync();
            var jsServers = servers.Select(s => new { urls = s.Urls, username = s.Username, credential = s.Credential });
            SendToJs(new { type = "set-ice-servers", servers = jsServers });
        }
        catch { /* call.html keeps its default STUN-only config */ }

        if (IsCaller)
        {
            Status = CallStatus.Ringing;
            SendToJs(new { type = "start-caller", kind = IsVideoCall ? "video" : "audio" });
        }
        else if (_pendingOfferForCallee is not null)
        {
            SendToJs(new { type = "start-callee", kind = IsVideoCall ? "video" : "audio", sdp = _pendingOfferForCallee });
        }
    }

    private static string GetString(JsonElement data, string property) =>
        data.TryGetProperty(property, out var v) ? v.GetString() ?? string.Empty : string.Empty;

    private void SendToJs(object command)
    {
        if (_webView is null || !_jsReady) return;
        _webView.SendRawMessage(JsonSerializer.Serialize(command));
    }

    private void EndCall(bool notifyPeer)
    {
        if (_hungUp) return;
        _hungUp = true;

        SendToJs(new { type = "hangup" });
        if (notifyPeer && !string.IsNullOrEmpty(ChatId))
            _ = _chat.CallHangupAsync(ChatId);

        Status = CallStatus.Ended;
        CallEnded?.Invoke();
    }
}
