using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace PorteiroVision;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    public static string LogFile = @"D:\Projetos\PorteiroCam\porteiro_debug.log";

    public static void Trace(string msg)
    {
        try
        {
            File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public App()
    {
        Trace("App constructor entered");

        DispatcherUnhandledException += (s, e) =>
        {
            Trace($"[Dispatcher Exception] {e.Exception}");
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Trace($"[AppDomain Exception] {e.ExceptionObject}");
        };

        Exit += (s, e) =>
        {
            Trace($"App.Exit event triggered with code: {e.ApplicationExitCode}");
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Trace("App.OnStartup entered");
        const string mutexName = "PorteiroVision_SingleInstance_Mutex_v1";
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        Trace($"Mutex createdNew: {createdNew}");

        if (!createdNew)
        {
            Trace("Duplicate instance detected, attempting to restore existing and exit.");
            try
            {
                var current = Process.GetCurrentProcess();
                foreach (var proc in Process.GetProcessesByName(current.ProcessName))
                {
                    if (proc.Id != current.Id && proc.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(proc.MainWindowHandle);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace($"Error restoring existing window: {ex}");
            }

            Current.Shutdown();
            return;
        }

        base.OnStartup(e);
        Trace("base.OnStartup finished");
    }
}
