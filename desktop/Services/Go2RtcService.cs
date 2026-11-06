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

    public static void KillOrphanProcesses()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("go2rtc"))
            {
                try
                {
                    p.Kill(true);
                    p.WaitForExit(1000);
                }
                catch { }
            }
        }
        catch { }
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
            else
            {
                KillOrphanProcesses();
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
                Console.WriteLine("[go2rtc] Servidor WebRTC / go2rtc iniciado com sucesso!");
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
        string localIp = WebServer.GetLocalIp();
        var sb = new StringBuilder();
        sb.AppendLine("api:");
        sb.AppendLine("  listen: \"127.0.0.1:1984\"");
        sb.AppendLine();
        sb.AppendLine("rtsp:");
        sb.AppendLine("  listen: \"127.0.0.1:8554\"");
        sb.AppendLine();
        sb.AppendLine("ffmpeg:");
        sb.AppendLine("  bin: \"ffmpeg\"");
        sb.AppendLine();
        sb.AppendLine("webrtc:");
        sb.AppendLine("  listen: \":8555\"");
        sb.AppendLine("  candidates:");
        if (!string.IsNullOrEmpty(localIp) && localIp != "127.0.0.1")
        {
            sb.AppendLine($"    - {localIp}:8555");
        }
        sb.AppendLine("    - 127.0.0.1:8555");
        sb.AppendLine("    - stun:8555");
        sb.AppendLine();
        sb.AppendLine("streams:");

        bool hasStreams = false;
        foreach (var c in cameras)
        {
            if (string.IsNullOrWhiteSpace(c.Id)) continue;
            string cid = c.Id;
            string rtspUrl = !string.IsNullOrWhiteSpace(c.CustomUrl)
                ? c.CustomUrl
                : StorageService.BuildRtspUrl(c);

            bool isIcsee = c.Brand?.ToLowerInvariant() == "icsee";
            if (isIcsee)
            {
                // Suporte dual H.265 + H.264 acelerado via GPU NVENC (elimina faixa verde no Android)
                sb.AppendLine($"  \"{cid}\":");
                sb.AppendLine($"    - \"{rtspUrl}\"");
                sb.AppendLine($"    - \"ffmpeg:{cid}#video=h264#hardware=cuda\"");
            }
            else
            {
                sb.AppendLine($"  \"{cid}\": \"{rtspUrl}\"");
            }
            hasStreams = true;

            // Se for iCSee multi-lente cadastrada
            if (c.MultiLens && c.Brand?.ToLowerInvariant() == "icsee")
            {
                string user = c.Username ?? "admin";
                string pwd = c.Password ?? "";
                string host = string.IsNullOrWhiteSpace(c.Host) ? "127.0.0.1" : c.Host;
                int port = 34567;

                sb.AppendLine($"  \"{cid}_ch0\": \"dvrip://{user}:{pwd}@{host}:{port}?channel=0\"");
                sb.AppendLine($"  \"{cid}_ch1\": \"dvrip://{user}:{pwd}@{host}:{port}?channel=1\"");
                sb.AppendLine($"  \"{cid}_ch2\": \"dvrip://{user}:{pwd}@{host}:{port}?channel=2\"");
                sb.AppendLine($"  \"{cid}_sub1\": \"rtsp://127.0.0.1:8554/{cid}_ch1\"");
                sb.AppendLine($"  \"{cid}_sub2\": \"rtsp://127.0.0.1:8554/{cid}_ch2\"");
            }
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
            }
            KillOrphanProcesses();
            Console.WriteLine("[go2rtc] Servidor WebRTC / go2rtc encerrado.");
        }
    }
}
