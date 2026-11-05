using System;
using System.IO;
using System.Windows;

namespace PorteiroVision;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), $"[{DateTime.Now}] [Dispatcher] {e.Exception}\n");
            }
            catch {}
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), $"[{DateTime.Now}] [AppDomain] {e.ExceptionObject}\n");
            }
            catch {}
        };
    }
}
