using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

internal sealed class SaleHudVisibility(bool shown)
{
    private bool shown = shown;
    private bool pending;
    private bool manuallyHidden;
    private ulong lastMessageId;

    internal void SetPending(bool value)
    {
        pending = value;
        // Closing the queue ends a manual hide, including a later reopening
        // through an edited post or removal of its completion reaction.
        if (!value) manuallyHidden = false;
    }

    internal void Observe(IReadOnlyList<ChatMessage> messages, bool automatic)
    {
        var latest = messages.Where(SaleSequence.IsQueueShapedMessage)
            .Select(message => message.Id).DefaultIfEmpty().Max();
        if (latest <= lastMessageId) return;
        lastMessageId = latest;
        // Replayed history does not override a manual hide. A new sale can,
        // even when the original timestamp is old or the PC clock differs.
        if (automatic) { shown = true; manuallyHidden = false; }
    }

    internal bool Visible(bool automatic)
        => automatic ? pending && !manuallyHidden : shown;

    internal void SetManual(bool value)
    {
        shown = value;
        manuallyHidden = !value;
    }

    internal void Toggle(bool automatic) => SetManual(!Visible(automatic));
}
