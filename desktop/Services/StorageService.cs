using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PorteiroVision.Models;

namespace PorteiroVision.Services;

public class StorageService
{
    private readonly object _lock = new();
    private readonly string _dataDir;
    private readonly string _camerasFile;
    private readonly string _settingsFile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public StorageService(string? baseDir = null)
    {
        baseDir ??= ResolveBaseDir();
        _dataDir = Path.Combine(baseDir, "data");
        _camerasFile = Path.Combine(_dataDir, "cameras.json");
        _settingsFile = Path.Combine(_dataDir, "settings.json");
        EnsureDir();
    }

    public static string ResolveBaseDir()
    {
        string candidate = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(candidate, "static")) && Directory.Exists(Path.Combine(candidate, "templates")))
            return candidate;

        string parent = Path.GetFullPath(Path.Combine(candidate, ".."));
        if (Directory.Exists(Path.Combine(parent, "static")) && Directory.Exists(Path.Combine(parent, "templates")))
            return parent;

        string projDir = @"D:\Projetos\PorteiroCam";
        if (Directory.Exists(Path.Combine(projDir, "static")))
            return projDir;

        return candidate;
    }

    private void EnsureDir()
    {
        try
        {
            if (!Directory.Exists(_dataDir))
            {
                Directory.CreateDirectory(_dataDir);
            }
        }
        catch { }
    }

    public SettingsConfig LoadSettings()
    {
        lock (_lock)
        {
            if (!File.Exists(_settingsFile))
                return new SettingsConfig();

            try
            {
                string json = File.ReadAllText(_settingsFile);
                return JsonSerializer.Deserialize<SettingsConfig>(json, JsonOptions) ?? new SettingsConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Storage] Erro ao carregar configurações: {ex.Message}");
                return new SettingsConfig();
            }
        }
    }

    public bool SaveSettings(SettingsConfig settings)
    {
        lock (_lock)
        {
            try
            {
                EnsureDir();
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(_settingsFile, json);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Storage] Erro ao salvar configurações: {ex.Message}");
                return false;
            }
        }
    }

    public List<CameraConfig> LoadCameras()
    {
        lock (_lock)
        {
            if (!File.Exists(_camerasFile))
                return new List<CameraConfig>();

            try
            {
                string json = File.ReadAllText(_camerasFile);
                return JsonSerializer.Deserialize<List<CameraConfig>>(json, JsonOptions) ?? new List<CameraConfig>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Storage] Erro ao carregar câmeras: {ex.Message}");
                return new List<CameraConfig>();
            }
        }
    }

    public bool SaveCameras(List<CameraConfig> cameras)
    {
        lock (_lock)
        {
            try
            {
                EnsureDir();
                string json = JsonSerializer.Serialize(cameras, JsonOptions);
                File.WriteAllText(_camerasFile, json);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Storage] Erro ao salvar câmeras: {ex.Message}");
                return false;
            }
        }
    }

    public CameraConfig AddCamera(CameraConfig camData)
    {
        lock (_lock)
        {
            var cameras = LoadCameras();
            camData.Id = Guid.NewGuid().ToString("N")[..8];
            if (string.IsNullOrWhiteSpace(camData.Name))
                camData.Name = $"Câmera {cameras.Count + 1}";

            camData.Order = cameras.Count;
            camData.CreatedAt = DateTime.Now.ToString("s");

            if (string.IsNullOrWhiteSpace(camData.CustomUrl))
            {
                camData.CustomUrl = BuildRtspUrl(camData);
            }

            cameras.Add(camData);
            SaveCameras(cameras);
            return camData;
        }
    }

    public CameraConfig? UpdateCamera(string camId, Action<CameraConfig> updater)
    {
        lock (_lock)
        {
            if (camId.Contains("_sub1") || camId.Contains("_sub2"))
                camId = camId.Split('_')[0];

            var cameras = LoadCameras();
            var target = cameras.FirstOrDefault(c => c.Id == camId);
            if (target == null) return null;

            updater(target);

            if (string.IsNullOrWhiteSpace(target.CustomUrl))
                target.CustomUrl = BuildRtspUrl(target);

            SaveCameras(cameras);
            return target;
        }
    }

    public bool DeleteCamera(string camId)
    {
        lock (_lock)
        {
            if (camId.Contains("_sub1") || camId.Contains("_sub2"))
            {
                string parentId = camId.Split('_')[0];
                UpdateCamera(parentId, c => c.MultiLens = false);
                return true;
            }

            var cameras = LoadCameras();
            int removed = cameras.RemoveAll(c => c.Id == camId);
            if (removed > 0)
            {
                SaveCameras(cameras);
                return true;
            }
            return false;
        }
    }

    public CameraConfig? GetCamera(string camId)
    {
        lock (_lock)
        {
            var cameras = LoadCameras();
            var found = cameras.FirstOrDefault(c => c.Id == camId || c.Host == camId);
            if (found != null) return found;

            if (camId.Contains("_sub1") || camId.Contains("_sub2"))
            {
                string parentId = camId.Split('_')[0];
                var parent = cameras.FirstOrDefault(c => c.Id == parentId);
                if (parent != null)
                {
                    int subIdx = camId.EndsWith("_sub1") ? 1 : 2;
                    return new CameraConfig
                    {
                        Id = camId,
                        ParentId = parent.Id,
                        Name = $"{parent.Name} (Fixa {subIdx})",
                        Host = "127.0.0.1",
                        Port = 8554,
                        Brand = "generic",
                        Protocol = "rtsp",
                        Channel = subIdx,
                        StreamType = "main",
                        CustomUrl = $"rtsp://127.0.0.1:8554/{parent.Id}_ch{subIdx}",
                        PtzEnabled = false,
                        MultiLens = false,
                        Order = parent.Order + subIdx * 0.1,
                        CreatedAt = parent.CreatedAt
                    };
                }
            }
            return null;
        }
    }

    public static string BuildRtspUrl(CameraConfig camera, string? streamTypeOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(camera.CustomUrl))
            return camera.CustomUrl;

        string brand = (camera.Brand ?? "generic").ToLowerInvariant();
        string host = string.IsNullOrWhiteSpace(camera.Host) ? "127.0.0.1" : camera.Host;
        int port = camera.Port > 0 ? camera.Port : 554;
        string user = camera.Username ?? "admin";
        string pwd = camera.Password ?? "";
        int ch = camera.Channel > 0 ? camera.Channel : 1;
        string stream = streamTypeOverride ?? camera.StreamType ?? "sub";
        int streamIdx = stream == "main" ? 0 : 1;

        string auth = !string.IsNullOrEmpty(user) ? $"{user}:{pwd}@" : "";

        return brand switch
        {
            "icsee" => $"rtsp://{auth}{host}:{port}/user={user}&pwd={pwd}&channel={ch}&stream={streamIdx}.sdp",
            "intelbras" => $"rtsp://{auth}{host}:{port}/cam/realmonitor?channel={ch}&subtype={streamIdx}",
            "tapo" => $"rtsp://{auth}{host}:{port}/{(streamIdx == 0 ? "stream1" : "stream2")}",
            "hikvision" => $"rtsp://{auth}{host}:{port}/Streaming/Channels/{(streamIdx == 0 ? $"{ch}01" : $"{ch}02")}",
            _ => $"rtsp://{auth}{host}:{port}/live/ch{streamIdx}"
        };
    }
}
