using System;
using System.IO;
using System.Windows;

namespace Vision;

public partial class App : System.Windows.Application
{
    public static void Trace(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), $"[{DateTime.Now}] [Dispatcher] {e.Exception}\n");
            }
            catch { }
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), $"[{DateTime.Now}] [AppDomain] {e.ExceptionObject}\n");
            }
            catch { }
        };
    }
}
