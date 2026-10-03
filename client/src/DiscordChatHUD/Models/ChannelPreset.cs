using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordChatHUD.Models;
internal sealed class ChannelPreset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public ulong Id { get; set; }

    public override string ToString() => Name;
}
