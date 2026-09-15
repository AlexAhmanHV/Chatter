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
}
