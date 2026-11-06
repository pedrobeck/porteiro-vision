using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PorteiroVision.Services;

public class DvripClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly int _timeoutMs;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private uint _sessionId = 0;
    private uint _seq = 0;

    private const ushort MSG_LOGIN_REQ = 1000;
    private const ushort MSG_LOGIN_RESP = 1001;
    private const ushort MSG_PTZ_REQ = 1400;

    public DvripClient(string host, int port = 34567, string username = "admin", string password = "", int timeoutMs = 2500)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;
        _timeoutMs = timeoutMs;
    }

    public async Task<bool> ConnectAsync()
    {
        try
        {
            _client = new TcpClient();
            using var cts = new System.Threading.CancellationTokenSource(_timeoutMs);
            await _client.ConnectAsync(_host, _port, cts.Token);
            _client.ReceiveTimeout = _timeoutMs;
            _client.SendTimeout = _timeoutMs;
            _stream = _client.GetStream();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DVRIP] Falha ao conectar em {_host}:{_port} - {ex.Message}");
            return false;
        }
    }

    private async Task<string?> SendCommandAsync(ushort msgId, object payload)
    {
        if (_stream == null) return null;

        try
        {
            string json = JsonSerializer.Serialize(payload) + "\n\0";
            byte[] payloadBytes = Encoding.UTF8.GetBytes(json);

            // Cabeçalho de 20 bytes: 0xFF, 0x00, 0x00, 0x00, Session(4B), Seq(4B), Total(2B), Cur(2B), MsgId(2B), Len(2B)
            byte[] header = new byte[20];
            header[0] = 0xFF;
            header[1] = 0x00;
            header[2] = 0x00;
            header[3] = 0x00;

            BitConverter.GetBytes(_sessionId).CopyTo(header, 4);
            BitConverter.GetBytes(_seq++).CopyTo(header, 8);
            BitConverter.GetBytes((ushort)0).CopyTo(header, 12);
            BitConverter.GetBytes((ushort)0).CopyTo(header, 14);
            BitConverter.GetBytes(msgId).CopyTo(header, 16);
            BitConverter.GetBytes((ushort)payloadBytes.Length).CopyTo(header, 18);

            await _stream.WriteAsync(header, 0, header.Length);
            await _stream.WriteAsync(payloadBytes, 0, payloadBytes.Length);

            // Lê resposta (20 bytes de cabeçalho)
            byte[] respHeader = new byte[20];
            int readTotal = 0;
            while (readTotal < 20)
            {
                int r = await _stream.ReadAsync(respHeader, readTotal, 20 - readTotal);
                if (r <= 0) return null;
                readTotal += r;
            }

            ushort respLen = BitConverter.ToUInt16(respHeader, 18);
            if (respLen > 0)
            {
                byte[] body = new byte[respLen];
                readTotal = 0;
                while (readTotal < respLen)
                {
                    int r = await _stream.ReadAsync(body, readTotal, respLen - readTotal);
                    if (r <= 0) break;
                    readTotal += r;
                }
                return Encoding.UTF8.GetString(body).TrimEnd('\0', '\n', '\r');
            }
            return "";
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> LoginAsync()
    {
        if (_stream == null && !await ConnectAsync())
            return false;

        var payload = new
        {
            EncryptType = "NONE",
            LoginType = "DVRIP-Web",
            PassWord = _password,
            UserName = _username
        };

        string? resp = await SendCommandAsync(MSG_LOGIN_REQ, payload);
        if (resp != null && resp.Contains("\"Ret\":100"))
        {
            try
            {
                using var doc = JsonDocument.Parse(resp);
                if (doc.RootElement.TryGetProperty("SessionID", out var sidProp))
                {
                    if (sidProp.ValueKind == JsonValueKind.String)
                    {
                        string s = sidProp.GetString() ?? "0";
                        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            _sessionId = Convert.ToUInt32(s[2..], 16);
                        else
                            uint.TryParse(s, out _sessionId);
                    }
                    else if (sidProp.ValueKind == JsonValueKind.Number)
                    {
                        _sessionId = sidProp.GetUInt32();
                    }
                }
                return true;
            }
            catch { }
        }
        return false;
    }

    public async Task<bool> PtzControlAsync(string command, int step = 4)
    {
        if (!await LoginAsync())
            return false;

        var payload = new
        {
            Name = "OPPTZControl",
            OPPTZControl = new
            {
                Command = command,
                Parameter = new
                {
                    Channel = 0,
                    Step = step
                }
            }
        };

        string? resp = await SendCommandAsync(MSG_PTZ_REQ, payload);
        return resp != null && resp.Contains("\"Ret\":100");
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }
}
