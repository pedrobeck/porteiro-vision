using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Vision.Models;

namespace Vision.Services;

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
        sb.AppendLine("  \"h264/cuda\": \"-c:v h264_nvenc -b:v 1500k -maxrate 2000k -bufsize 1000k -g 30 -bf 0 -profile:v high -level:v auto -preset:v p2 -tune:v ll\"");
        sb.AppendLine("  \"h264\": \"-c:v libx264 -b:v 1500k -maxrate 2000k -bufsize 1000k -g 30 -preset:v ultrafast -tune:v zerolatency\"");
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
            string mainUrl = StorageService.BuildRtspUrl(c, "main");
            string subUrl = StorageService.BuildRtspUrl(c, "sub");

            bool isIcsee = c.Brand?.ToLowerInvariant() == "icsee";

            // 1. Sub-stream (SD 640x360) - Leve para grade contínua
            if (isIcsee)
            {
                sb.AppendLine($"  \"{cid}_sub\":");
                sb.AppendLine($"    - \"{subUrl}\"");
                sb.AppendLine($"    - \"ffmpeg:{cid}_sub#video=h264#hardware=cuda\"");
            }
            else
            {
                sb.AppendLine($"  \"{cid}_sub\": \"{subUrl}\"");
            }

            // 2. Main-stream (HD) - Máxima resolução sob demanda
            if (isIcsee)
            {
                sb.AppendLine($"  \"{cid}_hd\":");
                sb.AppendLine($"    - \"{mainUrl}\"");
                sb.AppendLine($"    - \"ffmpeg:{cid}_hd#video=h264#hardware=cuda\"");
            }
            else
            {
                sb.AppendLine($"  \"{cid}_hd\": \"{mainUrl}\"");
            }

            // 3. Identificador padrão {cid} - Aponta para o Sub-stream por padrão (economia total)
            if (isIcsee)
            {
                sb.AppendLine($"  \"{cid}\":");
                sb.AppendLine($"    - \"{subUrl}\"");
                sb.AppendLine($"    - \"ffmpeg:{cid}#video=h264#hardware=cuda\"");
            }
            else
            {
                sb.AppendLine($"  \"{cid}\": \"{subUrl}\"");
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
