using System.Text.Json.Serialization;

namespace PorteiroVision.Models;

public class CameraInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("brand")]
    public string Brand { get; set; } = "RTSP";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "OFFLINE";

    [JsonPropertyName("fps")]
    public double Fps { get; set; } = 0.0;

    [JsonPropertyName("motion")]
    public bool Motion { get; set; } = false;

    [JsonPropertyName("last_seen")]
    public string? LastSeen { get; set; }
}

public class SystemTelemetry
{
    [JsonPropertyName("cpu_percent")]
    public double CpuPercent { get; set; } = 0.0;

    [JsonPropertyName("ram_percent")]
    public double RamPercent { get; set; } = 0.0;

    [JsonPropertyName("ram_used_gb")]
    public double RamUsedGb { get; set; } = 0.0;

    [JsonPropertyName("ram_used_mb")]
    public double RamUsedMb { get; set; } = 0.0;

    [JsonPropertyName("ram_str")]
    public string RamStr { get; set; } = "";

    [JsonPropertyName("ram_total_gb")]
    public double RamTotalGb { get; set; } = 0.0;

    [JsonPropertyName("net_up_str")]
    public string NetUpStr { get; set; } = "0 KB/s";

    [JsonPropertyName("net_down_str")]
    public string NetDownStr { get; set; } = "0 KB/s";
}

public class HubStatusResponse
{
    [JsonPropertyName("cameras")]
    public List<CameraInfo> Cameras { get; set; } = new();

    [JsonPropertyName("system")]
    public SystemTelemetry? System { get; set; }

    [JsonPropertyName("server_ip")]
    public string ServerIp { get; set; } = "127.0.0.1";
}
