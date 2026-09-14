using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;
using Chatter.Client.Services;
using Chatter.Client.ViewModels;
using Chatter.Client.Views;

namespace Chatter.Client;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<ApiAuthService>();
        builder.Services.AddSingleton<ChatService>();     // one HubConnection for the app
        builder.Services.AddSingleton(AudioManager.Current); // voice message record/playback
        builder.Services.AddTransient<ChatViewModel>();   // fresh VM per page
        builder.Services.AddTransient<ChatPage>();        // page that uses the VM

        builder.Services.AddTransient<LoginViewModel>();    // login vm
        builder.Services.AddTransient<LoginPage>();         // login page
        builder.Services.AddTransient<RegisterViewModel>();
        builder.Services.AddTransient<RegisterPage>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}
