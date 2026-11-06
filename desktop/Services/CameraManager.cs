using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PorteiroVision.Models;

namespace PorteiroVision.Services;

public class CameraManager : IDisposable
{
    private readonly Dictionary<string, CameraWorker> _workers = new();
    private readonly StorageService _storage;
    private readonly object _lock = new();

    public RecordingService RecordingService { get; } = new();

    private bool _nightModeActive = false;
    private string? _activeNightCamId = null;

    public bool IsNightMode => _nightModeActive;
    public string? ActiveNightCamId => _activeNightCamId;

    public CameraManager(StorageService storage)
    {
        _storage = storage;
    }

    public void StartAll()
    {
        var cameras = _storage.LoadCameras();
        Go2RtcService.Start(cameras);

        Console.WriteLine($"[CameraManager] Iniciando {cameras.Count} câmeras cadastradas...");
        foreach (var cam in cameras)
        {
            StartWorker(cam);
        }
    }

    public void StartWorker(CameraConfig camera, bool restartMain = true)
    {
        string camId = camera.Id;
        List<CameraWorker> toStop = new();

        lock (_lock)
        {
            if (restartMain || !_workers.ContainsKey(camId))
            {
                if (_workers.TryGetValue(camId, out var existing))
                {
                    toStop.Add(existing);
                }

                var worker = new CameraWorker(camera, RecordingService);
                worker.Start();
                _workers[camId] = worker;
            }

            // Multi-lente Xiongmai
            if (camera.MultiLens && camera.Brand?.ToLowerInvariant() == "icsee")
            {
                var cameras = _storage.LoadCameras();
                Go2RtcService.Start(cameras);

                for (int subIdx = 1; subIdx <= 2; subIdx++)
                {
                    string subId = $"{camId}_sub{subIdx}";
                    if (!_workers.ContainsKey(subId))
                    {
                        var subCam = new CameraConfig
                        {
                            Id = subId,
                            ParentId = camId,
                            Name = $"{camera.Name} (Fixa {subIdx})",
                            Brand = "generic",
                            Host = "127.0.0.1",
                            Port = 8554,
                            CustomUrl = $"rtsp://127.0.0.1:8554/{camId}_ch{subIdx}",
                            StreamType = "main"
                        };

                        var subWorker = new CameraWorker(subCam, RecordingService);
                        subWorker.Start();
                        _workers[subId] = subWorker;
                    }
                }
            }
            else
            {
                for (int subIdx = 1; subIdx <= 2; subIdx++)
                {
                    string subId = $"{camId}_sub{subIdx}";
                    if (_workers.TryGetValue(subId, out var sw))
                    {
                        toStop.Add(sw);
                        _workers.Remove(subId);
                    }
                }
            }
        }

        foreach (var w in toStop)
        {
            Task.Run(() => w.Stop());
        }
    }

    public void StopWorker(string camId)
    {
        List<CameraWorker> toStop = new();
        lock (_lock)
        {
            string[] ids = [camId, $"{camId}_sub1", $"{camId}_sub2"];
            foreach (var cid in ids)
            {
                if (_workers.TryGetValue(cid, out var w))
                {
                    toStop.Add(w);
                    _workers.Remove(cid);
                }
            }
        }
        foreach (var w in toStop)
        {
            Task.Run(() => w.Stop());
        }
    }

    public void RestartWorker(CameraConfig camera)
    {
        StartWorker(camera, restartMain: true);
    }

    public CameraWorker? GetWorker(string camId)
    {
        lock (_lock)
        {
            _workers.TryGetValue(camId, out var worker);
            return worker;
        }
    }

    public void EnterNightMode(string? selectedCamId, bool recordEvents = true)
    {
        lock (_lock)
        {
            _nightModeActive = true;
            _activeNightCamId = selectedCamId;
            RecordingService.AutoRecordEnabled = recordEvents;

            Console.WriteLine($"[CameraManager] Modo Vigia ativado (Câmera: {selectedCamId ?? "ALL"}, Gravação Automática: {recordEvents})");

            if (string.IsNullOrEmpty(selectedCamId) || selectedCamId == "ALL")
            {
                StartAll();
                return;
            }

            Console.WriteLine($"[CameraManager] Pausando câmeras secundárias para liberar 100% da rede Wi-Fi...");
            var cameras = _storage.LoadCameras();
            foreach (var cam in cameras)
            {
                if (cam.Id != selectedCamId)
                {
                    StopWorker(cam.Id);
                }
                else
                {
                    StartWorker(cam, restartMain: false);
                }
            }

            foreach (var w in _workers.Values)
            {
                w.Motion.Reset();
            }
        }
    }

    public void ExitNightMode()
    {
        lock (_lock)
        {
            _nightModeActive = false;
            _activeNightCamId = null;
            RecordingService.AutoRecordEnabled = true;
            Console.WriteLine("[CameraManager] Modo Noturno/Vigia encerrado. Retomando todas as câmeras e gravação normal...");
            StartAll();
        }
    }

    public void SetMotionSensitivity(string level, string? camId = null)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(camId))
            {
                if (_workers.TryGetValue(camId, out var w))
                {
                    w.Motion.Sensitivity = level;
                }
            }
            else
            {
                foreach (var w in _workers.Values)
                {
                    w.Motion.Sensitivity = level;
                }
            }
        }
    }

    public Dictionary<string, object> GetMotionStatus()
    {
        var result = new Dictionary<string, object>();
        lock (_lock)
        {
            double nowTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            foreach (var (cid, w) in _workers)
            {
                bool isMotion = w.Motion.MotionDetected;
                double lastM = w.Motion.LastMotionTime;
                if (nowTs - lastM >= 4.0)
                {
                    isMotion = false;
                }

                result[cid] = new
                {
                    motion = isMotion,
                    recording = w.IsRecording,
                    last_motion = Math.Round(lastM, 2),
                    name = w.Name,
                    status = w.Status,
                    sensitivity = w.Motion.Sensitivity
                };
            }
        }
        return result;
    }

    public List<CameraInfo> GetAllStatus()
    {
        var statusList = new List<CameraInfo>();
        var cameras = _storage.LoadCameras();

        lock (_lock)
        {
            double nowTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            foreach (var cam in cameras)
            {
                _workers.TryGetValue(cam.Id, out var w);
                bool isMotion = w?.Motion.MotionDetected ?? false;
                double lastM = w?.Motion.LastMotionTime ?? 0.0;
                if (nowTs - lastM >= 4.0)
                {
                    isMotion = false;
                }

                string defaultStatus = (_nightModeActive && !string.IsNullOrEmpty(_activeNightCamId) && _activeNightCamId != "ALL" && cam.Id != _activeNightCamId)
                    ? "STANDBY NOTURNO"
                    : "STOPPED";

                statusList.Add(new CameraInfo
                {
                    Id = cam.Id,
                    Name = cam.Name,
                    Host = cam.Host,
                    Brand = cam.Brand ?? "generic",
                    Status = w?.Status ?? defaultStatus,
                    Fps = w?.Fps ?? 0.0,
                    Motion = isMotion,
                    Recording = w?.IsRecording ?? false,
                    LastSeen = w?.LastSeen?.ToString("HH:mm:ss")
                });

                if (cam.MultiLens && cam.Brand?.ToLowerInvariant() == "icsee")
                {
                    for (int subIdx = 1; subIdx <= 2; subIdx++)
                    {
                        string subId = $"{cam.Id}_sub{subIdx}";
                        _workers.TryGetValue(subId, out var sw);
                        bool swMotion = sw?.Motion.MotionDetected ?? false;
                        double swLastM = sw?.Motion.LastMotionTime ?? 0.0;
                        if (nowTs - swLastM >= 4.0)
                        {
                            swMotion = false;
                        }

                        statusList.Add(new CameraInfo
                        {
                            Id = subId,
                            Name = $"{cam.Name} (Fixa {subIdx})",
                            Host = "127.0.0.1",
                            Brand = "icsee",
                            Status = sw?.Status ?? "STOPPED",
                            Fps = sw?.Fps ?? 0.0,
                            Motion = swMotion,
                            LastSeen = sw?.LastSeen?.ToString("HH:mm:ss")
                        });
                    }
                }
            }
        }
        return statusList;
    }

    public async Task StreamMjpegAsync(string camId, Stream outputStream, CancellationToken cancellationToken)
    {
        var worker = GetWorker(camId);
        long lastCount = -1;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (worker == null)
                {
                    await Task.Delay(500, cancellationToken);
                    worker = GetWorker(camId);
                    continue;
                }

                long currentCount = worker.FrameCount;
                if (currentCount == lastCount)
                {
                    await Task.Delay(30, cancellationToken);
                    continue;
                }

                lastCount = currentCount;
                byte[] frameBytes = worker.GetLatestJpeg();

                string header = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frameBytes.Length}\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                byte[] trailerBytes = "\r\n"u8.ToArray();

                await outputStream.WriteAsync(headerBytes, cancellationToken);
                await outputStream.WriteAsync(frameBytes, cancellationToken);
                await outputStream.WriteAsync(trailerBytes, cancellationToken);
                await outputStream.FlushAsync(cancellationToken);

                // Limita streaming a ~10 FPS para não saturar a rede Wi-Fi doméstica com 3 câmeras simultâneas
                await Task.Delay(100, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    public void Dispose()
    {
        List<CameraWorker> list;
        lock (_lock)
        {
            list = _workers.Values.ToList();
            _workers.Clear();
        }
        foreach (var w in list)
        {
            w.Dispose();
        }
        Go2RtcService.Stop();
    }
}
