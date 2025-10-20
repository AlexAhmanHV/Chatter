// Hubs/ChatHub.cs
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace Chatter.Server.Hubs;

public class ChatHub : Hub
{
    private static readonly ConcurrentDictionary<string, string> _names = new();

    public override async Task OnConnectedAsync()
    {
        _names[Context.ConnectionId] = "Unknown";
        await BroadcastRosterAsync();
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _names.TryRemove(Context.ConnectionId, out _);
        await BroadcastRosterAsync();
        await base.OnDisconnectedAsync(exception);
    }

    public Task SetDisplayName(string displayName)
    {
        _names[Context.ConnectionId] = displayName;
        return BroadcastRosterAsync();
    }

    public async Task ChangeDisplayName(string newDisplayName)
    {
        var connId = Context.ConnectionId;
        var old = _names.TryGetValue(connId, out var prev) ? prev : "Unknown";
        _names[connId] = newDisplayName;

        await Clients.All.SendAsync("DisplayNameChanged", old, newDisplayName);
        await BroadcastRosterAsync();
    }

    public Task SendMessage(string user, string message) =>
        Clients.All.SendAsync("ReceiveMessage", user, message);

    // Caller can request the current list explicitly
    public Task<IReadOnlyList<string>> GetOnlineUsers()
    {
        var list = _names.Values
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

        return Task.FromResult((IReadOnlyList<string>)list);
    }

    private Task BroadcastRosterAsync()
    {
        var list = _names.Values
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Clients.All.SendAsync("OnlineUsers", list);
    }
}
