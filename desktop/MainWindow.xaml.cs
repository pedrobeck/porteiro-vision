using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PorteiroVision.Models;
using PorteiroVision.Services;
using Forms = System.Windows.Forms;

namespace PorteiroVision;

public partial class MainWindow : Window
{
    private readonly HubManager _hub = new();
    private Forms.NotifyIcon? _trayIcon;
    private bool _isExplicitExit = false;

    public MainWindow()
    {
        InitializeComponent();
        SetupTrayIcon();
        SetupHubEvents();

        // Carrega configurações salvas
        StartWithWindowsCheck.IsChecked = AutoStartHelper.IsAutoStartEnabled();

        // Inicia a central de câmeras automaticamente ao abrir o aplicativo
        Loaded += async (s, e) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && args[1].Contains("--minimized"))
            {
                Hide();
            }
            await _hub.StartAsync();
        };
    }

    private void SetupTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        Icon icon;
        if (File.Exists(iconPath))
        {
            icon = new Icon(iconPath);
        }
        else
        {
            icon = SystemIcons.Shield;
        }

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Porteiro Vision Hub",
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();

        var statusItem = new Forms.ToolStripMenuItem("🔴 Hub Desligado") { Enabled = false };
        var openLocalItem = new Forms.ToolStripMenuItem("🌐 Abrir Painel Local", null, (s, e) => OpenLocalBrowser_Click(this, null));
        var openCloudItem = new Forms.ToolStripMenuItem("☁️ Abrir na Nuvem (Vercel)", null, (s, e) => OpenCloudBrowser_Click(this, null!));
        var copyWifiItem = new Forms.ToolStripMenuItem("📋 Copiar Link do Tablet/Wi-Fi", null, (s, e) => CopyWifiUrl_Click(this, null));
        var showWindowItem = new Forms.ToolStripMenuItem("⚙️ Exibir Central", null, (s, e) => RestoreFromTray());
        var exitItem = new Forms.ToolStripMenuItem("🚪 Sair", null, (s, e) => FullExit());

        menu.Items.Add(statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(openLocalItem);
        menu.Items.Add(openCloudItem);
        menu.Items.Add(copyWifiItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(showWindowItem);
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (s, e) => RestoreFromTray();
    }

    private void SetupHubEvents()
    {
        _hub.OnLog += msg =>
        {
            Dispatcher.Invoke(() =>
            {
                LogsTextBox.AppendText(msg + Environment.NewLine);
                LogScrollViewer.ScrollToEnd();
            });
        };

        _hub.OnStateChanged += isRunning =>
        {
            Dispatcher.Invoke(() =>
            {
                UpdateUiState(isRunning);
            });
        };

        _hub.OnTunnelReady += url =>
        {
            Dispatcher.Invoke(() =>
            {
                CloudUrlText.Text = url;
            });
        };

        _hub.OnStatusUpdated += status =>
        {
            Dispatcher.Invoke(() =>
            {
                UpdateTelemetryAndCameras(status);
            });
        };
    }

    private void UpdateUiState(bool isRunning)
    {
        if (isRunning)
        {
            TopStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 16, 185, 129));
            TopStatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
            TopStatusText.Text = "HUB ONLINE";
            TopStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));

            MainActionBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            MainActionBtn.Content = "⏹ PARAR HUB DE CÂMERAS";

            HubSubtitle.Text = "A central está online. O servidor de vídeo e detecção noturna estão ativos.";
            FooterStatus.Text = "Hub de streaming ativo na porta 8090.";

            if (_trayIcon?.ContextMenuStrip?.Items[0] is Forms.ToolStripMenuItem item)
            {
                item.Text = "🟢 Hub Online";
            }
        }
        else
        {
            TopStatusBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59));
            TopStatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            TopStatusText.Text = "HUB DESLIGADO";
            TopStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));

            MainActionBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
            MainActionBtn.Content = "▶ INICIAR HUB DE CÂMERAS";

            HubSubtitle.Text = "O servidor local está pronto para ser iniciado. Clique no botão ao lado para ligar as câmeras.";
            FooterStatus.Text = "Pronto.";

            ActiveCamsText.Text = "0 Online";
            CpuText.Text = "-- %";
            RamText.Text = "-- GB";
            NetText.Text = "-- MB/s";

            if (_trayIcon?.ContextMenuStrip?.Items[0] is Forms.ToolStripMenuItem item)
            {
                item.Text = "🔴 Hub Desligado";
            }
        }
    }

    private void UpdateTelemetryAndCameras(HubStatusResponse status)
    {
        // Atualiza URLs
        if (!string.IsNullOrEmpty(status.ServerIp))
        {
            WifiUrlText.Text = $"http://{status.ServerIp}:8090";
        }

        // Atualiza Telemetria
        if (status.System != null)
        {
            CpuText.Text = $"{status.System.CpuPercent:F1} %";
            RamText.Text = !string.IsNullOrEmpty(status.System.RamStr) ? status.System.RamStr : $"{status.System.RamUsedMb:F0} MB";
            NetText.Text = status.System.NetDownStr;
        }

        int onlineCount = 0;
        CamerasPanel.Children.Clear();

        foreach (var cam in status.Cameras)
        {
            bool isOnline = cam.Status == "ONLINE";
            if (isOnline) onlineCount++;

            var card = CreateCameraCard(cam, isOnline);
            CamerasPanel.Children.Add(card);
        }

        ActiveCamsText.Text = $"{onlineCount} Online";
    }

    private Border CreateCameraCard(CameraInfo cam, bool isOnline)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(isOnline 
                ? System.Windows.Media.Color.FromArgb(40, 56, 189, 248) 
                : System.Windows.Media.Color.FromRgb(51, 65, 85))
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        var titlePanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

        var dotColor = isOnline ? System.Windows.Media.Color.FromRgb(16, 185, 129) : System.Windows.Media.Color.FromRgb(239, 68, 68);
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(dotColor),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var title = new TextBlock
        {
            Text = cam.Name,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249)),
            VerticalAlignment = VerticalAlignment.Center
        };

        titlePanel.Children.Add(dot);
        titlePanel.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = $"{cam.Brand.ToUpper()} • IP: {cam.Host} • {cam.Fps:F1} FPS",
            FontSize = 11,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            Margin = new Thickness(14, 2, 0, 0)
        };

        left.Children.Add(titlePanel);
        left.Children.Add(subtitle);

        var statusBadge = new Border
        {
            Background = new SolidColorBrush(isOnline 
                ? System.Windows.Media.Color.FromArgb(50, 16, 185, 129) 
                : System.Windows.Media.Color.FromArgb(50, 239, 68, 68)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center
        };

        var statusText = new TextBlock
        {
            Text = cam.Status,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(isOnline 
                ? System.Windows.Media.Color.FromRgb(16, 185, 129) 
                : System.Windows.Media.Color.FromRgb(239, 68, 68))
        };
        statusBadge.Child = statusText;

        Grid.SetColumn(left, 0);
        Grid.SetColumn(statusBadge, 1);
        grid.Children.Add(left);
        grid.Children.Add(statusBadge);

        border.Child = grid;
        return border;
    }

    private async void MainActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_hub.IsRunning)
        {
            await _hub.StopAsync();
        }
        else
        {
            await _hub.StartAsync();
        }
    }

    private void OpenLocalBrowser_Click(object sender, RoutedEventArgs? e)
    {
        try { Process.Start(new ProcessStartInfo("http://127.0.0.1:8090") { UseShellExecute = true }); } catch { }
    }

    private void OpenCloudBrowser_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://porteiro-vision.vercel.app") { UseShellExecute = true }); } catch { }
    }

    private void CopyLocalUrl_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(LocalUrlText.Text);
        ShowCopiedFeedback("Link local copiado!");
    }

    private void CopyWifiUrl_Click(object sender, RoutedEventArgs? e)
    {
        System.Windows.Clipboard.SetText(WifiUrlText.Text);
        ShowCopiedFeedback("Link Wi-Fi do tablet copiado!");
    }

    private void CopyCloudUrl_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(CloudUrlText.Text);
        ShowCopiedFeedback("Link de acesso externo copiado!");
    }

    private void ShowCopiedFeedback(string text)
    {
        FooterStatus.Text = text;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (s, e) =>
        {
            FooterStatus.Text = _hub.IsRunning ? "Hub de streaming ativo na porta 8090." : "Pronto.";
            timer.Stop();
        };
        timer.Start();
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        LogsTextBox.Clear();
    }

    private void StartWithWindowsCheck_Click(object sender, RoutedEventArgs e)
    {
        bool enable = StartWithWindowsCheck.IsChecked == true;
        AutoStartHelper.SetAutoStart(enable);
        ShowCopiedFeedback(enable ? "Iniciar com o Windows: Ativado!" : "Iniciar com o Windows: Desativado.");
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
            _trayIcon?.ShowBalloonTip(2000, "Porteiro Vision", "A central está rodando em segundo plano na bandeja.", Forms.ToolTipIcon.Info);
        }
        else
        {
            FullExit();
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void FullExit()
    {
        _isExplicitExit = true;
        _trayIcon?.Dispose();
        await _hub.StopAsync();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExplicitExit && MinimizeToTrayCheck.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            _trayIcon?.ShowBalloonTip(2000, "Porteiro Vision", "A central continuará funcionando em segundo plano.", Forms.ToolTipIcon.Info);
        }
        else
        {
            base.OnClosing(e);
        }
    }
}
