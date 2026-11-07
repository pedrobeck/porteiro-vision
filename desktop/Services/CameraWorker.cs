using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using OpenCvSharp;
using Vision.Models;
using Size = OpenCvSharp.Size;
using Point = OpenCvSharp.Point;

namespace Vision.Services;

public class CameraWorker : IDisposable
{
    public CameraConfig Camera { get; }
    public string Id => Camera.Id;
    public string Name => Camera.Name;
    public string Status { get; private set; } = "INIT";
    public double Fps { get; private set; } = 0.0;
    public long FrameCount { get; private set; } = 0;
    public DateTime? LastSeen { get; private set; }
    public MotionDetector Motion { get; } = new();

    private readonly RecordingService? _recordingService;
    private readonly NotificationService? _notificationService;
    public bool IsRecording => _recordingService?.IsCameraRecording(Camera.Id) ?? false;

    private bool _running = false;
    private Thread? _thread;
    private byte[]? _latestJpeg;
    private readonly object _lock = new();

    private bool _snapshotSamplingActive = false;
    private double _bestMotionRatio = 0;
    private byte[]? _bestSnapshot = null;
    private readonly object _snapshotLock = new();

    public CameraWorker(CameraConfig camera, RecordingService? recordingService = null, NotificationService? notificationService = null)
    {
        Camera = camera;
        _recordingService = recordingService;
        _notificationService = notificationService;
        Motion.Sensitivity = camera.MotionSensitivity ?? "medium";
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = $"CamWorker-{Id}"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_thread != null && _thread.IsAlive && _thread != Thread.CurrentThread)
        {
            _thread.Join(300);
        }
        Motion.Dispose();
    }

    public byte[] GetLatestJpeg()
    {
        lock (_lock)
        {
            if (_latestJpeg != null && _latestJpeg.Length > 0)
                return _latestJpeg;

            return GeneratePlaceholder("INICIALIZANDO...");
        }
    }

    private void RunLoop()
    {
        int retryDelayMs = 2000;
        Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp|fflags;nobuffer|max_delay;500000");

        while (_running)
        {
            string url = StorageService.BuildRtspUrl(Camera, "sub");
            string safeUrl = url.Contains('@') ? $"rtsp://***:***@{url.Split('@')[1]}" : url;

            Console.WriteLine($"[Worker {Id}] Conectando em {safeUrl}...");
            Status = "CONNECTING";
            lock (_lock)
            {
                _latestJpeg = GeneratePlaceholder("CONECTANDO...", $"IP: {Camera.Host}");
            }

            using var cap = new VideoCapture(url, VideoCaptureAPIs.FFMPEG);
            cap.Set(VideoCaptureProperties.BufferSize, 1);

            if (!cap.IsOpened())
            {
                Console.WriteLine($"[Worker {Id}] Não foi possível abrir o fluxo RTSP.");
                Status = "OFFLINE";
                lock (_lock)
                {
                    _latestJpeg = GeneratePlaceholder("SEM SINAL", "Verifique o IP, porta e senha");
                }
                Thread.Sleep(retryDelayMs);
                continue;
            }

            Status = "ONLINE";
            Console.WriteLine($"[Worker {Id}] Conectado com sucesso!");

            var fpsTimer = Stopwatch.StartNew();
            int framesInSec = 0;
            long motionFrameSkip = 0;
            using var frame = new Mat();

            while (_running)
            {
                if (!cap.Read(frame) || frame.Empty())
                {
                    Console.WriteLine($"[Worker {Id}] Perda de quadro / conexão encerrada.");
                    Status = "OFFLINE";
                    lock (_lock)
                    {
                        _latestJpeg = GeneratePlaceholder("SINAL PERDIDO", "Tentando reconectar...");
                    }
                    break;
                }

                LastSeen = DateTime.Now;
                FrameCount++;
                framesInSec++;

                int h = frame.Height;
                int w = frame.Width;

                // Redimensiona para 640x360 (resolução ideal para grid CCTV no tablet/web),
                // economizando mais de 75% de banda Wi-Fi e evitando saturação da rede doméstica
                if (w > 640)
                {
                    int newH = (int)(h * (640.0 / w));
                    Cv2.Resize(frame, frame, new Size(640, newH), 0, 0, InterpolationFlags.Area);
                    h = frame.Height;
                    w = frame.Width;
                }

                // Detecção de movimento a cada 2 quadros
                motionFrameSkip++;
                if (motionFrameSkip % 2 == 0)
                {
                    bool moved = Motion.Detect(frame);
                    if (moved)
                    {
                        byte[]? currentSnap = null;
                        lock (_lock) { currentSnap = _latestJpeg; }

                        // 1. Inicia gravação imediatamente no início do movimento para capturar toda a ação
                        if (_recordingService != null && _recordingService.ShouldRecord(Camera.Id))
                        {
                            _recordingService.StartRecording(Camera, currentSnap);
                        }

                        // 2. Amostragem inteligente: aguarda 1.2s para a pessoa entrar no enquadramento
                        // e seleciona o quadro com o maior volume de movimento (evitando sombras iniciais)
                        if (!_snapshotSamplingActive && _notificationService != null && _notificationService.ShouldNotify(Camera.Id))
                        {
                            lock (_snapshotLock)
                            {
                                _snapshotSamplingActive = true;
                                _bestMotionRatio = Motion.LastMotionRatio;
                                _bestSnapshot = currentSnap;
                            }

                            Console.WriteLine($"[CameraWorker] Movimento detectado em {Name}. Acumulando quadros por 2.2s para capturar a pessoa centralizada...");

                            Task.Run(async () =>
                            {
                                await Task.Delay(2200);

                                byte[]? snapToSend;
                                lock (_snapshotLock)
                                {
                                    snapToSend = _bestSnapshot;
                                    _snapshotSamplingActive = false;
                                    _bestMotionRatio = 0;
                                    _bestSnapshot = null;
                                }

                                if (snapToSend == null)
                                {
                                    lock (_lock) { snapToSend = _latestJpeg; }
                                }

                                if (snapToSend != null)
                                {
                                    Console.WriteLine($"[CameraWorker] Foto de pico capturada com sucesso ({Name})! Disparando Toast no Windows...");
                                    _notificationService.NotifyMotion(Camera, snapToSend);
                                    _recordingService?.UpdateLatestThumbnail(Camera.Id, snapToSend);
                                }
                            });
                        }
                    }
                }

                // FPS
                if (fpsTimer.ElapsedMilliseconds >= 1000)
                {
                    Fps = Math.Round(framesInSec / (fpsTimer.ElapsedMilliseconds / 1000.0), 1);
                    framesInSec = 0;
                    fpsTimer.Restart();
                }

                // Estampa d'água CCTV no canto inferior esquerdo
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string hudText = $"{Name} | {ts} | {Fps:F1} FPS";
                var textPos = new Point(10, h - 10);

                // Contorno preto
                Cv2.PutText(frame, hudText, textPos, HersheyFonts.HersheySimplex, 0.45, Scalar.Black, 2, LineTypes.AntiAlias);
                // Texto branco
                Cv2.PutText(frame, hudText, textPos, HersheyFonts.HersheySimplex, 0.45, Scalar.White, 1, LineTypes.AntiAlias);

                // Codificação JPEG leve (qualidade 55) otimizada para streaming fluido sem engasgar o Wi-Fi
                Cv2.ImEncode(".jpg", frame, out byte[] jpgBytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, 55));

                lock (_lock)
                {
                    _latestJpeg = jpgBytes;
                }

                if (_snapshotSamplingActive)
                {
                    lock (_snapshotLock)
                    {
                        if (Motion.LastMotionRatio >= _bestMotionRatio)
                        {
                            _bestMotionRatio = Motion.LastMotionRatio;
                            _bestSnapshot = jpgBytes;
                        }
                    }
                }

                if (FrameCount % 300 == 0)
                {
                    GC.Collect(1, GCCollectionMode.Optimized);
                }

                Thread.Sleep(15);
            }

            try { cap.Release(); } catch { }
            Thread.Sleep(retryDelayMs);
        }
    }

    private byte[] GeneratePlaceholder(string statusText, string detailText = "")
    {
        using var img = new Mat(360, 640, MatType.CV_8UC3, new Scalar(18, 22, 28));

        // Grid CCTV
        for (int y = 0; y < 360; y += 40)
            Cv2.Line(img, new Point(0, y), new Point(640, y), new Scalar(25, 32, 40), 1);
        for (int x = 0; x < 640; x += 40)
            Cv2.Line(img, new Point(x, 0), new Point(x, 360), new Scalar(25, 32, 40), 1);

        // Mira HUD nos 4 cantos
        var hudColor = statusText == "ONLINE" ? new Scalar(0, 215, 255) : new Scalar(70, 80, 95);
        Cv2.Line(img, new Point(20, 20), new Point(50, 20), hudColor, 2);
        Cv2.Line(img, new Point(20, 20), new Point(20, 50), hudColor, 2);
        Cv2.Line(img, new Point(620, 20), new Point(590, 20), hudColor, 2);
        Cv2.Line(img, new Point(620, 20), new Point(620, 50), hudColor, 2);
        Cv2.Line(img, new Point(20, 340), new Point(50, 340), hudColor, 2);
        Cv2.Line(img, new Point(20, 340), new Point(20, 310), hudColor, 2);
        Cv2.Line(img, new Point(620, 340), new Point(590, 340), hudColor, 2);
        Cv2.Line(img, new Point(620, 340), new Point(620, 310), hudColor, 2);

        // Identificação da câmera
        Cv2.PutText(img, $"CAM: {Name.ToUpper()}", new Point(30, 45), HersheyFonts.HersheySimplex, 0.6, new Scalar(200, 220, 240), 2, LineTypes.AntiAlias);
        Cv2.PutText(img, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), new Point(400, 45), HersheyFonts.HersheySimplex, 0.5, new Scalar(140, 160, 180), 1, LineTypes.AntiAlias);

        // Badge de Status Central
        var badgeColor = statusText.Contains("CONECTANDO") ? new Scalar(0, 165, 255) : new Scalar(0, 0, 220);
        Cv2.PutText(img, statusText, new Point(200, 180), HersheyFonts.HersheyDuplex, 0.9, badgeColor, 2, LineTypes.AntiAlias);

        if (!string.IsNullOrEmpty(detailText))
        {
            string d = detailText.Length > 45 ? detailText[..45] : detailText;
            Cv2.PutText(img, d, new Point(140, 215), HersheyFonts.HersheySimplex, 0.5, new Scalar(130, 140, 150), 1, LineTypes.AntiAlias);
        }

        Cv2.ImEncode(".jpg", img, out byte[] jpg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 75));
        return jpg;
    }

    public void Dispose()
    {
        Stop();
    }
}
