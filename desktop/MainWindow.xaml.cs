using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PorteiroVision.Models;
using PorteiroVision.Services;
using Forms = System.Windows.Forms;

namespace PorteiroVision;

public partial class MainWindow : Window
{
    [DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hwProc);

    private readonly StorageService _storage;
    private readonly SystemMonitor _systemMonitor;
    private readonly CameraManager _cameraManager;
    private readonly WebServer _webServer;

    private Forms.NotifyIcon? _trayIcon;
    private bool _isRunning = false;

    // Buffer de logs limitado para impedir consumo de memória
    private readonly Queue<string> _logBuffer = new();
    private const int MaxLogBufferLines = 80;
    private bool _logUpdatePending = false;

    private DispatcherTimer? _uiTimer;
    private DispatcherTimer? _memoryTrimTimer;

    // Pincéis estáticos congelados (Freeze) para zero alocação de memória no WPF
    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush CardBg = CreateFrozenBrush(30, 41, 59);
    private static readonly SolidColorBrush BorderOnline = CreateFrozenBrush(56, 189, 248, 40);
    private static readonly SolidColorBrush BorderOffline = CreateFrozenBrush(51, 65, 85);
    private static readonly SolidColorBrush DotOnline = CreateFrozenBrush(16, 185, 129);
    private static readonly SolidColorBrush DotOffline = CreateFrozenBrush(239, 68, 68);
    private static readonly SolidColorBrush BadgeOnline = CreateFrozenBrush(16, 185, 129, 50);
    private static readonly SolidColorBrush BadgeOffline = CreateFrozenBrush(239, 68, 68, 50);
    private static readonly SolidColorBrush TitleBrush = CreateFrozenBrush(241, 245, 249);
    private static readonly SolidColorBrush SubtitleBrush = CreateFrozenBrush(148, 163, 184);

    public sealed class CameraCardUi
    {
        public Border Container = null!;
        public Ellipse Dot = null!;
        public TextBlock Title = null!;
        public TextBlock Subtitle = null!;
        public Border StatusBadge = null!;
        public TextBlock StatusText = null!;
    }

    private readonly Dictionary<string, CameraCardUi> _cameraCardCache = new();

    public MainWindow()
    {
        InitializeComponent();

        _storage = new StorageService();
        _systemMonitor = new SystemMonitor();
        _cameraManager = new CameraManager(_storage);
        _webServer = new WebServer(_storage, _cameraManager, _systemMonitor);

        LocalUrlText.Text = $"http://127.0.0.1:{_webServer.Port}";
        WifiUrlText.Text = $"http://{_webServer.LocalIp}:{_webServer.Port}";

        SetupTrayIcon();
        StartWithWindowsCheck.IsChecked = AutoStartHelper.IsAutoStartEnabled();

        // Redireciona Console.Out para a UI de logs
        var writer = new ConsoleLogRedirector(Log);
        Console.SetOut(writer);

        // Timer de atualização da interface (2,5s)
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _uiTimer.Tick += (s, e) => UpdateUi();

        // Limpeza periódica de memória a cada 45s
        _memoryTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _memoryTrimTimer.Tick += (s, e) => TrimMemory();
        _memoryTrimTimer.Start();

        Loaded += async (s, e) =>
        {
            await StartHubAsync();
            _uiTimer.Start();
        };
    }

    private sealed class ConsoleLogRedirector : TextWriter
    {
        private readonly Action<string> _logAction;
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public ConsoleLogRedirector(Action<string> logAction) => _logAction = logAction;
        public override void WriteLine(string? value) { if (!string.IsNullOrEmpty(value)) _logAction(value); }
    }

    private void TrimMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch { }
    }

    private async Task StartHubAsync()
    {
        if (_isRunning) return;

        Log("[Hub C#] Iniciando central Porteiro Vision 100% C# nativo...");
        SetState(true);

        try
        {
            _cameraManager.StartAll();
            await _webServer.StartAsync();
            _isRunning = true;

            Log($"[Hub C#] Servidor Kestrel ativo na porta {_webServer.Port}!");
            Log($"[Hub C#] Acesso Local: http://127.0.0.1:{_webServer.Port}");
            Log($"[Hub C#] Acesso Tablet: http://{_webServer.LocalIp}:{_webServer.Port}");
            FooterStatus.Text = "Central C# ativa e transmitindo.";
        }
        catch (Exception ex)
        {
            Log($"[Hub C#] ERRO ao iniciar central: {ex.Message}");
            SetState(false);
            FooterStatus.Text = "Erro ao iniciar central.";
        }
    }

    private async Task StopHubAsync()
    {
        if (!_isRunning) return;

        Log("[Hub C#] Parando central...");
        _isRunning = false;
        SetState(false);

        await _webServer.StopAsync();
        _cameraManager.Dispose();

        Log("[Hub C#] Central desligada.");
        FooterStatus.Text = "Central parada.";
    }

    private void SetState(bool running)
    {
        _isRunning = running;

        if (running)
        {
            TopStatusDot.Fill = DotOnline;
            TopStatusText.Text = "HUB ONLINE (100% C#)";
            TopStatusText.Foreground = DotOnline;
            TopStatusBadge.Background = BadgeOnline;

            MainActionBtn.Background = CreateFrozenBrush(239, 68, 68);
            MainActionBtn.Content = "⏹ PARAR HUB DE CÂMERAS";
            HubSubtitle.Text = "Central de vídeo C# ativa. Câmeras transmitindo sem necessidade de Python.";
        }
        else
        {
            TopStatusDot.Fill = DotOffline;
            TopStatusText.Text = "HUB DESLIGADO";
            TopStatusText.Foreground = DotOffline;
            TopStatusBadge.Background = BadgeOffline;

            MainActionBtn.Background = DotOnline;
            MainActionBtn.Content = "▶ INICIAR HUB DE CÂMERAS";
            HubSubtitle.Text = "O servidor local está pronto para ser iniciado. Clique no botão ao lado para ligar as câmeras.";
        }
    }

    private void UpdateUi()
    {
        if (!_isRunning) return;

        // Telemetria
        var stats = _systemMonitor.GetStats();
        CpuText.Text = $"{stats.CpuPercent:F1} %";
        RamText.Text = stats.RamStr;
        NetText.Text = stats.NetDownStr;

        // Câmeras
        var cameras = _cameraManager.GetAllStatus();
        int onlineCount = cameras.Count(c => c.Status == "ONLINE");
        ActiveCamsText.Text = $"{onlineCount} / {cameras.Count} Online";

        UpdateCameraCards(cameras);
    }

    private void UpdateCameraCards(List<CameraInfo> cameras)
    {
        if (cameras.Count == 0)
        {
            if (_cameraCardCache.Count == 0)
            {
                CamerasPanel.Children.Clear();
                CamerasPanel.Children.Add(new TextBlock
                {
                    Text = "Nenhuma câmera cadastrada.",
                    Foreground = CreateFrozenBrush(100, 116, 139),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 10, 0, 0)
                });
            }
            return;
        }

        var incomingIds = new HashSet<string>(cameras.Select(c => c.Id));

        var toRemove = _cameraCardCache.Keys.Where(k => !incomingIds.Contains(k)).ToList();
        foreach (var id in toRemove)
        {
            CamerasPanel.Children.Remove(_cameraCardCache[id].Container);
            _cameraCardCache.Remove(id);
        }

        if (CamerasPanel.Children.Count > 0 && CamerasPanel.Children[0] is TextBlock tb && tb.FontStyle == FontStyles.Italic)
        {
            CamerasPanel.Children.Clear();
        }

        foreach (var cam in cameras)
        {
            bool isOnline = cam.Status == "ONLINE";
            string fpsStr = cam.Fps > 0 ? $"{cam.Fps:F1} FPS" : "-- FPS";

            if (_cameraCardCache.TryGetValue(cam.Id, out var ui))
            {
                ui.Dot.Fill = isOnline ? DotOnline : DotOffline;
                ui.Container.BorderBrush = isOnline ? BorderOnline : BorderOffline;
                ui.Title.Text = cam.Name;
                ui.Subtitle.Text = $"{cam.Brand?.ToUpperInvariant()} • {cam.Host}";
                ui.StatusBadge.Background = isOnline ? BadgeOnline : BadgeOffline;
                ui.StatusText.Text = isOnline ? $"ONLINE ({fpsStr})" : cam.Status;
                ui.StatusText.Foreground = isOnline ? DotOnline : DotOffline;
            }
            else
            {
                var newUi = CreateCameraCard(cam, isOnline, fpsStr);
                _cameraCardCache[cam.Id] = newUi;
                CamerasPanel.Children.Add(newUi.Container);
            }
        }
    }

    private CameraCardUi CreateCameraCard(CameraInfo cam, bool isOnline, string fpsStr)
    {
        var card = new Border
        {
            Background = CardBg,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = isOnline ? BorderOnline : BorderOffline,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = isOnline ? DotOnline : DotOffline,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        left.Children.Add(dot);

        var info = new StackPanel();
        var title = new TextBlock
        {
            Text = cam.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = TitleBrush
        };
        var subtitle = new TextBlock
        {
            Text = $"{cam.Brand?.ToUpperInvariant()} • {cam.Host}",
            FontSize = 11,
            Foreground = SubtitleBrush,
            Margin = new Thickness(0, 2, 0, 0)
        };
        info.Children.Add(title);
        info.Children.Add(subtitle);
        left.Children.Add(info);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var badge = new Border
        {
            Background = isOnline ? BadgeOnline : BadgeOffline,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        var stText = new TextBlock
        {
            Text = isOnline ? $"ONLINE ({fpsStr})" : cam.Status,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = isOnline ? DotOnline : DotOffline
        };
        badge.Child = stText;
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        card.Child = grid;

        return new CameraCardUi
        {
            Container = card,
            Dot = dot,
            Title = title,
            Subtitle = subtitle,
            StatusBadge = badge,
            StatusText = stText
        };
    }

    private void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            lock (_logBuffer)
            {
                _logBuffer.Enqueue(line);
                while (_logBuffer.Count > MaxLogBufferLines)
                {
                    _logBuffer.Dequeue();
                }

                if (!_logUpdatePending)
                {
                    _logUpdatePending = true;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        lock (_logBuffer)
                        {
                            LogsTextBox.Text = string.Join(Environment.NewLine, _logBuffer);
                            _logUpdatePending = false;
                        }
                        LogScrollViewer.ScrollToEnd();
                    });
                }
            }
        });
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Porteiro Vision Hub (100% C#)",
            Visible = true
        };

        string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
        if (File.Exists(iconPath))
        {
            _trayIcon.Icon = new System.Drawing.Icon(iconPath);
        }
        else
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Painel", null, (s, e) => ShowAndRestore());
        menu.Items.Add("Abrir no Navegador", null, (s, e) => OpenBrowser($"http://127.0.0.1:{_webServer.Port}"));
        menu.Items.Add("-");
        menu.Items.Add("Sair do Porteiro Vision", null, (s, e) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;

        _trayIcon.DoubleClick += (s, e) => ShowAndRestore();
    }

    private void ShowAndRestore()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void ExitApplication()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;

        await StopHubAsync();
        _systemMonitor.Dispose();

        System.Windows.Application.Current.Shutdown();
    }

    // --- EVENTOS DE BOTÕES ---
    private async void MainActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
            await StopHubAsync();
        else
            await StartHubAsync();
    }

    private void OpenLocalBrowser_Click(object sender, RoutedEventArgs e)
    {
        OpenBrowser($"http://127.0.0.1:{_webServer.Port}");
    }

    private void CopyLocalUrl_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText($"http://127.0.0.1:{_webServer.Port}");
        FooterStatus.Text = "Link local copiado!";
    }

    private void CopyWifiUrl_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText($"http://{_webServer.LocalIp}:{_webServer.Port}");
        FooterStatus.Text = "Link do Wi-Fi copiado!";
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        lock (_logBuffer)
        {
            _logBuffer.Clear();
            LogsTextBox.Clear();
        }
    }

    private void StartWithWindowsCheck_Click(object sender, RoutedEventArgs e)
    {
        bool enable = StartWithWindowsCheck.IsChecked == true;
        AutoStartHelper.SetAutoStart(enable);
        FooterStatus.Text = enable ? "Início automático ativado." : "Início automático desativado.";
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (MinimizeToTrayCheck.IsChecked == true)
        {
            Hide();
            TrimMemory();
        }
        else
        {
            ExitApplication();
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
