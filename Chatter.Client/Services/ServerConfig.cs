using System.Net;
using System.Net.Sockets;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace Chatter.Client.Services;

// Where to find the ASP.NET Core backend. "localhost" only reaches the same machine, which
// breaks on the Android emulator (it has its own loopback; the host machine is 10.0.2.2) and
// on a physical device (needs the dev machine's real LAN IP) - DefaultBaseUrl below is only ever
// a best-effort starting point for those cases.
//
// The actual value is user-overridable at runtime (see LoginViewModel's "Server address" field)
// and persisted via Preferences, specifically so a physical device doesn't need a source-code
// edit and rebuild just to point at a real server - the single biggest piece of friction for
// anyone who isn't already a .NET developer. Preferences is per-app-install local storage, not
// synced or backed up, which is exactly the "just remember what I typed last time" behavior
// wanted here.
public static class ServerConfig
{
    private const int Port = 5291;
    private const string PreferenceKey = "ServerBaseUrl";

    public static string DefaultBaseUrl
    {
        get
        {
#if ANDROID
            return DeviceInfo.Current.DeviceType == DeviceType.Virtual
                ? $"http://10.0.2.2:{Port}"
                : $"http://192.168.1.100:{Port}"; // placeholder - replace with your dev machine's LAN IP in Settings
#elif IOS
            return DeviceInfo.Current.DeviceType == DeviceType.Virtual
                ? $"http://localhost:{Port}"
                : $"http://192.168.1.100:{Port}"; // placeholder - replace with your dev machine's LAN IP in Settings
#else
            return $"http://localhost:{Port}";
#endif
        }
    }

    public static string BaseUrl
    {
        get
        {
            var stored = Preferences.Default.Get(PreferenceKey, string.Empty);
            return string.IsNullOrWhiteSpace(stored) ? DefaultBaseUrl : stored.TrimEnd('/');
        }
    }

    // True only when the user has actually saved a custom value - lets the UI distinguish "using
    // the guessed default" from "using what you typed", without duplicating the fallback logic.
    public static bool HasCustomBaseUrl =>
        !string.IsNullOrWhiteSpace(Preferences.Default.Get(PreferenceKey, string.Empty));

    // Pass null/empty to clear the override and go back to the guessed default.
    public static void SetBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Preferences.Default.Remove(PreferenceKey);
            return;
        }

        Preferences.Default.Set(PreferenceKey, value.Trim().TrimEnd('/'));
    }

    // True when BaseUrl is plain http *and* points somewhere other than this machine/LAN - i.e.
    // the case where email/password/messages would actually cross a real, untrusted network in
    // cleartext. Plain http to localhost/a LAN address (the default for local dev - see
    // DefaultBaseUrl) is exempted since nothing there leaves a trusted boundary. Used to warn
    // before login/register - see LoginViewModel/RegisterViewModel.
    public static bool IsCurrentServerInsecure
    {
        get
        {
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return false;
            return !IsLocalOrPrivateHost(uri.Host);
        }
    }

    private static bool IsLocalOrPrivateHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false; // a real hostname - treat as public
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;

        var b = ip.GetAddressBytes();
        if (b[0] == 10) return true;                              // 10.0.0.0/8 (incl. the Android emulator's 10.0.2.2)
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;   // 172.16.0.0/12
        if (b[0] == 192 && b[1] == 168) return true;               // 192.168.0.0/16
        if (b[0] == 169 && b[1] == 254) return true;               // 169.254.0.0/16 (link-local)
        return false;
    }
}
