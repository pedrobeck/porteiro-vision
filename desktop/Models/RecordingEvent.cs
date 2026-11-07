using System;
using System.Text.Json.Serialization;

namespace Vision.Models;

public class RecordingEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("camera_id")]
    public string CameraId { get; set; } = "";

    [JsonPropertyName("camera_name")]
    public string CameraName { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("date_str")]
    public string DateStr { get; set; } = "";

    [JsonPropertyName("time_str")]
    public string TimeStr { get; set; } = "";

    [JsonPropertyName("duration_sec")]
    public int DurationSeconds { get; set; } = 15;

    [JsonPropertyName("video_file")]
    public string VideoFile { get; set; } = "";

    [JsonPropertyName("thumb_file")]
    public string ThumbFile { get; set; } = "";

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; } = 0;

    [JsonPropertyName("size_str")]
    public string SizeStr { get; set; } = "";
}
