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
        builder.Services.AddSingleton<ChatService>();     // one HubConnection for the app
        builder.Services.AddTransient<ChatViewModel>();   // fresh VM per page
        builder.Services.AddTransient<ChatPage>();        // page that uses the VM

        return builder.Build();
    }
}
