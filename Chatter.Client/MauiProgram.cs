using Microsoft.Extensions.Logging;
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

        builder.Services.AddSingleton(new SupabaseAuthService(
            url: "https://bvzbuxxskzodjvqflgbv.supabase.co",
            anonKey: "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImJ2emJ1eHhza3pvZGp2cWZsZ2J2Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NjA2MDc1OTcsImV4cCI6MjA3NjE4MzU5N30.nL2p01tukPkUVRD2AXQo1s1aHg_JZIaBS-BvwzEmp3g"
        ));
        builder.Services.AddSingleton<ChatService>();     // one HubConnection for the app
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
