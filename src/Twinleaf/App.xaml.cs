// SPDX-License-Identifier: Apache-2.0

using System;
using Microsoft.UI.Xaml;

namespace Twinleaf;

/// <summary>
/// Application entry point. Unpackaged WinUI 3, so there is no MSIX activation
/// contract to honour — <see cref="OnLaunched"/> simply creates the main window.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // A managed exception escaping to the XAML runtime would otherwise
        // terminate with no diagnostic at all in an unpackaged app.
        UnhandledException += (_, args) =>
        {
            AppLog.Error($"Unhandled exception: {args.Exception}");
            args.Handled = false;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}

/// <summary>
/// Console/debug logging, mirroring the Swift app's <c>TwinleafConsole</c>.
/// Debug output is opt-in through the same environment variables the macOS app
/// and <c>tio-bridge</c> honour, so a support session can enable it uniformly.
/// </summary>
public static class AppLog
{
    private static readonly string[] DebugEnvironmentKeys =
    [
        "TWINLEAF_CONSOLE_DEBUG",
        "TWINLEAF_DEBUG_LOGS",
        "TWINLEAF_DEBUG",
    ];

    public static bool IsDebugEnabled { get; } = ComputeDebugEnabled();

    private static bool ComputeDebugEnabled()
    {
        foreach (var key in DebugEnvironmentKeys)
        {
            var value = Environment.GetEnvironmentVariable(key)?.Trim().ToLowerInvariant();
            if (value is "1" or "true" or "yes" or "on")
            {
                return true;
            }
        }

        return false;
    }

    public static void Debug(string message)
    {
        if (!IsDebugEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[Twinleaf] {message}");
        System.Diagnostics.Debug.WriteLine($"[Twinleaf] {message}");
    }

    public static void Error(string message)
    {
        Console.Error.WriteLine($"[Twinleaf] {message}");
        System.Diagnostics.Debug.WriteLine($"[Twinleaf] {message}");
    }
}
