using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsiphonUI.Models;

public sealed class Notice
{
    [JsonPropertyName("noticeType")]
    public string NoticeType { get; set; } = "";

    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("showUser")]
    public bool ShowUser { get; set; }
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Error,
}
