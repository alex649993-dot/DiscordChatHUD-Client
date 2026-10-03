using System.Text.Json;
using System.Text.Json.Serialization;
using DiscordChatHUD.Models;
namespace DiscordChatHUD.Services;
internal sealed record RelaySnapshot(long Revision, string ChannelLabel, string Status,
    bool HasSaleChannel, ulong? SaleMessageId, DateTimeOffset SaleTimestamp,
    ChatMessage[] Messages, ChatMessage[] SaleMessages, GtaSessionPresence? SessionPresence = null,
    // 맨 뒤 선택 인자로만 늘린다. 구버전 클라이언트는 이 속성을 무시하고,
    // 구버전 중계에서 온 스냅샷은 여기가 비어 있어 예전과 똑같이 동작한다.
    GtaSessionPresence? SecondarySessionPresence = null);
internal static class RelayProtocol
{
    public const int Version = 190;
    public static readonly JsonSerializerOptions Json = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new ColorConverter());
        return options;
    }
    private sealed class ColorConverter : JsonConverter<System.Drawing.Color>
    {
        public override System.Drawing.Color Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => System.Drawing.Color.FromArgb(reader.GetInt32());
        public override void Write(Utf8JsonWriter writer, System.Drawing.Color color, JsonSerializerOptions options)
            => writer.WriteNumberValue(color.ToArgb());
    }
}
