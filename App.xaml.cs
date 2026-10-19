using System;
using System.Windows;
using MongoDBTinyClient.Core;
using MongoDBTinyClient.UI;

namespace MongoDBTinyClient;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled exception", args.Exception);
            MessageBox.Show(args.Exception.Message, "MongoDB Tiny Client",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error("Fatal exception", ex);
        };

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
