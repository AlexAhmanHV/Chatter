/*
File: ServiceHelper.cs

What this does:
- Purpose: Simple static gateway to resolve the app-wide DI container (IServiceProvider) from anywhere in the MAUI client.
- How: Exposes Services, which delegates to a platform-specific CurrentServices using MAUI platform singletons
  (Android/iOS/macOS Catalyst/Windows). Throws a clear exception if the provider isn’t ready yet.
*/

using System;

namespace Chatter.Client.Helpers;

public static class ServiceHelper
{
    public static IServiceProvider Services => CurrentServices
        ?? throw new InvalidOperationException("Service provider not available yet.");

    // Platform-specific accessors for MAUI's DI container
    static IServiceProvider? CurrentServices =>
#if ANDROID
        Microsoft.Maui.MauiApplication.Current?.Services;
#elif IOS || MACCATALYST
        UIKit.UIApplication.SharedApplication?.Delegate is Microsoft.Maui.MauiUIApplicationDelegate app ? app.Services : null;
#elif WINDOWS
        Microsoft.Maui.MauiWinUIApplication.Current?.Services;
#else
        null;
#endif
}
