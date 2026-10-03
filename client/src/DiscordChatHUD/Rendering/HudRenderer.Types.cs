using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Rendering;

// HudRenderer — 렌더러 내부 배치 레코드와 글꼴 묶음.
internal sealed partial class HudRenderer
{

    private sealed class RenderFonts : IDisposable
    {
        public RenderFonts(FontFamily body, FontFamily emoji, float scale, float emojiScale, float reactionEmojiScale = 1.05f)
        {
            // Manual scaling now reaches 30%. Keep only a very small safety
            // floor so values below the former 70% limit actually render
            // smaller instead of being pinned to the old 11-13px minimums.
            Body = CreateFont(body, Math.Max(6, 19 * scale), FontStyle.Regular);
            Name = CreateFont(body, Math.Max(6, 20 * scale), FontStyle.Bold);
            Small = CreateFont(body, Math.Max(5, 13 * scale), FontStyle.Regular);
            SmallBold = CreateFont(body, Math.Max(5, 13 * scale), FontStyle.Bold);
            // Keep the main chat and nickname sizes unchanged. The compact
            // two-line sale footer uses its own slightly larger 15px type.
            SaleStatus = CreateFont(body, Math.Max(5, 15 * scale), FontStyle.Regular);
            SaleStatusBold = CreateFont(body, Math.Max(5, 15 * scale), FontStyle.Bold);
            Reply = CreateFont(body, Math.Max(5, 16 * scale), FontStyle.Regular);
            ReplyBold = CreateFont(body, Math.Max(5, 16 * scale), FontStyle.Bold);
            ReplyIcon = CreateFont(body, Math.Max(6, 18 * scale), FontStyle.Regular);
            ForwardIcon = CreateFont(body, Math.Max(6, 19 * scale), FontStyle.Regular);
            TinyBold = CreateFont(body, Math.Max(5, 11 * scale), FontStyle.Bold);
            // Time labels remain visually subordinate to the fixed 17px icons.
            StaffAssignment = CreateFont(body, Math.Clamp(12 * scale, 5, 14), FontStyle.Bold);
            // Warehouse numbers fit the reduced 17px assignment icons.
            StaffAssignmentNumber = CreateFont(body, 8, FontStyle.Bold);
            Time = CreateFont(body, Math.Max(5, 12 * scale), FontStyle.Regular);
            Clock = CreateFont(body, Math.Max(5, 17 * scale), FontStyle.Regular);
            Channel = CreateFont(body, Math.Max(5, 15 * scale), FontStyle.Bold);
            Reaction = CreateFont(body, Math.Max(5, 14 * scale), FontStyle.Regular);
            Emoji = CreateFont(emoji, Math.Max(7, 25 * scale), FontStyle.Regular);
            JumboEmojiFallback = CreateFont(emoji, Math.Max(7, 25 * emojiScale), FontStyle.Regular);
            SaleEmojiFallback = CreateFont(emoji, Math.Max(7, 23 * scale), FontStyle.Regular);
            EmojiSmall = CreateFont(emoji, Math.Max(6, 20 * scale * reactionEmojiScale), FontStyle.Regular);
            RoleEmoji = CreateFont(emoji, Math.Max(6, 20 * scale), FontStyle.Regular);
            BusinessIconSize = ScaleGlyph(BusinessHudIconSize, scale);
            InlineEmojiSize = Math.Clamp((int)Math.Round(24 * scale), 7, 48);
            JumboEmojiSize = Math.Clamp((int)Math.Round(52 * emojiScale), 16, 104);
            // Inline and reaction emoji follow text; emoji-only messages use emojiScale.
            ReactionEmojiSize = ScaleGlyph(HudRenderer.ReactionEmojiSize, scale * reactionEmojiScale);
            ReactionPaddingX = ScaleGlyph(ReactionPillPaddingX, scale);
            ReactionPaddingY = ScaleGlyph(3, scale);
            ReactionCountGap = ScaleGlyph(ReactionEmojiCountGap, scale);
            ReactionGap = ScaleGlyph(ReactionPillGap, scale);
            ReactionHeight = ScaleGlyph(ReactionPillHeight, scale);
            ReactionRadius = ScaleGlyph(8, scale);
        }

        private static int ScaleGlyph(int baseline, float fontScale)
            => Math.Max(1, (int)Math.Round(baseline * fontScale));

        public Font Body { get; }
        public Font Name { get; }
        public Font Small { get; }
        public Font SmallBold { get; }
        public Font SaleStatus { get; }
        public Font SaleStatusBold { get; }
        public Font Reply { get; }
        public Font ReplyBold { get; }
        public Font ReplyIcon { get; }
        public Font ForwardIcon { get; }
        public Font TinyBold { get; }
        public Font StaffAssignment { get; }
        public Font StaffAssignmentNumber { get; }
        public Font Time { get; }
        public Font Clock { get; }
        public Font Channel { get; }
        public Font Reaction { get; }
        public Font Emoji { get; }
        public Font JumboEmojiFallback { get; }
        public Font EmojiSmall { get; }
        public Font SaleEmojiFallback { get; }
        public Font RoleEmoji { get; }
        public int BusinessIconSize { get; }
        public int InlineEmojiSize { get; }
        public int JumboEmojiSize { get; }
        public int ReactionEmojiSize { get; }
        public int ReactionPaddingX { get; }
        public int ReactionPaddingY { get; }
        public int ReactionCountGap { get; }
        public int ReactionGap { get; }
        public int ReactionHeight { get; }
        public int ReactionRadius { get; }

        private static Font CreateFont(FontFamily family, float size, FontStyle style)
        {
            var actualStyle = FontStyle.Regular;
            try
            {
                actualStyle = family.IsStyleAvailable(style) ? style : FontStyle.Regular;
            }
            catch { }

            var pixelSize = Math.Max(5f, (float)Math.Round(size));
            try { return new Font(family, pixelSize, actualStyle, GraphicsUnit.Pixel); }
            catch { return new Font(family, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel); }
        }

        public void Dispose()
        {
            Body.Dispose(); Name.Dispose(); Small.Dispose(); SmallBold.Dispose();
            SaleStatus.Dispose(); SaleStatusBold.Dispose(); Reply.Dispose(); ReplyBold.Dispose();
            ReplyIcon.Dispose(); ForwardIcon.Dispose(); TinyBold.Dispose(); StaffAssignment.Dispose(); StaffAssignmentNumber.Dispose();
            Time.Dispose(); Clock.Dispose(); Channel.Dispose(); Reaction.Dispose(); Emoji.Dispose(); JumboEmojiFallback.Dispose(); EmojiSmall.Dispose(); SaleEmojiFallback.Dispose(); RoleEmoji.Dispose();
        }
    }

    private sealed record MessageLayout(
        ChatMessage Message,
        RichLine? ReplyContentLine,
        List<RichLine> ContentLines,
        List<MediaBatchLayout> MediaBatches,
        List<ReactionRow> ReactionRows,
        ForwardLayout? Forward,
        GameInviteLayout? GameInvite,
        int Height,
        int ReplyLineHeight,
        int NameLineHeight,
        int BodyLineHeight,
        bool IsContinuation);

    private sealed record CachedMessageLayout(ChatMessage Message, MessageLayout Layout)
    {
        public long LastUsedPass { get; set; }
        /// <summary>Media sized with the fallback box because no size was known yet.</summary>
        public MediaItem[]? UnsizedMedia { get; init; }
    }

    private sealed class RichLine
    {
        public List<RichToken> Tokens { get; } = [];
        public float Width { get; set; }
        public int Height { get; set; }
    }

    private sealed record RichToken(
        string Text,
        string? EmojiUrl,
        string EmojiName,
        float Width,
        int Height,
        bool UseEmojiFont,
        bool IsMention = false,
        bool IsLink = false,
        bool IsJumbo = false);

    private sealed record SalePreviewToken(
        string Text,
        string? EmojiUrl,
        string Fallback,
        int Width,
        bool UseEmojiFont);

    private sealed record SaleStatusLine(
        string Prefix,
        string Preview,
        string Suffix,
        bool IsCurrent,
        bool Indented = false);

    private sealed class MediaBatchLayout(List<MediaCell> cells, int height)
    {
        public List<MediaCell> Cells { get; } = cells;
        public int Height { get; } = height;
        public int OffsetY { get; set; }
    }

    private sealed record MediaCell(MediaItem Media, Rectangle Rectangle, bool PreserveAspect = false);

    private sealed class ReactionRow
    {
        public List<ReactionCell> Cells { get; } = [];
        public int Width { get; set; }
        public int Height { get; set; }
        public int OffsetY { get; set; }
    }

    private sealed record ReactionCell(ReactionItem Reaction, int OffsetX, int Width);

    private sealed record ForwardLayout(
        ForwardPreview Forward,
        List<RichLine> Lines,
        List<ReactionRow> ReactionRows,
        int Height,
        int ImageWidth,
        MediaBatchLayout? Batch,
        int ImageHeight,
        int ImageOffsetY,
        int ReactionsOffsetY,
        int LineHeight);
}
