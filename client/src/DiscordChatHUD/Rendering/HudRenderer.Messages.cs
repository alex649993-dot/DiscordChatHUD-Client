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

// HudRenderer — 채팅 메시지 배치와 그리기: 본문·이모지, 답장, 전달, 반응, 이름·시간·역할 배지, 실시간 메시지.
internal sealed partial class HudRenderer
{

    private static bool IsContinuation(ChatMessage previous, ChatMessage current)
    {
        if (previous.AuthorId == 0 || previous.AuthorId != current.AuthorId) return false;
        if (previous.ChannelId != current.ChannelId) return false;
        // Every reply begins a distinct visual group, even when the same
        // author sends it immediately after a normal message or another reply.
        // A later plain-text follow-up can still continue below that reply.
        if (current.Reply is not null) return false;
        // Forwarded snapshots follow the same rule: different originals are
        // distinct message groups even if the sender/time are identical.
        if (previous.Forward is { } previousForward
            && current.Forward is { } currentForward
            && (previousForward.SourceMessageId is null
                || currentForward.SourceMessageId is null
                || previousForward.SourceMessageId != currentForward.SourceMessageId
                || previousForward.SourceChannelId != currentForward.SourceChannelId))
            return false;
        var elapsed = current.Timestamp - previous.Timestamp;
        return elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromMinutes(7);
    }

    private void PrepareMessageLayoutCache(int availableWidth, int fontScalePercent, int mediaScalePercent, int emojiScalePercent)
    {
        _layoutRenderPass++;
        if (_layoutCacheAvailableWidth == availableWidth
            && _layoutCacheFontScalePercent == fontScalePercent
            && _layoutCacheMediaScalePercent == mediaScalePercent
            && _layoutCacheEmojiScalePercent == emojiScalePercent)
            return;

        _messageLayoutCache.Clear();
        _layoutCacheAvailableWidth = availableWidth;
        _layoutCacheFontScalePercent = fontScalePercent;
        _layoutCacheMediaScalePercent = mediaScalePercent;
        _layoutCacheEmojiScalePercent = emojiScalePercent;
    }

    // Preview 307: media laid out before its size was known (a Tenor/Klipy link
    // embed arrives after the message, and the GIF is still decoding) got a fixed
    // fallback box, and that layout stayed cached for the same message object, so
    // 306's decoded-size fallback never reached the screen. Remember which media
    // were boxed, and rebuild the layout once any of them has a decoded size.
    private List<MediaItem>? _layoutUnsizedMedia;

    private bool HasNewlySizedMedia(CachedMessageLayout cached)
    {
        if (cached.UnsizedMedia is not { } pending) return false;
        foreach (var item in pending)
            if (_media.TryGetDecodedSize(item) is not null) return true;
        return false;
    }

    private MessageLayout GetMessageLayout(
        Graphics graphics,
        RenderFonts fonts,
        ChatMessage message,
        int width,
        int mediaScalePercent,
        bool isContinuation)
    {
        var key = (message.Id, width, isContinuation);
        if (_messageLayoutCache.TryGetValue(key, out var cached)
            && ReferenceEquals(cached.Message, message)
            && !HasNewlySizedMedia(cached))
        {
            cached.LastUsedPass = _layoutRenderPass;
            return cached.Layout;
        }

        // The Discord stores retain at most 160 messages. Leave headroom for
        // replaced and live-card entries, then reset in one cheap operation so
        // a long-running HUD cannot grow the layout cache indefinitely.
        if (_messageLayoutCache.Count >= 384) _messageLayoutCache.Clear();
        var unsized = new List<MediaItem>();
        _layoutUnsizedMedia = unsized;
        MessageLayout layout;
        try { layout = BuildMessageLayout(graphics, fonts, message, width, mediaScalePercent, isContinuation); }
        finally { _layoutUnsizedMedia = null; }
        _messageLayoutCache[key] = new CachedMessageLayout(message, layout)
        {
            LastUsedPass = _layoutRenderPass,
            UnsizedMedia = unsized.Count > 0 ? unsized.ToArray() : null
        };
        return layout;
    }

    private static int PercentToAlpha(int percent)
        => (int)Math.Round(
            Math.Clamp(percent, 0, 100) * 255d / 100d,
            MidpointRounding.AwayFromZero);

    private MessageLayout BuildMessageLayout(
        Graphics graphics,
        RenderFonts fonts,
        ChatMessage message,
        int width,
        int mediaScalePercent,
        bool isContinuation)
    {
        var bodyLineHeight = Math.Max(18, (int)Math.Ceiling(fonts.Body.GetHeight(graphics))) + LineGap;
        var nameLineHeight = isContinuation
            ? 0
            : Math.Max(20, (int)Math.Ceiling(fonts.Name.GetHeight(graphics))) + LineGap;
        RichLine? replyContentLine = null;
        if (message.Reply is { } reply)
        {
            var replyAuthor = GetReplyAuthorDisplay(graphics, fonts, reply.AuthorName, width);
            var replyTextOffset = 29
                                  + MeasureText(graphics, replyAuthor, fonts.ReplyBold)
                                  + 9;
            var replyTextWidth = Math.Max(20, width - replyTextOffset - ReplyRightPadding);
            replyContentLine = BuildReplyPreviewLine(
                graphics,
                fonts,
                DiscordMessageParser.StripMentionMarkers(reply.Content),
                replyTextWidth);
        }
        // Reply previews use the normal inline-emoji height. This keeps the
        // arrow sequence compact while leaving enough room for actual emoji
        // images instead of drawing their raw markup as text.
        var replyLineHeight = Math.Max(bodyLineHeight, replyContentLine?.Height ?? 0);
        var content = message.Content;
        if (message.Media.Count > 0 && Uri.TryCreate(content, UriKind.Absolute, out _)) content = string.Empty;
        // The rounded glyph outline extends beyond MeasureString's reported
        // advance. Wrap text a little earlier without changing media edges.
        var richLines = BuildRichLines(graphics, fonts, content, Math.Max(20, width - TextWrapSafety));
        var y = message.Reply is null ? 0 : replyLineHeight;
        y += nameLineHeight;
        y += richLines.Sum(line => Math.Max(bodyLineHeight, line.Height));

        GameInviteLayout? invite = null;
        if (message.GameInvite is { } gameInvite)
        {
            y += richLines.Count > 0 ? 6 : 4;
            var titleHeight = (int)Math.Ceiling(fonts.SmallBold.GetHeight(graphics)) + 3;
            var rowHeight = Math.Max(32, (int)Math.Ceiling(fonts.Body.GetHeight(graphics)) + 3);
            var actionHeight = (int)Math.Ceiling(fonts.Small.GetHeight(graphics)) + 3;
            invite = new GameInviteLayout(gameInvite, Math.Min(320, width),
                16 + titleHeight + 6 + rowHeight + 6 + actionHeight, y, titleHeight, rowHeight);
            y += invite.Height + 4;
        }

        ForwardLayout? forward = null;
        if (message.Forward is not null)
        {
            if (richLines.Count > 0) y += 4;
            forward = BuildForwardLayout(graphics, fonts, message.Forward, width, mediaScalePercent);
            y += forward.Height;
            if (message.Media.Count > 0 || message.Reactions.Count > 0)
                y += ForwardContentGap;
        }

        var mediaBatches = new List<MediaBatchLayout>();
        if (message.Media.Count > 0)
        {
            if (forward is null)
                y += richLines.Count > 0 ? MediaAfterTextGap : MediaAfterHeaderGap;
            foreach (var batch in message.Media.Chunk(4))
            {
                var mediaLayout = BuildMediaBatch(batch, width, mediaScalePercent);
                mediaLayout.OffsetY = y;
                mediaBatches.Add(mediaLayout);
                y += mediaLayout.Height + MediaBatchGap;
            }
            y -= MediaBatchGap;
        }

        var reactions = BuildReactionLayout(graphics, fonts, message.Reactions, width);
        if (reactions.Count > 0)
        {
            if (y > nameLineHeight) y += 2;
            foreach (var row in reactions)
            {
                row.OffsetY = y;
                y += row.Height + 4;
            }
            y -= 4;
        }

        return new MessageLayout(
            message,
            replyContentLine,
            richLines,
            mediaBatches,
            reactions,
            forward,
            invite,
            Math.Max(nameLineHeight, y),
            replyLineHeight,
            nameLineHeight,
            bodyLineHeight,
            isContinuation);
    }

    private void DrawMessage(
        Graphics graphics,
        RenderFonts fonts,
        MessageLayout layout,
        int x,
        int y,
        int width,
        int nicknameBadgeAlpha,
        Color tintColor,
        bool isSaleTurn)
    {
        // Each drawing primitive checks its own painted bounds. Including the empty
        // message margin here falsely overlaps the preceding animation in a continuation.
        var cursorY = y;
        if (layout.Message.Reply is { } reply)
        {
            DrawReply(graphics, fonts, reply, layout.ReplyContentLine, x, cursorY, width);
            cursorY += layout.ReplyLineHeight;
        }

        if (!layout.IsContinuation)
        {
            DrawNameAndTime(
                graphics,
                fonts,
                layout.Message,
                x,
                cursorY,
                width,
                nicknameBadgeAlpha,
                tintColor,
                isSaleTurn);
            cursorY += layout.NameLineHeight;
        }
        if (layout.Message.IsMuteNotice && layout.ContentLines.Count > 0)
        {
            var blockHeight = layout.ContentLines.Sum(line => Math.Max(layout.BodyLineHeight, line.Height));
            var blockBounds = new RectangleF(x - 5, cursorY - 3, width + 10, blockHeight + 6);
            InvalidateAnimationOverlap(graphics, RectangleF.Inflate(blockBounds, 2, 2));
            using var blockPath = RoundedRectangle(blockBounds, 6);
            using var blockBorder = new Pen(Color.FromArgb(255, 235, 48, 64), 1.5f);
            graphics.DrawPath(blockBorder, blockPath);
        }
        foreach (var line in layout.ContentLines)
        {
            DrawRichLine(graphics, fonts, line, x, cursorY);
            cursorY += Math.Max(layout.BodyLineHeight, line.Height);
        }

        if (layout.GameInvite is { } invite)
        {
            DrawGameInvite(graphics, fonts, invite, x, y + invite.OffsetY, tintColor);
            cursorY = y + invite.OffsetY + invite.Height + 4;
        }

        if (layout.Forward is { } forward)
        {
            if (layout.ContentLines.Count > 0) cursorY += 4;
            DrawForward(graphics, fonts, forward, x, cursorY, width, tintColor);
            cursorY += forward.Height;
            if (layout.Message.Media.Count > 0 || layout.ReactionRows.Count > 0)
                cursorY += ForwardContentGap;
        }

        foreach (var batch in layout.MediaBatches)
            DrawMediaBatch(graphics, batch, x, y + batch.OffsetY);
        foreach (var row in layout.ReactionRows)
            DrawReactionRow(graphics, fonts, row, x, y + row.OffsetY, tintColor);
    }

    private sealed record RoleIconTone(bool IsDark);

    private static RoleIconTone MeasureRoleIconTone(Bitmap bitmap)
    {
        // Ignore transparent padding; weight visible samples by alpha. Cache
        // this once per decoded bitmap without keeping old cache frames alive.
        double luminanceSum = 0;
        double alphaSum = 0;
        var columns = Math.Min(16, bitmap.Width);
        var rows = Math.Min(16, bitmap.Height);
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var x = Math.Min(bitmap.Width - 1, (int)((column + 0.5) * bitmap.Width / columns));
            var y = Math.Min(bitmap.Height - 1, (int)((row + 0.5) * bitmap.Height / rows));
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.A < 32) continue;
            luminanceSum += (0.2126 * pixel.R + 0.7152 * pixel.G + 0.0722 * pixel.B) * pixel.A;
            alphaSum += pixel.A;
        }
        return new RoleIconTone(alphaSum > 0 && luminanceSum / alphaSum < 140);
    }

    private void DrawGameInvite(Graphics graphics, RenderFonts fonts, GameInviteLayout layout,
        int x, int y, Color tintColor)
    {
        var rectangle = new RectangleF(x, y, layout.Width, layout.Height);
        InvalidateAnimationOverlap(graphics, RectangleF.Inflate(rectangle, 2, 2));
        using var path = RoundedRectangle(rectangle, 8);
        using var fill = new SolidBrush(Color.FromArgb(180, 35, 37, 42));
        using var foreground = new SolidBrush(Color.FromArgb(255, 240, 242, 247));
        using var muted = new SolidBrush(Color.FromArgb(255, 185, 190, 201));
        using var link = new SolidBrush(Color.FromArgb(255, 88, 175, 245));
        var state = graphics.Save();
        graphics.SetClip(path, CombineMode.Intersect);
        graphics.FillPath(fill, path);
        var innerWidth = Math.Max(1, layout.Width - 16);
        DrawSoftText(graphics, Ellipsize(graphics, layout.Invite.KindLabel, fonts.SmallBold, innerWidth),
            fonts.SmallBold, muted, x + 8, y + 8);
        var rowY = y + 8 + layout.TitleHeight + 6;
        var nameX = x + 8;
        if (layout.Width >= 140 && !string.IsNullOrEmpty(layout.Invite.IconUrl))
        {
            var iconBox = new RectangleF(nameX, rowY + (layout.RowHeight - 32) / 2f, 32, 32);
            if (IsCachedImageVisible(graphics, layout.Invite.IconUrl, iconBox))
            {
                using var icon = _media.TryGetClone(layout.Invite.IconUrl, lowByteLimit: true);
                if (icon is not null)
                    graphics.DrawImage(icon.Bitmap, ContainRectangle(icon.Bitmap.Size, iconBox));
            }
            nameX += 40;
        }
        DrawSoftText(graphics, Ellipsize(graphics, layout.Invite.GameName, fonts.Body,
            Math.Max(1, x + layout.Width - 8 - (int)nameX)), fonts.Body, foreground,
            nameX, rowY + Math.Max(0, (layout.RowHeight - fonts.Body.GetHeight(graphics)) / 2f));
        // This is an informational HUD label. Joining uses the original card
        // in Discord, which owns the activity session and its current state.
        DrawSoftText(graphics, Ellipsize(graphics, layout.Invite.ActionLabel, fonts.Small, innerWidth),
            fonts.Small, link, x + 8, rowY + layout.RowHeight + 6);
        graphics.Restore(state);
    }

    private sealed record GameInviteLayout(GameInvitePreview Invite, int Width, int Height, int OffsetY, int TitleHeight, int RowHeight);

    private List<RichLine> BuildRichLines(
        Graphics graphics,
        RenderFonts fonts,
        string content,
        int maxWidth,
        bool allowJumboEmoji = true)
    {
        var lines = new List<RichLine>();
        if (string.IsNullOrWhiteSpace(content)) return lines;
        content = DiscordTextDisplay.FormatLinks(content);
        var jumboEmoji = allowJumboEmoji && ShouldUseJumboEmoji(content);
        foreach (var paragraph in content.Split('\n'))
        {
            var line = new RichLine();
            var cursor = 0;
            foreach (Match match in CustomEmojiRegex().Matches(paragraph))
            {
                AppendTextRange(graphics, fonts, paragraph[cursor..match.Index], maxWidth, lines, ref line, jumboEmoji);
                var animated = match.Groups[1].Success;
                var name = match.Groups[2].Value;
                var id = match.Groups[3].Value;
                var url = $"https://cdn.discordapp.com/emojis/{id}.{(animated ? "gif" : "png")}?size=96&quality=lossless";
                var emojiSize = jumboEmoji ? fonts.JumboEmojiSize : fonts.InlineEmojiSize;
                AppendUnit(graphics, fonts, lines, ref line, new RichToken(string.Empty, url, name, emojiSize + EmojiAdvancePadding, emojiSize, false, IsJumbo: jumboEmoji), maxWidth);
                cursor = match.Index + match.Length;
            }
            AppendTextRange(graphics, fonts, paragraph[cursor..], maxWidth, lines, ref line, jumboEmoji);
            lines.Add(line);
        }
        while (lines.Count > 0 && lines[^1].Tokens.Count == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private RichLine? BuildReplyPreviewLine(
        Graphics graphics,
        RenderFonts fonts,
        string content,
        int maxWidth)
    {
        var lines = BuildRichLines(
            graphics,
            fonts,
            content,
            maxWidth,
            allowJumboEmoji: false);
        if (lines.Count == 0) return null;

        var firstLine = lines[0];
        if (lines.Count > 1)
            AppendReplyEllipsis(graphics, fonts, firstLine, maxWidth);
        return firstLine;
    }

    private void AppendReplyEllipsis(
        Graphics graphics,
        RenderFonts fonts,
        RichLine line,
        int maxWidth)
    {
        const string ellipsis = "...";
        var ellipsisWidth = Math.Max(1, MeasureText(graphics, ellipsis, fonts.Body));
        while (line.Tokens.Count > 0 && line.Width + ellipsisWidth > maxWidth)
        {
            var lastIndex = line.Tokens.Count - 1;
            var last = line.Tokens[lastIndex];
            if (last.EmojiUrl is not null || last.IsMention || string.IsNullOrEmpty(last.Text))
            {
                line.Tokens.RemoveAt(lastIndex);
                line.Width = Math.Max(0, line.Width - last.Width);
                continue;
            }

            var elements = new List<string>();
            var enumerator = StringInfo.GetTextElementEnumerator(last.Text);
            while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
            if (elements.Count > 0) elements.RemoveAt(elements.Count - 1);
            var shortened = string.Concat(elements).TrimEnd();
            if (shortened.Length == 0)
            {
                line.Tokens.RemoveAt(lastIndex);
                line.Width = Math.Max(0, line.Width - last.Width);
                continue;
            }

            var font = last.UseEmojiFont ? fonts.Emoji : fonts.Body;
            var shortenedWidth = Math.Max(1, MeasureText(graphics, shortened, font));
            line.Tokens[lastIndex] = last with { Text = shortened, Width = shortenedWidth };
            line.Width = Math.Max(0, line.Width - last.Width + shortenedWidth);
        }

        line.Tokens.Add(new RichToken(ellipsis, null, string.Empty, ellipsisWidth, 0, false));
        line.Width += ellipsisWidth;
    }

    private void AppendTextRange(
        Graphics graphics,
        RenderFonts fonts,
        string text,
        int maxWidth,
        List<RichLine> lines,
        ref RichLine line,
        bool jumboEmoji)
    {
        var cursor = 0;
        while (cursor < text.Length)
        {
            var mentionStart = text.IndexOf(DiscordMessageParser.MentionStart, cursor);
            var channelStart = text.IndexOf(DiscordMessageParser.ChannelReferenceStart, cursor);
            var linkStart = text.IndexOf(DiscordTextDisplay.LinkStart, cursor);
            var referenceStart = mentionStart < 0
                ? channelStart
                : channelStart < 0
                    ? mentionStart
                    : Math.Min(mentionStart, channelStart);
            if (linkStart >= 0 && (referenceStart < 0 || linkStart < referenceStart)) referenceStart = linkStart;
            if (referenceStart < 0)
            {
                AppendPlainTextRange(graphics, fonts, text[cursor..], maxWidth, lines, ref line, jumboEmoji);
                break;
            }

            if (referenceStart > cursor)
                AppendPlainTextRange(graphics, fonts, text[cursor..referenceStart], maxWidth, lines, ref line, jumboEmoji);

            var isLink = referenceStart == linkStart;
            var isChannelReference = referenceStart == channelStart;
            var endMarker = isLink ? DiscordTextDisplay.LinkEnd : isChannelReference
                ? DiscordMessageParser.ChannelReferenceEnd
                : DiscordMessageParser.MentionEnd;
            var referenceEnd = text.IndexOf(endMarker, referenceStart + 1);
            if (referenceEnd < 0)
            {
                AppendPlainTextRange(graphics, fonts, text[(referenceStart + 1)..], maxWidth, lines, ref line, jumboEmoji);
                break;
            }

            var payload = text[(referenceStart + 1)..referenceEnd];
            var separator = payload.IndexOf(DiscordMessageParser.MentionIdSeparator);
            var display = separator >= 0 ? payload[(separator + 1)..] : payload;
            if (!string.IsNullOrWhiteSpace(display))
            {
                if (isLink)
                {
                    display = Ellipsize(graphics, display, fonts.Body, Math.Max(1, maxWidth - 4));
                    var linkWidth = MeasureText(graphics, display, fonts.Body) + 4;
                    AppendUnit(graphics, fonts, lines, ref line,
                        new RichToken(display, null, string.Empty, linkWidth, 0, false, IsLink: true), maxWidth);
                }
                else if (isChannelReference || !display.StartsWith('@'))
                {
                    AppendPlainTextRange(graphics, fonts, display, maxWidth, lines, ref line, jumboEmoji: false);
                }
                else
                {
                    display = Ellipsize(graphics, display, fonts.Body, Math.Max(12, maxWidth - 14));
                    var mentionWidth = Math.Max(1, MeasureText(graphics, display, fonts.Body)) + 14;
                    AppendUnit(
                        graphics, fonts, lines,
                        ref line,
                        new RichToken(display, null, string.Empty, mentionWidth, 0, false, true),
                        maxWidth);
                }
            }
            cursor = referenceEnd + 1;
        }
    }

    private void AppendPlainTextRange(
        Graphics graphics,
        RenderFonts fonts,
        string text,
        int maxWidth,
        List<RichLine> lines,
        ref RichLine line,
        bool jumboEmoji)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var emojiUrl = TwemojiAsset.GetUrl(element);
            RichToken token;
            if (emojiUrl is not null)
            {
                var emojiSize = jumboEmoji ? fonts.JumboEmojiSize : fonts.InlineEmojiSize;
                token = new RichToken(string.Empty, emojiUrl, element, emojiSize + EmojiAdvancePadding, emojiSize, true, IsJumbo: jumboEmoji);
            }
            else
            {
                var measured = Math.Max(1, MeasureText(graphics, element, fonts.Body));
                token = new RichToken(element, null, string.Empty, measured, 0, false);
            }
            if (string.IsNullOrWhiteSpace(element) && line.Width <= 0) continue;
            AppendUnit(graphics, fonts, lines, ref line, token, maxWidth);
        }
    }

    private void AppendUnit(Graphics graphics, RenderFonts fonts, List<RichLine> lines, ref RichLine line, RichToken token, int maxWidth)
    {
        if (token.EmojiUrl is not null && token.Width > maxWidth)
        {
            var targetImageSize = Math.Max(12, maxWidth - EmojiAdvancePadding);
            var scale = Math.Min(1f, targetImageSize / (float)Math.Max(1, token.Height));
            var scaledHeight = Math.Max(12, (int)Math.Floor(token.Height * scale));
            token = token with
            {
                Width = Math.Min(maxWidth, scaledHeight + EmojiAdvancePadding),
                Height = scaledHeight
            };
        }
        var merge = token.EmojiUrl is null && !token.IsMention && !token.IsLink
            && line.Tokens.Count > 0 && line.Tokens[^1].EmojiUrl is null
            && !line.Tokens[^1].IsMention && !line.Tokens[^1].IsLink
            && line.Tokens[^1].UseEmojiFont == token.UseEmojiFont;
        var mergedWidth = merge
            ? MeasureText(graphics, line.Tokens[^1].Text + token.Text, token.UseEmojiFont ? fonts.Emoji : fonts.Body)
            : 0;
        var nextWidth = merge ? line.Width - line.Tokens[^1].Width + mergedWidth : line.Width + token.Width;
        if (line.Tokens.Count > 0 && nextWidth > maxWidth)
        {
            lines.Add(line);
            line = new RichLine();
            if (token.EmojiUrl is null && string.IsNullOrWhiteSpace(token.Text)) return;
        }
        if (token.EmojiUrl is null && line.Tokens.Count > 0)
        {
            var previous = line.Tokens[^1];
            if (previous.EmojiUrl is null
                && previous.UseEmojiFont == token.UseEmojiFont
                && !previous.IsMention
                && !token.IsMention
                && !previous.IsLink
                && !token.IsLink)
            {
                var actualWidth = MeasureText(graphics, previous.Text + token.Text, token.UseEmojiFont ? fonts.Emoji : fonts.Body);
                line.Tokens[^1] = previous with { Text = previous.Text + token.Text, Width = actualWidth };
                line.Width += actualWidth - previous.Width;
                line.Height = Math.Max(line.Height, token.Height);
                return;
            }
        }
        line.Tokens.Add(token);
        line.Width += token.Width;
        line.Height = Math.Max(line.Height, token.Height);
    }

    private static bool ShouldUseJumboEmoji(string content)
    {
        var emojiCount = CustomEmojiRegex().Matches(content).Count;
        var remaining = CustomEmojiRegex().Replace(content, string.Empty);
        var enumerator = StringInfo.GetTextElementEnumerator(remaining);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (string.IsNullOrWhiteSpace(element)) continue;
            if (TwemojiAsset.GetUrl(element) is null) return false;
            emojiCount++;
        }
        return emojiCount is >= 1 and <= 27;
    }

    private ForwardLayout BuildForwardLayout(
        Graphics graphics,
        RenderFonts fonts,
        ForwardPreview forward,
        int width,
        int mediaScalePercent)
    {
        const int pad = 11;
        const int headerHeight = 26;
        var innerWidth = Math.Max(60, width - pad * 2);
        var lines = BuildRichLines(
            graphics,
            fonts,
            forward.Content,
            Math.Max(20, innerWidth - TextWrapSafety));
        if (lines.Count > 3) lines = lines.Take(3).ToList();
        var lineHeight = Math.Max(18, (int)Math.Ceiling(fonts.Small.GetHeight(graphics))) + 2;
        var items = forward.AllMedia.Take(3).ToArray();
        var imageSize = items.Length == 1
            ? ConstrainSize(CalculateSingleMediaSize(items[0], innerWidth, 112, mediaScalePercent),
                ScaleMediaDimension(ForwardMediaMaxWidth, mediaScalePercent), ScaleMediaDimension(ForwardMediaMaxHeight, mediaScalePercent))
            : Size.Empty;
        var batch = items.Length > 1 ? BuildMediaBatch(items, Math.Min(innerWidth, ForwardMediaMaxWidth), mediaScalePercent) : null;
        var imageHeight = batch?.Height ?? imageSize.Height;
        var textHeight = lines.Sum(line => Math.Max(lineHeight, line.Height));
        var imageGap = imageHeight == 0 ? 0 : textHeight > 0 ? 7 : 4;
        var reactionRows = BuildReactionLayout(graphics, fonts, forward.Reactions, innerWidth);
        var reactionHeight = reactionRows.Count == 0
            ? 0
            : reactionRows.Sum(row => row.Height + 4) - 4;

        var cursorY = pad + headerHeight + textHeight;
        var imageOffsetY = -1;
        if (imageHeight > 0)
        {
            cursorY += imageGap;
            imageOffsetY = cursorY;
            cursorY += imageHeight;
        }

        var reactionsOffsetY = -1;
        if (reactionHeight > 0)
        {
            cursorY += (imageHeight > 0 || textHeight > 0) ? 6 : 4;
            reactionsOffsetY = cursorY;
            cursorY += reactionHeight;
        }

        var height = cursorY + pad;
        return new ForwardLayout(
            forward,
            lines,
            reactionRows,
            height,
            imageSize.Width,
            batch,
            imageHeight,
            imageOffsetY,
            reactionsOffsetY,
            lineHeight);
    }

    private List<ReactionRow> BuildReactionLayout(Graphics graphics, RenderFonts fonts, IReadOnlyList<ReactionItem> reactions, int width)
    {
        var rows = new List<ReactionRow>();
        if (reactions.Count == 0) return rows;
        var current = new ReactionRow();
        foreach (var reaction in reactions)
        {
            var imageUrl = reaction.ImageUrl;
            var textWidth = MeasureText(graphics, imageUrl is null ? reaction.Name : string.Empty, fonts.EmojiSmall);
            var countWidth = MeasureText(graphics, reaction.Count.ToString(CultureInfo.InvariantCulture), fonts.Reaction);
            var emojiWidth = imageUrl is null ? textWidth : fonts.ReactionEmojiSize;
            var pillWidth = fonts.ReactionPaddingX + emojiWidth + fonts.ReactionCountGap + countWidth + fonts.ReactionPaddingX;
            if (current.Cells.Count > 0 && current.Width + fonts.ReactionGap + pillWidth > width)
            {
                rows.Add(current);
                current = new ReactionRow();
            }
            if (current.Cells.Count > 0) current.Width += fonts.ReactionGap;
            current.Cells.Add(new ReactionCell(reaction, current.Width, pillWidth));
            current.Width += pillWidth;
            var contentHeight = Math.Max(fonts.ReactionEmojiSize,
                (int)Math.Ceiling(Math.Max(fonts.Reaction.GetHeight(graphics), fonts.EmojiSmall.GetHeight(graphics))));
            current.Height = Math.Max(fonts.ReactionHeight, contentHeight + fonts.ReactionPaddingY * 2);
        }
        if (current.Cells.Count > 0) rows.Add(current);
        return rows;
    }

    private void DrawReply(
        Graphics graphics,
        RenderFonts fonts,
        ReplyPreview reply,
        RichLine? contentLine,
        int x,
        int y,
        int width)
    {
        using var iconBrush = new SolidBrush(Color.FromArgb(215, 236, 239, 244));
        using var nameBrush = new SolidBrush(Color.FromArgb(230, reply.AuthorColor));
        DrawSoftText(graphics, "↩", fonts.ReplyIcon, iconBrush, x, y - 1);
        var nameX = x + 29;
        var replyAuthor = GetReplyAuthorDisplay(graphics, fonts, reply.AuthorName, width);
        DrawSoftText(graphics, replyAuthor, fonts.ReplyBold, nameBrush, nameX, y + 2);
        var snippetX = nameX + MeasureText(graphics, replyAuthor, fonts.ReplyBold) + 9;
        if (contentLine is not null)
            DrawRichLine(graphics, fonts, contentLine, (int)snippetX, y, textAlpha: 165);
    }

    private string GetReplyAuthorDisplay(
        Graphics graphics,
        RenderFonts fonts,
        string authorName,
        int width)
    {
        var maximumWidth = Math.Max(
            24,
            Math.Min(width / 3, width - 29 - 9 - 20 - TextWrapSafety));
        return Ellipsize(graphics, authorName, fonts.ReplyBold, maximumWidth);
    }

    private void DrawNameAndTime(
        Graphics graphics,
        RenderFonts fonts,
        ChatMessage message,
        int x,
        int y,
        int width,
        int nicknameBadgeAlpha,
        Color tintColor,
        bool isSaleTurn)
    {
        var time = FormatMessageTime(message.Timestamp);
        var timeWidth = MeasureText(graphics, time, fonts.Time);
        const string saleTurnText = "판매 순서";
        var saleTurnWidth = isSaleTurn
            ? MeasureText(graphics, saleTurnText, fonts.TinyBold) + 14
            : 0;
        var saleTurnReservation = isSaleTurn ? saleTurnWidth + 6 : 0;
        // Server profile tags are intentionally omitted. Application badges
        // and the highest visible role icon remain useful identity markers.
        var visibleAuthorBadges = message.AuthorBadges
            .Where(authorBadge => authorBadge.Kind != AuthorBadgeKind.ServerTag)
            .Take(3)
            .ToList();
        if (message.IsBot && visibleAuthorBadges.All(badge => badge.Kind != AuthorBadgeKind.Application))
            visibleAuthorBadges.Insert(0, new AuthorBadge(AuthorBadgeKind.Application, "앱"));
        var authorBadgesWidth = MeasureAuthorBadges(graphics, fonts, visibleAuthorBadges);
        var authorBadgeReservation = authorBadgesWidth > 0
            ? AuthorNameToBadgeGap + authorBadgesWidth + AuthorBadgeBlockRightPadding + AuthorBadgeTimeGap
            : 6;
        var maximumNameWidth = Math.Max(
            24,
            width
            - timeWidth
            - saleTurnReservation
            - authorBadgeReservation
            - TextWrapSafety);
        var displayName = Ellipsize(graphics, message.AuthorName, fonts.Name, maximumNameWidth);
        var nameWidth = MeasureText(graphics, displayName, fonts.Name);
        var nameHeight = Math.Max(
            Math.Max(22f, fonts.Name.GetHeight(graphics) + 2f),
            visibleAuthorBadges.Count > 0 ? AuthorBadgeHeight + 2f : 0f);
        var badgeContentRight = x + nameWidth;
        if (visibleAuthorBadges.Count > 0)
            badgeContentRight += AuthorNameToBadgeGap + authorBadgesWidth + AuthorBadgeBlockRightPadding;
        else
            badgeContentRight += 4;
        var badge = new RectangleF(x - 4, y - 1, badgeContentRight - (x - 4), nameHeight);
        InvalidateAnimationOverlap(graphics, RectangleF.Inflate(badge, 2, 2));
        using (var badgePath = RoundedRectangle(badge, 5))
        // Keep the visible identity badges inside the same glass block as the
        // nickname so they read as one aligned unit.
        using (var badgeFill = new SolidBrush(Color.FromArgb(
                   nicknameBadgeAlpha,
                   tintColor.R,
                   tintColor.G,
                   tintColor.B)))
        {
            graphics.FillPath(badgeFill, badgePath);
        }
        using var nameBrush = new SolidBrush(message.AuthorColor);
        DrawSoftText(graphics, displayName, fonts.Name, nameBrush, x, y);

        if (visibleAuthorBadges.Count > 0)
        {
            var authorBadgeTop = badge.Top + (badge.Height - AuthorBadgeHeight) / 2f;
            DrawAuthorBadges(
                graphics,
                fonts,
                visibleAuthorBadges,
                x + nameWidth + AuthorNameToBadgeGap,
                authorBadgeTop,
                tintColor);
        }

        float nextX = badge.Right + (visibleAuthorBadges.Count > 0 ? AuthorBadgeTimeGap : 2);
        if (isSaleTurn)
        {
            var saleBadge = new RectangleF(nextX, y + 1, saleTurnWidth, 22);
            using var salePath = RoundedRectangle(saleBadge, 7);
            using var saleFill = new SolidBrush(Color.FromArgb(205, 232, 139, 28));
            graphics.FillPath(saleFill, salePath);
            using var saleTextBrush = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
            DrawSoftText(
                graphics,
                saleTurnText,
                fonts.TinyBold,
                saleTextBrush,
                saleBadge.Left + 7,
                saleBadge.Top + 4);
            nextX = saleBadge.Right + 6;
        }
        using var timeBrush = new SolidBrush(Color.FromArgb(225, 232, 235, 240));
        DrawSoftText(graphics, time, fonts.Time, timeBrush, nextX, y + 6);
    }

    private int MeasureAuthorBadges(
        Graphics graphics,
        RenderFonts fonts,
        IReadOnlyList<AuthorBadge> badges)
    {
        var total = 0;
        var visible = 0;
        foreach (var badge in badges.Where(badge => badge.Kind != AuthorBadgeKind.ServerTag).Take(3))
        {
            var width = badge.Kind switch
            {
                AuthorBadgeKind.RoleIcon => AuthorBadgeHeight,
                _ => 10 + MeasureText(graphics, badge.Text, fonts.TinyBold)
            };
            total += width;
            visible++;
        }
        return total + Math.Max(0, visible - 1) * AuthorBadgeGap;
    }

    private float DrawAuthorBadges(
        Graphics graphics,
        RenderFonts fonts,
        IReadOnlyList<AuthorBadge> badges,
        float x,
        float top,
        Color tintColor)
    {
        var visible = 0;
        foreach (var badge in badges.Where(badge => badge.Kind != AuthorBadgeKind.ServerTag).Take(3))
        {
            var width = badge.Kind switch
            {
                AuthorBadgeKind.RoleIcon => AuthorBadgeHeight,
                _ => 10 + MeasureText(graphics, badge.Text, fonts.TinyBold)
            };
            var bounds = new RectangleF(x, top, width, AuthorBadgeHeight);
            if (badge.Kind == AuthorBadgeKind.RoleIcon)
            {
                DrawAuthorBadgeImageOrEmoji(graphics, fonts, badge, bounds, tintColor);
            }
            else
            {
                using var path = RoundedRectangle(bounds, 6);
                var fillColor = badge.Kind == AuthorBadgeKind.Application
                    ? Color.FromArgb(238, 88, 101, 242)
                    : Color.FromArgb(205, 45, 47, 54);
                using var fill = new SolidBrush(fillColor);
                graphics.FillPath(fill, path);

                var textX = bounds.Left + 5;
                if (!string.IsNullOrWhiteSpace(badge.ImageUrl))
                {
                    var imageBounds = new RectangleF(
                        bounds.Left + 3,
                        bounds.Top + (bounds.Height - AuthorBadgeIconSize) / 2f,
                        AuthorBadgeIconSize,
                        AuthorBadgeIconSize);
                    DrawAuthorBadgeImageOrEmoji(graphics, fonts, badge, imageBounds, tintColor);
                    textX = imageBounds.Right + 3;
                }
                using var textBrush = new SolidBrush(Color.FromArgb(255, 248, 248, 250));
                var badgeTextHeight = fonts.TinyBold.GetHeight(graphics);
                DrawSoftText(
                    graphics,
                    badge.Text,
                    fonts.TinyBold,
                    textBrush,
                    textX,
                    bounds.Top + Math.Max(0f, (bounds.Height - badgeTextHeight) / 2f));
            }
            x = bounds.Right + AuthorBadgeGap;
            visible++;
        }
        return visible > 0 ? x - AuthorBadgeGap : x;
    }

    private bool IsCachedImageVisible(Graphics graphics, string url, RectangleF box)
    {
        // Reading dimensions is metadata-only: an off-screen GIF must not be
        // counted as animated or decompressed just to decide whether to draw it.
        var painted = _media.TryGetRenderFrameSize(url) is { } size
            ? ContainRectangle(size, box) : box;
        return graphics.IsVisible(RectangleF.Inflate(painted, 1f, 1f));
    }

    private void DrawAuthorBadgeImageOrEmoji(
        Graphics graphics,
        RenderFonts fonts,
        AuthorBadge badge,
        RectangleF bounds,
        Color tintColor)
    {
        if (!graphics.IsVisible(RectangleF.Inflate(bounds, 1f, 1f))) return;
        if (!string.IsNullOrWhiteSpace(badge.ImageUrl))
        {
            var imageBounds = bounds;
            if (badge.Kind == AuthorBadgeKind.RoleIcon) imageBounds.Inflate(-1f, -1f);
            if (!IsCachedImageVisible(graphics, badge.ImageUrl, imageBounds)) return;
            using var image = _media.TryGetClone(badge.ImageUrl, lowByteLimit: true);
            if (image is not null)
            {
                if (badge.Kind == AuthorBadgeKind.RoleIcon)
                {
                    // Reserve one pixel for a contour, without a circular backing.
                    bounds.Inflate(-1f, -1f);
                    DrawRoleIconOutline(graphics, image.Bitmap,
                        ContainRectangle(image.Bitmap.Size, bounds), tintColor);
                }
                else
                {
                    graphics.DrawImage(image.Bitmap, ContainRectangle(image.Bitmap.Size, bounds));
                }
                return;
            }
        }

        if (badge.Kind != AuthorBadgeKind.RoleIcon || string.IsNullOrWhiteSpace(badge.Text)) return;
        using var brush = new SolidBrush(Color.FromArgb(255, 240, 241, 245));
        var textWidth = MeasureText(graphics, badge.Text, fonts.RoleEmoji);
        var textHeight = fonts.RoleEmoji.GetHeight(graphics);
        DrawSoftText(
            graphics,
            badge.Text,
            fonts.RoleEmoji,
            brush,
            bounds.Left + Math.Max(0f, (bounds.Width - textWidth) / 2f),
            bounds.Top + Math.Max(0f, (bounds.Height - textHeight) / 2f));
    }

    private static void DrawRoleIconOutline(
        Graphics graphics, Bitmap bitmap, RectangleF bounds, Color tintColor)
    {
        // Bright artwork is drawn untouched. Only dark artwork on the dark
        // tint gets a faint subpixel contour; never recolor the source itself.
        var tone = RoleIconTones.GetValue(bitmap, MeasureRoleIconTone);
        if (!tone.IsDark || tintColor.GetBrightness() >= 0.5f)
        {
            graphics.DrawImage(bitmap, bounds);
            return;
        }
        const float channel = 1f;
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix(new float[][]
        {
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 0.08f, 0 },
            new float[] { channel, channel, channel, 0, 1 }
        }));
        var source = new RectangleF(0, 0, bitmap.Width, bitmap.Height);
        for (var step = 0; step < 4; step++)
        {
            var angle = step * Math.PI / 2;
            var dx = (float)Math.Cos(angle) * 0.1f;
            var dy = (float)Math.Sin(angle) * 0.1f;
            var destination = new PointF[]
            {
                new(bounds.Left + dx, bounds.Top + dy),
                new(bounds.Right + dx, bounds.Top + dy),
                new(bounds.Left + dx, bounds.Bottom + dy)
            };
            graphics.DrawImage(bitmap, destination, source, GraphicsUnit.Pixel, attributes);
        }
        graphics.DrawImage(bitmap, bounds);
    }

    private void DrawRichLine(Graphics graphics, RenderFonts fonts, RichLine line, int x, int y, int textAlpha = 255)
    {
        float cursorX = x;
        var bodyLineHeight = Math.Max(18, (int)Math.Ceiling(fonts.Body.GetHeight(graphics))) + LineGap;
        var renderedLineHeight = Math.Max(bodyLineHeight, line.Height);
        foreach (var token in line.Tokens)
        {
            if (token.EmojiUrl is not null)
            {
                var emojiTop = y + Math.Max(0f, (renderedLineHeight - token.Height) / 2f);
                var emojiBox = new RectangleF(cursorX + EmojiAdvancePadding / 2f, emojiTop,
                    Math.Max(1, token.Width - EmojiAdvancePadding), Math.Max(1, token.Height));
                if (!IsCachedImageVisible(graphics, token.EmojiUrl, emojiBox))
                { cursorX += token.Width; continue; }
                using var image = _media.TryGetClone(token.EmojiUrl, lowByteLimit: true);
                if (image is not null)
                {
                    DrawEmojiImage(graphics, token.EmojiUrl, image, emojiBox);
                }
                else
                {
                    using var fallback = new SolidBrush(Color.FromArgb(220, 235, 237, 242));
                    if (token.UseEmojiFont)
                        DrawSoftText(graphics, token.EmojiName, token.IsJumbo ? fonts.JumboEmojiFallback : fonts.Emoji, fallback, cursorX + EmojiAdvancePadding / 2f, emojiTop + 1);
                    else
                        DrawSoftText(graphics, $":{token.EmojiName}:", fonts.Small, fallback, cursorX + EmojiAdvancePadding / 2f, emojiTop + 3);
                }
            }
            else
            {
                var font = token.UseEmojiFont ? fonts.Emoji : fonts.Body;
                if (token.IsMention)
                {
                    var mentionHeight = Math.Max(20f, fonts.Body.GetHeight(graphics) + 1f);
                    var mentionRectangle = new RectangleF(cursorX + 2, y + 1, Math.Max(1, token.Width - 4), mentionHeight);
                    InvalidateAnimationOverlap(graphics, RectangleF.Inflate(mentionRectangle, 1, 1));
                    using var mentionPath = RoundedRectangle(mentionRectangle, 5);
                    using var mentionFill = new SolidBrush(Color.FromArgb(MentionFillAlpha, MentionFillRed, MentionFillGreen, MentionFillBlue));
                    graphics.FillPath(mentionFill, mentionPath);
                    using var mentionText = new SolidBrush(Color.FromArgb(
                        textAlpha,
                        MentionTextRed,
                        MentionTextGreen,
                        MentionTextBlue));
                    DrawSoftText(graphics, token.Text, fonts.Body, mentionText, cursorX + 7, y);
                }
                else
                {
                    using var brush = new SolidBrush(token.IsLink ? Color.FromArgb(textAlpha, 88, 175, 245) : Color.FromArgb(textAlpha, 245, 247, 250));
                    DrawSoftText(graphics, token.Text, font, brush, cursorX, y + (token.UseEmojiFont ? 1 : 0));
                }
            }
            cursorX += token.Width;
        }
    }

    private void DrawForward(
        Graphics graphics,
        RenderFonts fonts,
        ForwardLayout layout,
        int x,
        int y,
        int width,
        Color tintColor)
    {
        InvalidateAnimationOverlap(graphics, new RectangleF(x - 2, y - 2, width + 4, layout.Height + 4));
        using var path = RoundedRectangle(new RectangleF(x, y, width, layout.Height), 12);
        using var fill = new SolidBrush(Color.FromArgb(ForwardFillAlpha, tintColor.R, tintColor.G, tintColor.B));
        graphics.FillPath(fill, path);
        DrawGlassBorder(graphics, path, tintColor, ForwardBorderAlpha);

        const int pad = 11;
        using var pillPath = RoundedRectangle(new RectangleF(x + pad, y + pad, 58, 23), 8);
        using var pillFill = new SolidBrush(Color.FromArgb(ForwardFillAlpha, tintColor.R, tintColor.G, tintColor.B));
        graphics.FillPath(pillFill, pillPath);
        DrawGlassBorder(graphics, pillPath, tintColor, ForwardBorderAlpha);
        using var light = new SolidBrush(Color.FromArgb(225, 245, 247, 250));
        DrawSoftText(graphics, "↪", fonts.ForwardIcon, light, x + pad + 5, y + pad - 1);
        DrawSoftText(graphics, "전달", fonts.TinyBold, light, x + pad + 27, y + pad + 4);

        var nameX = x + pad + 66;
        using var nameBrush = new SolidBrush(layout.Forward.AuthorColor);
        var maximumNameWidth = Math.Max(20, width - (nameX - x) - pad - TextWrapSafety);
        var displayName = Ellipsize(graphics, layout.Forward.AuthorName, fonts.SmallBold, maximumNameWidth);
        DrawSoftText(graphics, displayName, fonts.SmallBold, nameBrush, nameX, y + pad + 3);
        var lineY = y + pad + 29;
        foreach (var line in layout.Lines)
        {
            DrawRichLine(graphics, fonts, line, x + pad, lineY);
            lineY += Math.Max(layout.LineHeight, line.Height);
        }
        if (layout.Batch is { } batch && layout.ImageOffsetY >= 0)
            DrawMediaBatch(graphics, batch, x + pad, y + layout.ImageOffsetY);
        else if (layout.ImageHeight > 0 && layout.ImageOffsetY >= 0 && layout.Forward.AllMedia.FirstOrDefault() is { } media)
        {
            var rectangle = new Rectangle(x + pad, y + layout.ImageOffsetY, layout.ImageWidth, layout.ImageHeight);
            DrawMediaCell(graphics, media, rectangle, preserveAspect: true);
        }
        if (layout.ReactionsOffsetY >= 0)
        {
            var reactionY = y + layout.ReactionsOffsetY;
            foreach (var row in layout.ReactionRows)
            {
                DrawReactionRow(graphics, fonts, row, x + pad, reactionY, tintColor);
                reactionY += row.Height + 4;
            }
        }
    }

    private void DrawReactionRow(Graphics graphics, RenderFonts fonts, ReactionRow row, int x, int y, Color tintColor)
    {
        foreach (var cell in row.Cells)
        {
            var rect = new RectangleF(x + cell.OffsetX, y, cell.Width, row.Height);
            if (!graphics.IsVisible(RectangleF.Inflate(rect, 2f, 2f))) continue;
            InvalidateAnimationOverlap(graphics, RectangleF.Inflate(rect, 2, 2));
            using var path = RoundedRectangle(rect, Math.Min(fonts.ReactionRadius, rect.Height / 2f));
            using var fill = new SolidBrush(Color.FromArgb(ReactionFillAlpha, tintColor.R, tintColor.G, tintColor.B));
            graphics.FillPath(fill, path);
            DrawGlassBorder(graphics, path, tintColor, ReactionBorderAlpha);
            var cursor = rect.Left + fonts.ReactionPaddingX;
            if (cell.Reaction.ImageUrl is { } imageUrl)
            {
                var emojiBox = new RectangleF(cursor,
                    rect.Top + (rect.Height - fonts.ReactionEmojiSize) / 2f,
                    fonts.ReactionEmojiSize, fonts.ReactionEmojiSize);
                if (IsCachedImageVisible(graphics, imageUrl, emojiBox))
                {
                    using var image = _media.TryGetClone(imageUrl, lowByteLimit: true);
                    if (image is not null)
                    {
                        DrawEmojiImage(graphics, imageUrl, image, emojiBox);
                    }
                    else
                    {
                        using var missing = new SolidBrush(Color.FromArgb(230, 235, 237, 242));
                        if (cell.Reaction.EmojiId is null)
                            DrawSoftText(graphics, cell.Reaction.Name, fonts.EmojiSmall, missing, cursor,
                                rect.Top + (rect.Height - fonts.EmojiSmall.GetHeight(graphics)) / 2f);
                        else
                            DrawSoftText(graphics, "◇", fonts.Reaction, missing, cursor,
                                rect.Top + (rect.Height - fonts.Reaction.GetHeight(graphics)) / 2f);
                    }
                }
                cursor += fonts.ReactionEmojiSize + fonts.ReactionCountGap;
            }
            else
            {
                using var emojiBrush = new SolidBrush(Color.FromArgb(245, 245, 247, 250));
                DrawSoftText(graphics, cell.Reaction.Name, fonts.EmojiSmall, emojiBrush, cursor,
                    rect.Top + (rect.Height - fonts.EmojiSmall.GetHeight(graphics)) / 2f);
                cursor += MeasureText(graphics, cell.Reaction.Name, fonts.EmojiSmall) + fonts.ReactionCountGap;
            }
            using var countBrush = new SolidBrush(Color.FromArgb(228, 235, 237, 242));
            DrawSoftText(graphics, cell.Reaction.Count.ToString(CultureInfo.InvariantCulture), fonts.Reaction, countBrush, cursor,
                rect.Top + (rect.Height - fonts.Reaction.GetHeight(graphics)) / 2f);
        }
    }
    private sealed record VinewoodPopupDisplay(IReadOnlyList<IReadOnlyList<StaffAssignmentPopupItem>> Rows);

    private static RectangleF GetLiveMessageRectangle(
        Graphics graphics,
        RenderFonts fonts,
        int width,
        int topClip,
        int contentBottom,
        MessageLayout liveLayout)
    {
        var availableWidth = Math.Max(80, width - PaddingLeft - PaddingRight);
        var headingHeight = (int)Math.Ceiling(fonts.SmallBold.GetHeight(graphics));
        var wantedHeight = LiveMessagePaddingTop
                           + headingHeight
                           + LiveMessageHeadingBodyGap
                           + liveLayout.Height
                           + LiveMessagePaddingBottom;
        var latestMessageBottom = contentBottom - LatestMessageBottomGap;
        var maximumHeight = Math.Max(1, latestMessageBottom - topClip);
        var cardHeight = Math.Min(wantedHeight, maximumHeight);
        return new RectangleF(
            PaddingLeft,
            Math.Max(topClip, latestMessageBottom - cardHeight),
            availableWidth,
            cardHeight);
    }

    private void DrawLiveMessage(
        Graphics graphics,
        RenderFonts fonts,
        MessageLayout liveLayout,
        RectangleF rectangle,
        int nicknameBadgeAlpha,
        Color tintColor,
        int backgroundTintAlpha)
    {
        InvalidateAnimationOverlap(graphics, RectangleF.Inflate(rectangle, 2, 2));
        using var path = RoundedRectangle(rectangle, 9);
        using var fill = new SolidBrush(Color.FromArgb(
            backgroundTintAlpha,
            tintColor.R,
            tintColor.G,
            tintColor.B));
        // SourceCopy gives the card exactly the configured HUD tint alpha and
        // clears any already-rendered message pixels underneath it. SourceOver
        // would stack the same tint twice and make this area more opaque.
        var fillState = graphics.Save();
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.FillPath(fill, path);
        graphics.Restore(fillState);
        using var border = new Pen(Color.FromArgb(96, 255, 255, 255), 1f);
        graphics.DrawPath(border, path);

        var innerWidth = Math.Max(20, (int)rectangle.Width - LiveMessagePaddingX * 2);
        const string heading = "현재 채팅";
        using var headingBrush = new SolidBrush(Color.FromArgb(225, 240, 242, 247));
        var headingY = rectangle.Top + LiveMessagePaddingTop;
        DrawSoftText(
            graphics,
            heading,
            fonts.SmallBold,
            headingBrush,
            rectangle.Left + LiveMessagePaddingX,
            headingY);

        var messageY = headingY
                       + (int)Math.Ceiling(fonts.SmallBold.GetHeight(graphics))
                       + LiveMessageHeadingBodyGap;
        var messageState = graphics.Save();
        graphics.SetClip(
            new RectangleF(
                rectangle.Left + LiveMessagePaddingX,
                messageY,
                innerWidth,
                Math.Max(1, rectangle.Bottom - LiveMessagePaddingBottom - messageY)),
            CombineMode.Intersect);
        DrawMessage(
            graphics,
            fonts,
            liveLayout,
            (int)rectangle.Left + LiveMessagePaddingX,
            (int)Math.Round(messageY),
            innerWidth,
            nicknameBadgeAlpha,
            tintColor,
            isSaleTurn: false);
        graphics.Restore(messageState);
    }
}
