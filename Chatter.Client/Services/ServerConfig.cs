using Microsoft.Maui.Devices;

namespace Chatter.Client.Services;

// Where to find the ASP.NET Core backend. "localhost" only reaches the same machine, which
// breaks on the Android emulator (it has its own loopback; the host machine is 10.0.2.2) and
// on a physical device (needs the dev machine's real LAN IP).
public static class ServerConfig
{
    private const int Port = 5291;

    // Only used on a physical Android/iOS device. Set this to your development machine's
    // LAN IP (e.g. "192.168.1.42") before running on real hardware.
    private const string DevMachineLanIp = "192.168.1.100";

    public static string BaseUrl
    {
        get
        {
#if ANDROID
            return DeviceInfo.Current.DeviceType == DeviceType.Virtual
                ? $"http://10.0.2.2:{Port}"
                : $"http://{DevMachineLanIp}:{Port}";
#elif IOS
            return DeviceInfo.Current.DeviceType == DeviceType.Virtual
                ? $"http://localhost:{Port}"
                : $"http://{DevMachineLanIp}:{Port}";
#else
            return $"http://localhost:{Port}";
#endif
        }
    }
}
