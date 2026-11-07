using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.Notifications;
using Vision.Models;

namespace Vision.Services;

public class NotificationService
{
    private readonly string _notificationsDir;
    private readonly string _brandLogoPath;
    private readonly ConcurrentDictionary<string, DateTime> _lastNotificationPerCam = new();
    private static bool _activatedHandlerRegistered = false;
    private static readonly object _initLock = new();

    public bool IsEnabled { get; set; } = true;
    public bool OnlyInBackground { get; set; } = false;
    public int CooldownSeconds { get; set; } = 15;

    public Func<bool>? IsAppInBackground { get; set; }
    public Action? OnRestoreRequested { get; set; }
    public Action? OnOpenBrowserRequested { get; set; }

    public NotificationService()
    {
        string baseDir = StorageService.ResolveBaseDir();
        _notificationsDir = Path.Combine(baseDir, "recordings", "notifications");
        _brandLogoPath = Path.Combine(baseDir, "static", "brand", "png", "icone", "vision-icone-app-512.png");

        try
        {
            if (!Directory.Exists(_notificationsDir))
            {
                Directory.CreateDirectory(_notificationsDir);
            }
        }
        catch { }

        RegisterActivationHandler();
    }

    private void RegisterActivationHandler()
    {
        lock (_initLock)
        {
            if (_activatedHandlerRegistered) return;
            _activatedHandlerRegistered = true;

            try
            {
                ToastNotificationManagerCompat.OnActivated += toastArgs =>
                {
                    try
                    {
                        var args = ToastArguments.Parse(toastArgs.Argument);
                        string action = args.TryGetValue("action", out string? act) ? (act ?? "open") : "open";

                        Console.WriteLine($"[NotificationService] Toast clicado com ação: '{action}'");

                        if (action == "browser")
                        {
                            OnOpenBrowserRequested?.Invoke();
                        }
                        else
                        {
                            // "open", "viewMotion", ou clique direto no corpo do toast
                            OnRestoreRequested?.Invoke();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NotificationService] Erro ao tratar ativação de toast: {ex.Message}");
                    }
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NotificationService] Aviso: não foi possível registrar ToastNotificationManagerCompat.OnActivated: {ex.Message}");
            }
        }
    }

    public bool ShouldNotify(string camId)
    {
        if (!IsEnabled)
        {
            Console.WriteLine($"[NotificationService] Notificação ignorada: serviço desativado nas configurações.");
            return false;
        }

        if (OnlyInBackground && IsAppInBackground != null && !IsAppInBackground())
        {
            Console.WriteLine($"[NotificationService] Notificação ignorada: app aberto em primeiro plano.");
            return false;
        }

        if (_lastNotificationPerCam.TryGetValue(camId, out DateTime lastTime))
        {
            double elapsed = (DateTime.UtcNow - lastTime).TotalSeconds;
            if (elapsed < CooldownSeconds)
            {
                Console.WriteLine($"[NotificationService] Notificação em cooldown ({elapsed:F0}s / {CooldownSeconds}s) para câmera {camId}.");
                return false;
            }
        }

        return true;
    }

    public void NotifyMotion(CameraConfig camera, byte[]? snapshotBytes, string? existingThumbPath = null)
    {
        if (!ShouldNotify(camera.Id))
            return;

        _lastNotificationPerCam[camera.Id] = DateTime.UtcNow;

        Task.Run(async () =>
        {
            try
            {
                string? imagePath = existingThumbPath;

                // Se não temos caminho existente mas temos os bytes da foto, salva a foto
                if (string.IsNullOrEmpty(imagePath) && snapshotBytes != null && snapshotBytes.Length > 0)
                {
                    string safeId = camera.Id.Replace('/', '_').Replace('\\', '_');
                    string fileName = $"motion_{safeId}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                    imagePath = Path.Combine(_notificationsDir, fileName);

                    await File.WriteAllBytesAsync(imagePath, snapshotBytes);
                    CleanOldImages();
                }

                ShowToast(camera.Name, camera.Id, imagePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NotificationService] Erro ao disparar notificação: {ex.Message}");
            }
        });
    }

    public bool SendTestNotification(string? customImagePath = null, string? customCamName = null)
    {
        try
        {
            string? imagePath = customImagePath;
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                // Tenta achar alguma thumbnail recente em recordings/thumbs
                string baseDir = StorageService.ResolveBaseDir();
                string thumbsDir = Path.Combine(baseDir, "recordings", "thumbs");
                if (Directory.Exists(thumbsDir))
                {
                    var latest = new DirectoryInfo(thumbsDir)
                        .GetFiles("*.jpg")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (latest != null)
                    {
                        imagePath = latest.FullName;
                    }
                }
            }

            string camTitle = customCamName ?? "Câmera Teste (Demonstração)";
            ShowToast(camTitle, "test_camera", imagePath, isTest: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NotificationService] Erro no teste de notificação: {ex.Message}");
            return false;
        }
    }

    private void ShowToast(string camName, string camId, string? imagePath, bool isTest = false)
    {
        try
        {
            string timeStr = DateTime.Now.ToString("HH:mm:ss");
            string title = isTest ? "🔔 Vision - Teste de Notificação" : $"⚠️ Movimento: {camName}";
            string body = isTest 
                ? $"Notificações ativas no Windows! ({timeStr})" 
                : $"Atividade detectada às {timeStr}. Clique para abrir.";

            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(body)
                .AddArgument("action", "open")
                .AddArgument("cameraId", camId);

            // Adiciona logo da marca Vision no canto do toast se existir
            if (File.Exists(_brandLogoPath))
            {
                builder.AddAppLogoOverride(new Uri(Path.GetFullPath(_brandLogoPath)), ToastGenericAppLogoCrop.Circle);
            }

            // Adiciona foto de captura ampla (Inline Image) se houver imagem válida
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath) && new FileInfo(imagePath).Length > 200)
            {
                builder.AddInlineImage(new Uri(Path.GetFullPath(imagePath)));
            }

            // Botões de ação rápida
            builder.AddButton(new ToastButton()
                .SetContent("👁️ Ver no Vision")
                .AddArgument("action", "open")
                .AddArgument("cameraId", camId));

            builder.AddButton(new ToastButton()
                .SetContent("🌐 Abrir Navegador")
                .AddArgument("action", "browser")
                .AddArgument("cameraId", camId));

            builder.Show(toast =>
            {
                toast.ExpirationTime = DateTimeOffset.Now.AddMinutes(5);
            });

            Console.WriteLine($"[NotificationService] Toast enviado com sucesso: {camName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NotificationService] Falha ao exibir ToastContentBuilder.Show: {ex.Message}");
        }
    }

    private void CleanOldImages()
    {
        try
        {
            var dir = new DirectoryInfo(_notificationsDir);
            if (!dir.Exists) return;

            var files = dir.GetFiles("*.jpg")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(20) // Mantém apenas as 20 fotos mais recentes de notificações
                .ToList();

            foreach (var f in files)
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }
}
