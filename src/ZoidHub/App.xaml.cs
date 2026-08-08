using System;
using System.Windows;
using System.Windows.Threading;
using ZoidHub.Services;

namespace ZoidHub;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLogger.Log("ZoidHub started");

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Log($"UNHANDLED UI EXCEPTION: {args.Exception}");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLogger.Log($"UNHANDLED EXCEPTION: {args.ExceptionObject}");
        };
    }
}
