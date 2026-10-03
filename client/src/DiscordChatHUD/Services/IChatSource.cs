using DiscordChatHUD.Models;
namespace DiscordChatHUD.Services;
internal interface IChatSource : IAsyncDisposable
{
    GtaSessionPresence? SessionPresence => null;
    /// <summary>SessionHost가 게임 중이 아닐 때 대신 보여 줄 대상. 중계만 채운다.</summary>
    GtaSessionPresence? SecondarySessionPresence => null;
    event Action? MessagesChanged;
    event Action? StatusChanged;
    string ChannelLabel { get; }
    string Status { get; }
    bool HasSaleChannel { get; }
    (ulong? MessageId, DateTimeOffset TimestampUtc) SaleActivity { get; }
    IReadOnlyList<ChatMessage> Snapshot();
    IReadOnlyList<ChatMessage> SaleSnapshot();
    void Start();
}
