using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using PorteiroVision.Models;

namespace PorteiroVision.Services;

public class WebServer : IDisposable
{
    private WebApplication? _app;
    private readonly StorageService _storage;
    private readonly CameraManager _cameraManager;
    private readonly SystemMonitor _systemMonitor;
    private readonly string _baseDir;
    private readonly string _staticDir;
    private readonly string _templateDir;

    public int Port { get; } = 8090;
    public string LocalIp { get; }

    public WebServer(StorageService storage, CameraManager cameraManager, SystemMonitor systemMonitor)
    {
        _storage = storage;
        _cameraManager = cameraManager;
        _systemMonitor = systemMonitor;

        _baseDir = StorageService.ResolveBaseDir();
        _staticDir = Path.Combine(_baseDir, "static");
        _templateDir = Path.Combine(_baseDir, "templates");

        LocalIp = GetLocalIp();
    }

    public static string GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                return endPoint.Address.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Any, Port);
        });

        builder.Services.AddCors();

        _app = builder.Build();

        _app.UseCors(policy => policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
        );

        _app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        });

        ConfigureRoutes(_app);

        await _app.StartAsync();
        Console.WriteLine($"[WebServer] Kestrel C# rodando em http://0.0.0.0:{Port} (IP Local: {LocalIp})");
    }

    private void ConfigureRoutes(WebApplication app)
    {
        // 1. Página inicial (templates/index.html)
        app.MapGet("/", async (HttpContext ctx) =>
        {
            string indexPath = Path.Combine(_templateDir, "index.html");
            if (!File.Exists(indexPath))
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Template index.html não encontrado.");
                return;
            }

            string html = await File.ReadAllTextAsync(indexPath);
            html = html.Replace("{{ server_ip }}", LocalIp);

            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(html);
        });

        // 2. Manifest & Service Worker
        app.MapGet("/manifest.json", async (HttpContext ctx) =>
        {
            string p = Path.Combine(_staticDir, "manifest.json");
            if (File.Exists(p))
            {
                ctx.Response.ContentType = "application/manifest+json";
                await ctx.Response.SendFileAsync(p);
            }
            else ctx.Response.StatusCode = 404;
        });

        app.MapGet("/sw.js", async (HttpContext ctx) =>
        {
            string p = Path.Combine(_staticDir, "sw.js");
            if (File.Exists(p))
            {
                ctx.Response.ContentType = "application/javascript";
                await ctx.Response.SendFileAsync(p);
            }
            else ctx.Response.StatusCode = 404;
        });

        // 3. Download APK
        app.MapGet("/download/Vision.apk", ServeApk);
        app.MapGet("/download/PorteiroVision.apk", ServeApk);
        app.MapGet("/download/apk", ServeApk);

        async Task ServeApk(HttpContext ctx)
        {
            string apkPath = Path.Combine(_staticDir, "Vision.apk");
            if (!File.Exists(apkPath))
            {
                apkPath = Path.Combine(_staticDir, "PorteiroVision.apk");
            }
            if (File.Exists(apkPath))
            {
                ctx.Response.ContentType = "application/vnd.android.package-archive";
                ctx.Response.Headers["Content-Disposition"] = "attachment; filename=\"Vision.apk\"";
                await ctx.Response.SendFileAsync(apkPath);
            }
            else
            {
                ctx.Response.Redirect("https://github.com/pedrobeck/porteiro-vision/releases/latest");
            }
        }

        // 4. Arquivos estáticos (/static/...)
        app.MapGet("/static/{**filePath}", async (HttpContext ctx, string filePath) =>
        {
            string fullPath = Path.Combine(_staticDir, filePath);
            if (!File.Exists(fullPath))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            ctx.Response.ContentType = ext switch
            {
                ".js" => "application/javascript",
                ".css" => "text/css",
                ".json" => "application/json",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".svg" => "image/svg+xml",
                ".ico" => "image/x-icon",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".apk" => "application/vnd.android.package-archive",
                _ => "application/octet-stream"
            };

            ctx.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
            await ctx.Response.SendFileAsync(fullPath);
        });

        // 5. API de Status
        app.MapGet("/api/status", () =>
        {
            return Results.Json(new
            {
                server_ip = LocalIp,
                cameras = _cameraManager.GetAllStatus(),
                system = _systemMonitor.GetStats()
            });
        });

        // 6. API de Movimento
        app.MapGet("/api/motion", () => Results.Json(_cameraManager.GetMotionStatus()));

        app.MapGet("/api/motion/config", () =>
        {
            var settings = _storage.LoadSettings();
            var profiles = MotionDetector.Profiles.ToDictionary(k => k.Key, v => v.Value.Label);
            return Results.Json(new
            {
                current_sensitivity = settings.MotionSensitivity ?? "medium",
                profiles
            });
        });

        app.MapPost("/api/motion/config", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            string? sensitivity = null;
            string? camId = null;

            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("sensitivity", out var sProp)) sensitivity = sProp.GetString();
                    if (doc.RootElement.TryGetProperty("camera_id", out var cProp)) camId = cProp.GetString();
                }
                catch { }
            }

            if (sensitivity != null && MotionDetector.Profiles.ContainsKey(sensitivity))
            {
                _cameraManager.SetMotionSensitivity(sensitivity, camId);
                if (!string.IsNullOrEmpty(camId))
                {
                    _storage.UpdateCamera(camId, c => c.MotionSensitivity = sensitivity);
                }
                else
                {
                    _storage.SaveSettings(new SettingsConfig { MotionSensitivity = sensitivity });
                    var cams = _storage.LoadCameras();
                    foreach (var c in cams) c.MotionSensitivity = sensitivity;
                    _storage.SaveCameras(cams);
                }
                return Results.Json(new { status = "ok", sensitivity, camera_id = camId });
            }

            return Results.Json(new { error = "Perfil de sensibilidade inválido" }, statusCode: 400);
        });

        // 6.1 API de Modo Noturno (Controle de Câmeras em Standby / Economia Total de Wi-Fi)
        app.MapPost("/api/night_mode/enter", async (HttpContext ctx) =>
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                string body = await reader.ReadToEndAsync();
                string camId = "ALL";
                bool recordEvents = true;
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("camera_id", out var cProp))
                        {
                            camId = cProp.GetString() ?? "ALL";
                        }
                        if (doc.RootElement.TryGetProperty("record_events", out var rProp))
                        {
                            recordEvents = rProp.GetBoolean();
                        }
                    }
                    catch { }
                }

                _cameraManager.EnterNightMode(camId, recordEvents);
                return Results.Json(new { status = "ok", night_mode = true, camera_id = camId, record_events = recordEvents });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebServer] Erro no Modo Noturno: {ex}");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapPost("/api/night_mode/exit", () =>
        {
            _cameraManager.ExitNightMode();
            return Results.Json(new { status = "ok", night_mode = false });
        });

        app.MapGet("/api/night_mode/status", () =>
        {
            return Results.Json(new
            {
                night_mode = _cameraManager.IsNightMode,
                active_camera_id = _cameraManager.ActiveNightCamId
            });
        });

        // 7. API de Câmeras
        app.MapGet("/api/cameras", () =>
        {
            var cameras = _storage.LoadCameras();
            var expanded = new List<CameraConfig>();

            foreach (var c in cameras)
            {
                expanded.Add(c);
                if (c.MultiLens && c.Brand?.ToLowerInvariant() == "icsee")
                {
                    for (int subIdx = 1; subIdx <= 2; subIdx++)
                    {
                        expanded.Add(new CameraConfig
                        {
                            Id = $"{c.Id}_sub{subIdx}",
                            ParentId = c.Id,
                            Name = $"{c.Name} (Fixa {subIdx})",
                            Host = c.Host,
                            Port = 8554,
                            Brand = "icsee",
                            Protocol = "rtsp",
                            PtzEnabled = false,
                            IsSubLens = true,
                            Order = c.Order + subIdx * 0.1
                        });
                    }
                }
            }

            var allStatus = _cameraManager.GetAllStatus().ToDictionary(s => s.Id);
            foreach (var c in expanded)
            {
                if (allStatus.TryGetValue(c.Id, out var st))
                {
                    c.Status = st.Status;
                    c.Fps = st.Fps;
                    c.LastSeen = st.LastSeen;
                }
                else
                {
                    c.Status = "OFFLINE";
                    c.Fps = 0.0;
                }
            }

            return Results.Json(expanded);
        });

        app.MapPost("/api/cameras", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            var cam = JsonSerializer.Deserialize<CameraConfig>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (cam == null || string.IsNullOrWhiteSpace(cam.Host))
            {
                return Results.Json(new { error = "Host/IP da câmera é obrigatório" }, statusCode: 400);
            }

            var added = _storage.AddCamera(cam);
            _ = Task.Run(() =>
            {
                _cameraManager.StartWorker(added);
                Go2RtcService.Start(_storage.LoadCameras());
            });
            return Results.Json(added, statusCode: 201);
        });

        app.MapGet("/api/cameras/{camId}", (string camId) =>
        {
            var cam = _storage.GetCamera(camId);
            return cam != null ? Results.Json(cam) : Results.Json(new { error = "Câmera não encontrada" }, statusCode: 404);
        });

        app.MapPut("/api/cameras/{camId}", async (string camId, HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            var updateData = JsonSerializer.Deserialize<CameraConfig>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (updateData == null) return Results.Json(new { error = "Dados inválidos" }, statusCode: 400);

            var updated = _storage.UpdateCamera(camId, c =>
            {
                if (!string.IsNullOrWhiteSpace(updateData.Name)) c.Name = updateData.Name;
                if (!string.IsNullOrWhiteSpace(updateData.Host)) c.Host = updateData.Host;
                if (updateData.Port > 0) c.Port = updateData.Port;
                if (!string.IsNullOrWhiteSpace(updateData.Brand)) c.Brand = updateData.Brand;
                if (!string.IsNullOrWhiteSpace(updateData.Username)) c.Username = updateData.Username;
                if (!string.IsNullOrWhiteSpace(updateData.Password)) c.Password = updateData.Password;
                if (updateData.Channel > 0) c.Channel = updateData.Channel;
                if (!string.IsNullOrWhiteSpace(updateData.StreamType)) c.StreamType = updateData.StreamType;
                if (!string.IsNullOrWhiteSpace(updateData.CustomUrl)) c.CustomUrl = updateData.CustomUrl;
                c.PtzEnabled = updateData.PtzEnabled;
                c.MultiLens = updateData.MultiLens;
                if (!string.IsNullOrWhiteSpace(updateData.MotionSensitivity)) c.MotionSensitivity = updateData.MotionSensitivity;
            });

            if (updated == null) return Results.Json(new { error = "Câmera não encontrada" }, statusCode: 404);

            _ = Task.Run(() =>
            {
                _cameraManager.RestartWorker(updated);
                Go2RtcService.Start(_storage.LoadCameras());
            });
            return Results.Json(updated);
        });

        app.MapDelete("/api/cameras/{camId}", (string camId) =>
        {
            if (_storage.DeleteCamera(camId))
            {
                _ = Task.Run(() =>
                {
                    _cameraManager.StopWorker(camId);
                    Go2RtcService.Start(_storage.LoadCameras());
                });
                return Results.Json(new { success = true });
            }
            return Results.Json(new { error = "Câmera não encontrada" }, statusCode: 404);
        });

        // 8. Toggle Multi-lens
        app.MapPost("/api/cameras/{camId}/toggle_multilens", (string camId) =>
        {
            var cam = _storage.GetCamera(camId);
            if (cam == null) return Results.Json(new { error = "Câmera não encontrada" }, statusCode: 404);

            bool newState = !cam.MultiLens;
            var updated = _storage.UpdateCamera(camId, c => c.MultiLens = newState);
            if (updated != null)
            {
                _ = Task.Run(() =>
                {
                    _cameraManager.StartWorker(updated, restartMain: false);
                    Go2RtcService.Start(_storage.LoadCameras());
                });
            }
            return Results.Json(new { success = true, multi_lens = newState });
        });

        // 9. Teste de Câmera RTSP
        app.MapPost("/api/cameras/test", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            var testCam = JsonSerializer.Deserialize<CameraConfig>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new CameraConfig();

            string url = StorageService.BuildRtspUrl(testCam);
            Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp|fflags;nobuffer|max_delay;500000|stimeout;3000000");

            return await Task.Run(() =>
            {
                try
                {
                    using var cap = new VideoCapture(url, VideoCaptureAPIs.FFMPEG);

                    if (cap.IsOpened())
                    {
                        using var frame = new Mat();
                        bool readOk = cap.Read(frame);
                        if (readOk && !frame.Empty())
                        {
                            return Results.Json(new
                            {
                                success = true,
                                message = $"Conexão bem-sucedida! Resolução: {frame.Width}x{frame.Height}"
                            });
                        }
                        return Results.Json(new { success = true, message = "Stream RTSP aberto, aguardando quadros." });
                    }

                    return Results.Json(new { success = false, message = "Não foi possível conectar. Verifique IP, porta RTSP e senha." }, statusCode: 400);
                }
                catch (Exception ex)
                {
                    return Results.Json(new { success = false, message = $"Erro ao testar stream: {ex.Message}" }, statusCode: 400);
                }
            });
        });

        // 10. PTZ Control
        app.MapPost("/api/cameras/{camId}/ptz", async (string camId, HttpContext ctx) =>
        {
            var cam = _storage.GetCamera(camId);
            if (cam == null) return Results.Json(new { error = "Câmera não encontrada" }, statusCode: 404);

            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);

            string cmd = doc.RootElement.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() ?? "stop" : "stop";
            int step = doc.RootElement.TryGetProperty("step", out var stepProp) ? stepProp.GetInt32() : 4;

            string dvripCmd = cmd.ToLowerInvariant() switch
            {
                "up" => "DirectionUp",
                "down" => "DirectionDown",
                "left" => "DirectionLeft",
                "right" => "DirectionRight",
                "zoomin" => "IrisLarge",
                "zoomout" => "IrisSmall",
                _ => "Stop"
            };

            using var dvrip = new DvripClient(cam.Host, 34567, cam.Username, cam.Password);
            bool ok = await dvrip.PtzControlAsync(dvripCmd, step);

            return Results.Json(new { success = ok, command = dvripCmd });
        });

        // 11. Stream MJPEG
        app.MapGet("/api/stream/{camId}", async (string camId, HttpContext ctx) =>
        {
            var cam = _storage.GetCamera(camId);
            if (cam == null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Câmera não encontrada");
                return;
            }

            var worker = _cameraManager.GetWorker(cam.Id);
            if (worker == null)
            {
                _cameraManager.StartWorker(cam);
            }

            ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate, max-age=0";
            ctx.Response.Headers["Pragma"] = "no-cache";
            ctx.Response.Headers["Expires"] = "0";
            ctx.Response.Headers["Connection"] = "close";

            await _cameraManager.StreamMjpegAsync(cam.Id, ctx.Response.Body, ctx.RequestAborted);
        });

        // 12. Snapshot JPEG
        app.MapGet("/api/snapshot/{camId}", async (string camId, HttpContext ctx) =>
        {
            var cam = _storage.GetCamera(camId);
            if (cam == null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Câmera não encontrada");
                return;
            }

            var worker = _cameraManager.GetWorker(cam.Id);
            if (worker == null)
            {
                _cameraManager.StartWorker(cam);
                await Task.Delay(500);
                worker = _cameraManager.GetWorker(cam.Id);
            }

            byte[] jpeg = worker?.GetLatestJpeg() ?? Array.Empty<byte>();

            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate, max-age=0";
            ctx.Response.Headers["Pragma"] = "no-cache";
            ctx.Response.Headers["Expires"] = "0";

            await ctx.Response.Body.WriteAsync(jpeg);
        });

        // Token endpoint
        app.MapGet("/api/token", () => Results.Json(new { token = "local" }));

        // 12.1 Gravações & Linha do Tempo de Eventos (Fase 3)
        app.MapGet("/api/recordings", (HttpContext ctx) =>
        {
            string? camId = ctx.Request.Query["camera_id"].FirstOrDefault();
            int limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out int l) ? l : 100;
            var events = _cameraManager.RecordingService.GetEvents(camId, limit);
            return Results.Json(events);
        });

        app.MapMethods("/api/recordings/video/{filename}", ["GET", "HEAD"], async (string filename, HttpContext ctx) =>
        {
            string? path = _cameraManager.RecordingService.GetVideoPath(filename);
            if (path == null || !File.Exists(path))
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Vídeo não encontrado.");
                return;
            }

            ctx.Response.ContentType = "video/mp4";
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            ctx.Response.Headers["Cache-Control"] = "public, max-age=86400";
            await ctx.Response.SendFileAsync(path);
        });

        app.MapMethods("/api/recordings/thumb/{filename}", ["GET", "HEAD"], async (string filename, HttpContext ctx) =>
        {
            string? path = _cameraManager.RecordingService.GetThumbPath(filename);
            if (path == null || !File.Exists(path))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.Headers["Cache-Control"] = "public, max-age=86400";
            await ctx.Response.SendFileAsync(path);
        });

        app.MapDelete("/api/recordings/{id}", (string id) =>
        {
            bool ok = _cameraManager.RecordingService.DeleteEvent(id);
            return ok ? Results.Json(new { success = true }) : Results.Json(new { error = "Gravação não encontrada" }, statusCode: 404);
        });

        app.MapPost("/api/recordings/trigger/{camId}", (string camId) =>
        {
            var cam = _storage.GetCamera(camId);
            if (cam == null) return Results.Json(new { error = "Câmera não encontrada" }, statusCode: 404);

            var worker = _cameraManager.GetWorker(camId);
            byte[]? snap = worker?.GetLatestJpeg();
            _cameraManager.RecordingService.StartRecording(cam, snap);
            return Results.Json(new { success = true, message = $"Gravação manual de 15s iniciada para {cam.Name}" });
        });

        // 12.1 Notificações Nativas do Windows (Toast)
        app.MapGet("/api/notifications/status", () =>
        {
            var settings = _storage.LoadSettings();
            return Results.Json(new
            {
                enabled = _cameraManager.NotificationService.IsEnabled,
                only_background = _cameraManager.NotificationService.OnlyInBackground,
                cooldown_seconds = _cameraManager.NotificationService.CooldownSeconds
            });
        });

        app.MapPost("/api/notifications/toggle", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var settings = _storage.LoadSettings();

            if (doc.RootElement.TryGetProperty("enabled", out var enElem))
            {
                bool en = enElem.GetBoolean();
                _cameraManager.NotificationService.IsEnabled = en;
                settings.DesktopNotifications = en;
            }

            if (doc.RootElement.TryGetProperty("only_background", out var obElem))
            {
                bool ob = obElem.GetBoolean();
                _cameraManager.NotificationService.OnlyInBackground = ob;
                settings.NotificationsOnlyBackground = ob;
            }

            _storage.SaveSettings(settings);

            return Results.Json(new
            {
                status = "ok",
                enabled = _cameraManager.NotificationService.IsEnabled,
                only_background = _cameraManager.NotificationService.OnlyInBackground
            });
        });

        app.MapPost("/api/notifications/test", () =>
        {
            bool ok = _cameraManager.NotificationService.SendTestNotification(null, "Vision - Notificação de Teste");
            return Results.Json(new { success = ok, message = ok ? "Notificação de teste disparada no Windows!" : "Falha ao disparar notificação" });
        });

        app.MapPost("/api/notifications/test-motion/{camId}", (string camId) =>
        {
            var cam = _storage.GetCamera(camId);
            if (cam == null) return Results.Json(new { error = "Câmera não encontrada" }, statusCode: 404);

            var worker = _cameraManager.GetWorker(camId);
            byte[]? snap = worker?.GetLatestJpeg();
            _cameraManager.NotificationService.NotifyMotion(cam, snap);
            return Results.Json(new { success = true, message = $"Simulação de movimento disparada para {cam.Name}!" });
        });

        // 13. WebRTC / WebSocket Proxy para go2rtc
        app.Map("/api/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("WebSocket request expected.");
                return;
            }

            using var clientWs = await ctx.WebSockets.AcceptWebSocketAsync();
            using var serverWs = new ClientWebSocket();
            var targetUri = new Uri($"ws://127.0.0.1:1984/api/ws{ctx.Request.QueryString}");

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                await serverWs.ConnectAsync(targetUri, cts.Token);

                var t1 = ForwardWebSocketStream(clientWs, serverWs, cts);
                var t2 = ForwardWebSocketStream(serverWs, clientWs, cts);

                await Task.WhenAny(t1, t2);
                cts.Cancel();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebServer] Erro no túnel WS go2rtc: {ex.Message}");
            }
        });

        // 14. WebRTC SDP POST Proxy para go2rtc
        app.MapPost("/api/webrtc", async (HttpContext ctx) =>
        {
            try
            {
                var uri = $"http://127.0.0.1:1984/api/webrtc{ctx.Request.QueryString}";
                using var httpClient = new HttpClient();
                using var request = new HttpRequestMessage(HttpMethod.Post, uri);
                using var content = new StreamContent(ctx.Request.Body);
                if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                {
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ctx.Request.ContentType);
                }
                request.Content = content;

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                ctx.Response.StatusCode = (int)response.StatusCode;
                ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "text/plain";
                await response.Content.CopyToAsync(ctx.Response.Body);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebServer] Erro no proxy WebRTC SDP: {ex.Message}");
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsync($"Erro ao contatar go2rtc: {ex.Message}");
            }
        });
    }

    private static async Task ForwardWebSocketStream(WebSocket source, WebSocket destination, CancellationTokenSource cts)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var res = await source.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    if (destination.State == WebSocketState.Open)
                    {
                        await destination.CloseAsync(res.CloseStatus ?? WebSocketCloseStatus.NormalClosure, res.CloseStatusDescription, CancellationToken.None);
                    }
                    break;
                }

                await destination.SendAsync(new ArraySegment<byte>(buffer, 0, res.Count), res.MessageType, res.EndOfMessage, cts.Token);
            }
        }
        catch { }
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
