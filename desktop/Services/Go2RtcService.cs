using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PorteiroVision.Models;

namespace PorteiroVision.Services;

public class Go2RtcService
{
    private static Process? _proc;
    private static string _lastConfig = "";
    private static readonly object _lock = new();

    public static string GetBinPath()
    {
        string baseDir = StorageService.ResolveBaseDir();
        return Path.Combine(baseDir, "go2rtc.exe");
    }

    public static string GetConfigPath()
    {
        string baseDir = StorageService.ResolveBaseDir();
        return Path.Combine(baseDir, "go2rtc.yaml");
    }

    public static bool Start(List<CameraConfig> cameras)
    {
        lock (_lock)
        {
            string binPath = GetBinPath();
            if (!File.Exists(binPath))
            {
                Console.WriteLine($"[go2rtc] Binário não encontrado em {binPath}");
                return false;
            }

            bool changed = GenerateConfig(cameras);

            if (_proc != null && !_proc.HasExited)
            {
                if (!changed) return true;
                Stop();
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = binPath,
                    Arguments = $"-config \"{GetConfigPath()}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                _proc = Process.Start(psi);
                System.Threading.Thread.Sleep(1200);
                Console.WriteLine("[go2rtc] Servidor multi-lente iniciado com sucesso!");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[go2rtc] Erro ao iniciar: {ex.Message}");
                return false;
            }
        }
    }

    public static bool GenerateConfig(List<CameraConfig> cameras)
    {
        var sb = new StringBuilder();
        sb.AppendLine("api:");
        sb.AppendLine("  listen: \"127.0.0.1:1984\"");
        sb.AppendLine();
        sb.AppendLine("rtsp:");
        sb.AppendLine("  listen: \"127.0.0.1:8554\"");
        sb.AppendLine();
        sb.AppendLine("streams:");

        bool hasStreams = false;
        foreach (var c in cameras.Where(c => c.Brand?.ToLowerInvariant() == "icsee"))
        {
            string cid = c.Id;
            string user = c.Username ?? "admin";
            string pwd = c.Password ?? "";
            string host = string.IsNullOrWhiteSpace(c.Host) ? "127.0.0.1" : c.Host;
            int port = 34567;

            sb.AppendLine($"  {cid}_ch0: \"dvrip://{user}:{pwd}@{host}:{port}?channel=0\"");
            sb.AppendLine($"  {cid}_ch1: \"dvrip://{user}:{pwd}@{host}:{port}?channel=1\"");
            sb.AppendLine($"  {cid}_ch2: \"dvrip://{user}:{pwd}@{host}:{port}?channel=2\"");
            hasStreams = true;
        }

        if (!hasStreams)
        {
            sb.AppendLine("  dummy: \"ffmpeg:null\"");
        }

        string newConfig = sb.ToString();
        bool changed = (newConfig != _lastConfig) || !File.Exists(GetConfigPath());

        if (changed)
        {
            File.WriteAllText(GetConfigPath(), newConfig);
            _lastConfig = newConfig;
        }

        return changed;
    }

    public static void Stop()
    {
        lock (_lock)
        {
            if (_proc != null)
            {
                try
                {
                    if (!_proc.HasExited)
                    {
                        _proc.Kill(true);
                        _proc.WaitForExit(2000);
                    }
                }
                catch { }
                _proc = null;
                Console.WriteLine("[go2rtc] Servidor multi-lente encerrado.");
            }
        }
    }
}
