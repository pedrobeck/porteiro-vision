using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PorteiroVision.Models;

namespace PorteiroVision.Services;

public class HubManager : IDisposable
{
    private Process? _hubProcess;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private System.Threading.Timer? _pollTimer;
    private bool _isStarting = false;
    private bool _isAttached = false;

    public bool IsRunning => (_hubProcess != null && !_hubProcess.HasExited) || _isAttached;
    public string TunnelUrl { get; private set; } = "";
    public string LocalIp { get; private set; } = "127.0.0.1";
    public int Port { get; private set; } = 8090;

    public event Action<string>? OnLog;
    public event Action<bool>? OnStateChanged;
    public event Action<string>? OnTunnelReady;
    public event Action<HubStatusResponse>? OnStatusUpdated;

    public string ProjectDir { get; set; } = @"D:\Projetos\PorteiroCam";

    private string FindPythonPath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python313\python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python312\python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python311\python.exe"),
            @"C:\Python313\python.exe",
            @"C:\Python312\python.exe",
            @"C:\Python311\python.exe"
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        return "python.exe";
    }

    public async Task<bool> StartAsync()
    {
        App.Trace($"StartAsync called: IsRunning={IsRunning}, _isStarting={_isStarting}");
        if (IsRunning || _isStarting) return true;

        _isStarting = true;
        TunnelUrl = "";
        Log("[HubManager] Verificando central Porteiro Vision...");

        try
        {
            // 1. Verifica se o servidor Python já está ativo e respondendo na porta 8090
            try
            {
                App.Trace("StartAsync checking http://127.0.0.1:8090/api/status");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var check = await _http.GetAsync($"http://127.0.0.1:{Port}/api/status", cts.Token);
                if (check.IsSuccessStatusCode)
                {
                    Log($"[HubManager] Central já ativa e respondendo na porta {Port}.");
                    _isAttached = true;
                    _isStarting = false;
                    OnStateChanged?.Invoke(true);
                    _pollTimer = new System.Threading.Timer(async _ => await PollStatusAsync(), null, 500, 2500);
                    return true;
                }
            }
            catch (Exception ex)
            {
                App.Trace($"StartAsync check failed (expected if hub is off): {ex.Message}");
            }

            var pythonExe = FindPythonPath();
            var runHubScript = Path.Combine(ProjectDir, "run_hub.py");
            App.Trace($"StartAsync pythonExe={pythonExe}, runHubScript={runHubScript}");

            if (!File.Exists(runHubScript))
            {
                Log($"[Erro] Arquivo 'run_hub.py' não encontrado em: {ProjectDir}");
                _isStarting = false;
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{runHubScript}\"",
                WorkingDirectory = ProjectDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                Environment =
                {
                    ["NO_AUTO_BROWSER"] = "1",
                    ["PORT"] = Port.ToString()
                }
            };

            _hubProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

            _hubProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    HandleProcessOutput(e.Data);
                }
            };

            _hubProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Log($"[SYS] {e.Data}");
                }
            };

            _hubProcess.Exited += (s, e) =>
            {
                Log("[HubManager] Central de câmeras foi encerrada.");
                _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                OnStateChanged?.Invoke(false);
            };

            App.Trace("StartAsync starting _hubProcess...");
            if (!_hubProcess.Start())
            {
                Log("[Erro] Não foi possível iniciar o processo do Python.");
                _isStarting = false;
                return false;
            }
            App.Trace($"StartAsync _hubProcess started (PID: {_hubProcess.Id})");

            _hubProcess.BeginOutputReadLine();
            _hubProcess.BeginErrorReadLine();

            _isStarting = false;
            OnStateChanged?.Invoke(true);

            // Inicia polling de telemetria a cada 2.5 segundos
            _pollTimer = new System.Threading.Timer(async _ => await PollStatusAsync(), null, 3000, 2500);

            return true;
        }
        catch (Exception ex)
        {
            Log($"[Erro ao iniciar] {ex.Message}");
            _isStarting = false;
            return false;
        }
    }

    private void HandleProcessOutput(string line)
    {
        Log(line);

        // Detecta URL do Cloudflare Tunnel
        var tunnelMatch = Regex.Match(line, @"https://[a-zA-Z0-9-]+\.trycloudflare\.com");
        if (tunnelMatch.Success)
        {
            TunnelUrl = tunnelMatch.Value;
            Log($"[Túnel Cloudflare Ativo] Link externo: {TunnelUrl}");
            OnTunnelReady?.Invoke(TunnelUrl);
        }

        // Detecta IP Local
        var ipMatch = Regex.Match(line, @"http://(192\.168\.\d+\.\d+|10\.\d+\.\d+\.\d+):8090");
        if (ipMatch.Success)
        {
            LocalIp = ipMatch.Groups[1].Value;
        }
    }

    private async Task PollStatusAsync()
    {
        if (!IsRunning) return;

        try
        {
            var res = await _http.GetStringAsync("http://127.0.0.1:8090/api/status");
            var status = JsonSerializer.Deserialize<HubStatusResponse>(res);
            if (status != null)
            {
                if (!string.IsNullOrEmpty(status.ServerIp))
                {
                    LocalIp = status.ServerIp;
                }
                OnStatusUpdated?.Invoke(status);
            }
        }
        catch
        {
            // O hub pode estar iniciando
        }
    }

    public async Task StopAsync()
    {
        Log("[HubManager] Parando todos os serviços e liberando portas...");
        _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        if (_hubProcess != null && !_hubProcess.HasExited)
        {
            try
            {
                // Mata a árvore inteira de processos (Python, Cloudflared, FFmpeg, etc)
                _hubProcess.Kill(entireProcessTree: true);
                await _hubProcess.WaitForExitAsync();
            }
            catch { }
        }

        _hubProcess = null;
        _isAttached = false;
        OnStateChanged?.Invoke(false);
        Log("[HubManager] Todos os processos foram finalizados com sucesso.");
    }

    private void Log(string message)
    {
        App.Trace(message);
        var formatted = $"[{DateTime.Now:HH:mm:ss}] {message}";
        OnLog?.Invoke(formatted);
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _http.Dispose();
        if (_hubProcess != null && !_hubProcess.HasExited)
        {
            try { _hubProcess.Kill(true); } catch { }
        }
    }
}
