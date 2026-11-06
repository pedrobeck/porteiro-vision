using System.Text.Json.Serialization;

namespace PorteiroVision.Models;

public class CameraConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 554;

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "rtsp";

    [JsonPropertyName("brand")]
    public string Brand { get; set; } = "generic";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "admin";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("channel")]
    public int Channel { get; set; } = 1;

    [JsonPropertyName("stream_type")]
    public string StreamType { get; set; } = "sub";

    [JsonPropertyName("custom_url")]
    public string CustomUrl { get; set; } = "";

    [JsonPropertyName("ptz_enabled")]
    public bool PtzEnabled { get; set; } = false;

    [JsonPropertyName("multi_lens")]
    public bool MultiLens { get; set; } = false;

    [JsonPropertyName("order")]
    public double Order { get; set; } = 0;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("motion_sensitivity")]
    public string MotionSensitivity { get; set; } = "medium";

    // Propriedades virtuais/runtime para o frontend
    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }

    [JsonPropertyName("is_sub_lens")]
    public bool? IsSubLens { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("fps")]
    public double? Fps { get; set; }

    [JsonPropertyName("last_seen")]
    public string? LastSeen { get; set; }
}

public class SettingsConfig
{
    [JsonPropertyName("motion_sensitivity")]
    public string MotionSensitivity { get; set; } = "medium";

    [JsonPropertyName("night_duration")]
    public int NightDuration { get; set; } = 30;

    [JsonPropertyName("night_sound")]
    public bool NightSound { get; set; } = true;
}
