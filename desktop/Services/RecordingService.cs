using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PorteiroVision.Models;

namespace PorteiroVision.Services;

public class RecordingService
{
    private readonly string _recordingsDir;
    private readonly string _thumbsDir;
    private readonly string _eventsJsonPath;
    private readonly List<RecordingEvent> _events = new();
    private readonly object _lock = new();

    private readonly ConcurrentDictionary<string, DateTime> _lastRecordingStart = new();
    private readonly ConcurrentDictionary<string, bool> _activeRecordings = new();
    private readonly ConcurrentDictionary<string, string> _activeThumbPaths = new();

    public bool AutoRecordEnabled { get; set; } = true;

    public RecordingService()
    {
        string baseDir = StorageService.ResolveBaseDir();
        _recordingsDir = Path.Combine(baseDir, "recordings");
        _thumbsDir = Path.Combine(_recordingsDir, "thumbs");
        _eventsJsonPath = Path.Combine(_recordingsDir, "events.json");

        EnsureDirectories();
        LoadEvents();
    }

    private void EnsureDirectories()
    {
        if (!Directory.Exists(_recordingsDir))
            Directory.CreateDirectory(_recordingsDir);

        if (!Directory.Exists(_thumbsDir))
            Directory.CreateDirectory(_thumbsDir);
    }

    private void LoadEvents()
    {
        lock (_lock)
        {
            _events.Clear();
            if (File.Exists(_eventsJsonPath))
            {
                try
                {
                    string json = File.ReadAllText(_eventsJsonPath);
                    var list = JsonSerializer.Deserialize<List<RecordingEvent>>(json);
                    if (list != null)
                    {
                        // Filtra eventos cujos arquivos realmente existem no disco
                        foreach (var ev in list.OrderByDescending(e => e.Timestamp))
                        {
                            string vPath = Path.Combine(_recordingsDir, ev.VideoFile);
                            if (File.Exists(vPath))
                            {
                                _events.Add(ev);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RecordingService] Erro ao ler {_eventsJsonPath}: {ex.Message}");
                }
            }
        }
    }

    private void SaveEvents()
    {
        lock (_lock)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_events, options);
                File.WriteAllText(_eventsJsonPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RecordingService] Erro ao salvar {_eventsJsonPath}: {ex.Message}");
            }
        }
    }

    public bool ShouldRecord(string camId)
    {
        if (!AutoRecordEnabled)
            return false;

        if (_activeRecordings.TryGetValue(camId, out bool active) && active)
            return false;

        if (_lastRecordingStart.TryGetValue(camId, out DateTime lastStart))
        {
            if (DateTime.UtcNow - lastStart < TimeSpan.FromSeconds(30))
                return false;
        }

        return true;
    }

    public bool IsCameraRecording(string camId)
    {
        return _activeRecordings.TryGetValue(camId, out bool active) && active;
    }

    public void StartRecording(CameraConfig camera, byte[]? snapshotBytes)
    {
        if (!ShouldRecord(camera.Id)) return;

        _lastRecordingStart[camera.Id] = DateTime.UtcNow;
        _activeRecordings[camera.Id] = true;

        Task.Run(async () =>
        {
            try
            {
                await RecordClipAsync(camera, snapshotBytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RecordingService] Erro na gravação da câmera {camera.Name}: {ex.Message}");
            }
            finally
            {
                _activeRecordings[camera.Id] = false;
                _activeThumbPaths.TryRemove(camera.Id, out _);
            }
        });
    }

    public void UpdateLatestThumbnail(string camId, byte[] newThumbBytes)
    {
        if (_activeThumbPaths.TryGetValue(camId, out string? thumbPath) && !string.IsNullOrEmpty(thumbPath))
        {
            try
            {
                File.WriteAllBytes(thumbPath, newThumbBytes);
                Console.WriteLine($"[RecordingService] Thumbnail atualizada com foto de pico para câmera {camId}");
            }
            catch { }
        }
    }

    private async Task RecordClipAsync(CameraConfig camera, byte[]? snapshotBytes)
    {
        string evtId = $"{DateTime.Now:yyyyMMdd_HHmmss}_{camera.Id[..Math.Min(6, camera.Id.Length)]}";
        string videoFilename = $"{evtId}.mp4";
        string thumbFilename = $"{evtId}.jpg";

        string videoPath = Path.Combine(_recordingsDir, videoFilename);
        string thumbPath = Path.Combine(_thumbsDir, thumbFilename);
        _activeThumbPaths[camera.Id] = thumbPath;

        // 1. Salva thumbnail instantânea do momento do evento
        if (snapshotBytes != null && snapshotBytes.Length > 0)
        {
            try
            {
                await File.WriteAllBytesAsync(thumbPath, snapshotBytes);
            }
            catch { }
        }

        // 2. Gravação de 15s via ffmpeg a partir do RTSP do go2rtc (0% CPU - stream copy)
        // Utiliza o sub-stream que já está ativo em RAM, eliminando 100% de oscilações de encoder no firmware da câmera
        string streamUrl = $"rtsp://127.0.0.1:8554/{camera.Id}_sub";
        bool ok = await RunFfmpegRecordAsync(streamUrl, videoPath, 15);

        if (!ok || !File.Exists(videoPath) || new FileInfo(videoPath).Length < 1000)
        {
            // Fallback para HD apenas se sub falhar
            streamUrl = $"rtsp://127.0.0.1:8554/{camera.Id}_hd";
            ok = await RunFfmpegRecordAsync(streamUrl, videoPath, 15);
        }

        if (!File.Exists(videoPath))
        {
            Console.WriteLine($"[RecordingService] Falha ao gravar clipe para {camera.Name}");
            return;
        }

        var fi = new FileInfo(videoPath);
        if (fi.Length < 1000)
        {
            try { fi.Delete(); } catch { }
            return;
        }

        var recEvent = new RecordingEvent
        {
            Id = evtId,
            CameraId = camera.Id,
            CameraName = camera.Name,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateStr = DateTime.Now.ToString("dd/MM/yyyy"),
            TimeStr = DateTime.Now.ToString("HH:mm:ss"),
            DurationSeconds = 15,
            VideoFile = videoFilename,
            ThumbFile = thumbFilename,
            SizeBytes = fi.Length,
            SizeStr = FormatSize(fi.Length)
        };

        lock (_lock)
        {
            _events.Insert(0, recEvent);
            EnforceRetentionLimit();
        }

        SaveEvents();
        Console.WriteLine($"[RecordingService] Clipe gravado com sucesso: {videoFilename} ({recEvent.SizeStr}) para {camera.Name}");
    }

    private static async Task<bool> RunFfmpegRecordAsync(string rtspUrl, string outputPath, int durationSeconds)
    {
        try
        {
            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { }
            }

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -rtsp_transport tcp -i \"{rtspUrl}\" -t {durationSeconds} -c copy -movflags +faststart \"{outputPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var readErrorTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + 10));
            await proc.WaitForExitAsync(cts.Token);
            await readErrorTask;
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RecordingService] ffmpeg erro: {ex.Message}");
            return false;
        }
    }

    private void EnforceRetentionLimit()
    {
        // Limita a 500 gravações ou ~5 GB
        const int maxClips = 500;
        while (_events.Count > maxClips)
        {
            var oldest = _events[^1];
            _events.RemoveAt(_events.Count - 1);
            DeleteFiles(oldest);
        }
    }

    private void DeleteFiles(RecordingEvent ev)
    {
        try
        {
            string v = Path.Combine(_recordingsDir, ev.VideoFile);
            if (File.Exists(v)) File.Delete(v);
        }
        catch { }

        try
        {
            string t = Path.Combine(_thumbsDir, ev.ThumbFile);
            if (File.Exists(t)) File.Delete(t);
        }
        catch { }
    }

    public List<RecordingEvent> GetEvents(string? cameraId = null, int limit = 100)
    {
        lock (_lock)
        {
            var query = _events.AsEnumerable();
            if (!string.IsNullOrEmpty(cameraId) && cameraId != "ALL")
            {
                query = query.Where(e => e.CameraId == cameraId);
            }
            return query.Take(limit).ToList();
        }
    }

    public bool DeleteEvent(string id)
    {
        lock (_lock)
        {
            var ev = _events.FirstOrDefault(e => e.Id == id);
            if (ev != null)
            {
                _events.Remove(ev);
                DeleteFiles(ev);
                SaveEvents();
                return true;
            }
            return false;
        }
    }

    public string? GetVideoPath(string filename)
    {
        string p = Path.Combine(_recordingsDir, Path.GetFileName(filename));
        return File.Exists(p) ? p : null;
    }

    public string? GetThumbPath(string filename)
    {
        string p = Path.Combine(_thumbsDir, Path.GetFileName(filename));
        return File.Exists(p) ? p : null;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        return $"{(bytes / (1024.0 * 1024.0)):F1} MB";
    }
}
