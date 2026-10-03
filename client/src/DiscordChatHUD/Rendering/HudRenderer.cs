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

internal sealed partial class HudRenderer : IDisposable
{
    private const int PaddingLeft = 11;
    private const int PaddingRight = 11;
    private const int PaddingY = 14;
    private const int TopReservedY = 34;
    // Without per-message cards, keep unrelated messages readable without
    // reintroducing the large vertical padding the cards used to need.
    private const int GroupGap = 36;
    private const int SameAuthorGap = 1;
    private const int ForwardContentGap = 6;
    private const int LatestMessageBottomGap = 8;
    private const int LineGap = 1;
    // Discord leaves a small, deliberate gap above attachments even when a
    // message contains no body text. The old layout skipped that gap for
    // image-only posts, making the thumbnail look glued to the nickname.
    private const int MediaAfterHeaderGap = 5;
    private const int MediaAfterTextGap = 7;
    private const int MediaBatchGap = 4;
    private const int ReplyRightPadding = 14;
    private const int TextWrapSafety = 12;
    private const int SaleStatusMinHeight = 58;
    private const int SaleStatusGap = 8;
    private const int SaleStatusCanvasBottomGap = 1;
    private const int SaleStatusPadding = 10;
    private const int BusinessHudPaddingY = 6;
    private const int SaleStatusLineGap = 2;
    private const int SaleStatusEmojiSize = 17;
    private const int VinewoodPopupTop = 4;
    private const int VinewoodPopupPaddingX = 8;
    private const int StaffAssignmentIconSize = SaleStatusEmojiSize;
    private const int StaffAssignmentIdleDotSize = 10;
    private const int StaffAssignmentIconTextGap = 1;
    private const int StaffAssignmentItemGap = 8;
    private const int StaffAssignmentNumberCircleSize = 10;
    private static readonly Color BusinessActiveBorder = Color.FromArgb(250, 45, 85);
    private const int BusinessHudIconSize = 17;
    private const int LiveMessagePaddingX = 10;
    private const int LiveMessagePaddingTop = 6;
    private const int LiveMessageHeadingBodyGap = 4;
    private const int LiveMessagePaddingBottom = 8;
    private const int LiveMessageContentClipGap = 2;
    // 디스코드와 같은 방식이다. 비율에 따라 단계를 나누지 않고, 상자 하나에
    // 비율을 유지한 채 넣기만 한다(확대는 하지 않는다). 그러면 가로로 긴 것은
    // 폭까지 크게, 세로로 긴 것은 높이에 먼저 걸려 작게, 정사각은 그 중간이
    // 저절로 된다.
    //
    // 중요한 건 그 상자의 가로:세로 비다. 디스코드는 상자가 550x350(1.57)이고
    // 메시지 폭도 550이라 폭과 높이 상한의 균형이 맞는다. HUD 는 폭이 좁은데
    // 높이 상한만 크게 남아 있으면 실질 상자가 세로로 길어져(폭 215 / 높이 280
    // -> 0.77) 세로 사진이 화면을 잡아먹는다. 그래서 높이 상한을 쓸 수 있는
    // 폭에서 이 비로 계산한다. HUD 폭이 달라져도 디스코드와 같은 인상이 된다.
    private const double MediaBoxAspect = 550d / 350d;
    // HUD 가 아주 넓을 때를 위한 천장. 좁은 HUD 에서는 위의 비가 먼저 걸린다.
    private const int ImageMaxWidth = 550;
    private const int ImageMaxHeight = 350;


    // 스티커와 거의 정사각형인 GIF. 원래 정사각이라 상자 비를 적용하지 않는다.
    private const int CompactAnimatedMaxSize = 160;
    private const int ForwardMediaMaxWidth = 220;
    private const int ForwardMediaMaxHeight = 160;
    private const int PairedMediaMaxHeight = 220;
    // A multi-attachment row needs a stable, useful thumbnail height at
    // 100%. Deriving its height from every source aspect ratio made a single
    // wide image shrink the whole row into a thin strip.
    private const double PairedMediaCellAspect = 1.18d;
    private const int MediaGridGap = 3;
    private const int SingleMediaRadius = 8;
    private const int MediaGridRadius = 5;
    // Adjacent emoji should sit flush horizontally. The old 6px advance added
    // 3px to both sides of every emoji, so sequences looked artificially spaced.
    private const int EmojiAdvancePadding = 0;
    private const int AuthorBadgeHeight = 20;
    private const int AuthorBadgeIconSize = 16;
    private const int AuthorBadgeGap = 4;
    private const int AuthorNameToBadgeGap = 6;
    private const int AuthorBadgeBlockRightPadding = 4;
    private const int AuthorBadgeTimeGap = 10;
    private const int ForwardFillAlpha = 18;
    // Discord's compact reaction control has a small but comfortably padded
    // square emoji. Keep custom Discord emoji and Twemoji on this exact grid.
    private const int ReactionEmojiSize = 20;
    private const int ReactionPillHeight = 30;
    private const int ReactionPillPaddingX = 7;
    private const int ReactionEmojiCountGap = 5;
    private const int ReactionPillGap = 6;
    // Fixed Discord-style mention colors keep the chip identical in light and
    // dark tint modes instead of changing with the game scene underneath.
    private const int MentionFillAlpha = 255;
    private const int MentionFillRed = 59;
    private const int MentionFillGreen = 63;
    private const int MentionFillBlue = 101;
    private const int MentionTextRed = 205;
    private const int MentionTextGreen = 215;
    private const int MentionTextBlue = 255;
    // The selected glass tint is painted underneath messages/media so image
    // colors remain untouched.
    private const int ReactionFillAlpha = 20;
    private const int ForwardBorderAlpha = 76;
    private const int ReactionBorderAlpha = 92;
    private const float GlassBorderWidth = 1.0f;
    private readonly MediaCache _media;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Bitmap, RoleIconTone> RoleIconTones = new();
    private readonly PrivateFontCollection _privateFonts = new();
    private readonly FontFamily _bodyFamily;
    private readonly FontFamily _emojiFamily;
    private readonly Image? _staffHangarIcon;
    private readonly Image? _staffWarehouseIcon;
    private readonly Image? _businessBunkerIcon;
    private readonly Image? _businessAcidLabIcon;
    private readonly Dictionary<string,Image> _extraBusinessIcons = new();
    private readonly object _bitmapPoolGate = new();
    private readonly Action<Bitmap> _returnRenderBitmap;
    private Bitmap? _pooledBitmap;
    private RenderFonts? _cachedFonts;
    private readonly HudTextCache _textCache = new();
    private int _cachedFontScalePercent = -1;
    private int _cachedEmojiScalePercent = -1;
    private int _reactionEmojiScalePercent = 105;
    private int _cachedReactionEmojiScalePercent = -1;
    private readonly Dictionary<(ulong MessageId, int Width, bool Continuation), CachedMessageLayout> _messageLayoutCache = [];
    private long _layoutRenderPass;
    private int _layoutCacheAvailableWidth = -1;
    private int _layoutCacheFontScalePercent = -1;
    private int _layoutCacheMediaScalePercent = -1;
    private int _layoutCacheEmojiScalePercent = -1;
    private Bitmap? _textScene;
    private TextSceneKey? _textSceneKey;
    private sealed record TextSceneKey(IReadOnlyList<ChatMessage> Messages, RenderFonts Fonts,
        Rectangle Area, int Height, int Scroll, int BackgroundAlpha, int NicknameAlpha, int NicknameGroupGap,
        int Tint, ulong? SaleTurn, string IconSources);

    private string? GetTextSceneSources(IReadOnlyList<ChatMessage> messages)
    {
        static bool Plain(string? text) => string.IsNullOrEmpty(text) || !text.Any(c => c == '<' || char.IsSurrogate(c)
            || char.GetUnicodeCategory(c) == UnicodeCategory.OtherSymbol);
        if (messages.Count == 0 || messages.Any(m => m.Media.Count != 0 || m.Reactions.Count != 0
            || m.Forward is not null || m.GameInvite is not null || !Plain(m.Content)
            || (m.Reply is not null && !Plain(m.Reply.Content)))) return null;
        var sources = new StringBuilder();
        foreach (var message in messages)
        foreach (var badge in message.AuthorBadges)
        {
            if (string.IsNullOrWhiteSpace(badge.ImageUrl)) continue;
            // Inspect identity without acquiring an animated frame. The author's
            // header can be clipped even when the rest of this message is visible.
            if (!_media.TryGetStaticRenderSourceId(badge.ImageUrl, out var sourceId)) return null;
            sources.Append(sourceId).Append(',');
        }
        return sources.ToString();
    }

    private bool _disposed;

    public HudRenderer(MediaCache media)
    {
        _media = media;
        _returnRenderBitmap = ReturnRenderBitmap;
        _checkAnimationPaint = InvalidateAnimationOverlap;
        FontFamily? loadedFamily = null;
        try
        {
            if (!File.Exists(AppPaths.FontPath))
                throw new FileNotFoundException("HUD 글꼴 파일을 찾을 수 없음", AppPaths.FontPath);
            _privateFonts.AddFontFile(AppPaths.FontPath);
            var families = _privateFonts.Families;
            loadedFamily = families.FirstOrDefault();
            for (var index = 1; index < families.Length; index++) families[index].Dispose();
            if (loadedFamily is null) throw new InvalidDataException("HUD 글꼴 패밀리를 읽지 못함");
            AppLog.Info($"HUD 글꼴 로드 완료: {loadedFamily.Name}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"HUD 글꼴 로드 실패, 시스템 글꼴 사용: {ex.Message}");
        }
        _bodyFamily = loadedFamily ?? FontFamily.GenericSansSerif;
        try { _emojiFamily = new FontFamily("Segoe UI Emoji"); }
        catch { _emojiFamily = _bodyFamily; }
        _staffHangarIcon = TryLoadHudImage(AppPaths.StaffHangarIconPath);
        _staffWarehouseIcon = TryLoadHudImage(AppPaths.StaffWarehouseIconPath);
        _businessBunkerIcon = TryLoadHudImage(AppPaths.BusinessBunkerIconPath);
        _businessAcidLabIcon = TryLoadHudImage(AppPaths.BusinessAcidLabIconPath);
        foreach(var key in new[]{"cocaine","meth","cash","weed","documents","nightclub"})
        {
            using var resource=typeof(HudRenderer).Assembly.GetManifestResourceStream("BusinessHudIcons."+key+".png");
            if(resource is null)continue;
            using var source=Image.FromStream(resource);_extraBusinessIcons[key]=new Bitmap(source);
        }
    }

    private static Image? TryLoadHudImage(string path)
    {
        try
        {
            return File.Exists(path) ? Image.FromFile(path) : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"HUD 아이콘 로드 실패({Path.GetFileName(path)}): {ex.Message}");
            return null;
        }
    }

    private RenderFonts GetRenderFonts(int fontScalePercent, int emojiScalePercent)
    {
        if (_cachedFonts is not null && _cachedFontScalePercent == fontScalePercent
            && _cachedEmojiScalePercent == emojiScalePercent
            && _cachedReactionEmojiScalePercent == _reactionEmojiScalePercent)
            return _cachedFonts;

        _textCache.Clear();
        _cachedFonts?.Dispose();
        _cachedFontScalePercent = fontScalePercent;
        _cachedEmojiScalePercent = emojiScalePercent;
        _cachedReactionEmojiScalePercent = _reactionEmojiScalePercent;
        _cachedFonts = new RenderFonts(
            _bodyFamily,
            _emojiFamily,
            fontScalePercent / 100f,
            emojiScalePercent / 100f, _reactionEmojiScalePercent / 100f);
        return _cachedFonts;
    }

    private Bitmap RentRenderBitmap(int width, int height)
    {
        lock (_bitmapPoolGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HudRenderer));
            if (_pooledBitmap is not null
                && _pooledBitmap.Width == width
                && _pooledBitmap.Height == height)
            {
                var reused = _pooledBitmap;
                _pooledBitmap = null;
                return reused;
            }
            _pooledBitmap?.Dispose();
            _pooledBitmap = null;
        }

        var created = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        created.SetResolution(96, 96);
        return created;
    }

    private void ReturnRenderBitmap(Bitmap bitmap)
    {
        var dispose = false;
        lock (_bitmapPoolGate)
        {
            if (_disposed || _pooledBitmap is not null)
                dispose = true;
            else
                _pooledBitmap = bitmap;
        }
        if (dispose) bitmap.Dispose();
    }

    // Called only before starting the render worker, so font/layout resources
    // are never measured concurrently with drawing.
    public int MeasureLiveMessageHeight(ChatMessage message, int width, int fontScale, int mediaScale, int emojiScale, int reactionScale)
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        ConfigureGraphics(graphics);
        using var fonts = new RenderFonts(_bodyFamily, _emojiFamily, fontScale / 100f, emojiScale / 100f, reactionScale / 100f);
        var layout = BuildMessageLayout(graphics, fonts, message,
            Math.Max(20, width - PaddingLeft - PaddingRight - LiveMessagePaddingX * 2), mediaScale, false);
        var wanted = LiveMessagePaddingTop + (int)Math.Ceiling(fonts.SmallBold.GetHeight(graphics))
            + LiveMessageHeadingBodyGap + layout.Height + LiveMessagePaddingBottom + SaleStatusGap;
        return Math.Clamp(wanted, 48, 360);
    }

    public RenderedHud Render(
        int width,
        int height,
        int fontScalePercent,
        int mediaScalePercent,
        int emojiScalePercent,
        IReadOnlyList<ChatMessage> messages,
        string channelLabel,
        string status,
        int requestedScrollPixels,
        int backgroundTintAlpha,
        int nicknameBadgeAlpha,
        int saleTintAlpha,
        int staffAssignmentTintAlpha,
        bool darkTint,
        bool clockInHeader,
        bool clockOnLeft,
        SaleSequenceStatus? saleStatus,
        bool showSaleStatus,
        bool showBusinessSupplies,
        bool businessTrackingOnline,
        IReadOnlyList<BusinessSupplyEntry> businessSupplies,
        string businessHudTargetKey,
        string businessDisplayMode,
        IReadOnlyList<RemoteStaffTimerEntry> remoteStaffTimers,
        bool showVinewoodTimerPopup,
        ChatMessage? liveMessage,
        bool animationOnly = false,
        int mediaOpacityPercent = 100,
        int reactionEmojiScalePercent = 105,
        int liveMessageReservedHeight = 0, string? sessionLabel = null, int businessTintAlpha = 38,
        int nicknameGroupGap = GroupGap,
        IReadOnlyList<string>? expandedKeys = null, IReadOnlyList<string>? valueKeys = null, int businessTargetCount = 1, bool businessIconOnly = false)
    {
        nicknameGroupGap = Math.Clamp(nicknameGroupGap, 0, 80);
        businessTintAlpha = Math.Clamp(businessTintAlpha, 0, 100);
        if (_businessTintAlpha != businessTintAlpha) { _businessTintAlpha = businessTintAlpha; ClearAnimationScene(); }
        reactionEmojiScalePercent = Math.Clamp(reactionEmojiScalePercent, 30, 200);
        if (_reactionEmojiScalePercent != reactionEmojiScalePercent)
        {
            _reactionEmojiScalePercent = reactionEmojiScalePercent;
            _messageLayoutCache.Clear();
            ClearAnimationScene();
        }
        mediaOpacityPercent = Math.Clamp(mediaOpacityPercent, 0, 100);
        if (_mediaOpacityPercent != mediaOpacityPercent) { ClearAnimationScene(); _mediaOpacityPercent = mediaOpacityPercent; }
        if (animationOnly && TryRenderAnimation(width, height) is { } animation) return animation;
        // Each finished render tells MediaCache exactly which animations were
        // actually drawn. Off-screen GIFs therefore stop scheduling work
        // immediately instead of lingering on a time-based activity window.
        _media.BeginRenderPass();
        width = Math.Clamp(width, HudLayoutPreset.MinimumWidth, HudLayoutPreset.MaximumWidth);
        height = Math.Clamp(height, HudLayoutPreset.MinimumHeight, HudLayoutPreset.MaximumHeight);
        fontScalePercent = Math.Clamp(fontScalePercent, 30, 200);
        mediaScalePercent = Math.Clamp(mediaScalePercent, 30, 100);
        emojiScalePercent = Math.Clamp(emojiScalePercent, 30, 200);
        // Configuration stores human-readable percentages. GDI+ expects an
        // 8-bit alpha value, so map the full slider range to 0..255. The old
        // direct use of 0..100 made "100%" only about 39% opaque.
        backgroundTintAlpha = PercentToAlpha(backgroundTintAlpha);
        nicknameBadgeAlpha = PercentToAlpha(nicknameBadgeAlpha);
        saleTintAlpha = PercentToAlpha(saleTintAlpha);
        staffAssignmentTintAlpha = PercentToAlpha(staffAssignmentTintAlpha);
        var tintColor = darkTint ? Color.Black : Color.White;
        var fonts = GetRenderFonts(fontScalePercent, emojiScalePercent);
        var bitmap = RentRenderBitmap(width, height);
        BeginAnimationScene(bitmap);
        var returnBitmapOnFailure = true;
        try
        {
        using var graphics = Graphics.FromImage(bitmap);
        ConfigureGraphics(graphics);
        graphics.Clear(Color.Transparent);
        var saleTurnMessageId = showSaleStatus ? saleStatus?.CurrentMessageId : null;
        var drawSaleStatus = showSaleStatus && saleStatus is not null;
        var saleStatusReferenceHeight = saleStatus is null
            ? SaleStatusMinHeight
            : GetSaleStatusHeight(graphics, fonts, saleStatus);
        var saleStatusHeight = drawSaleStatus ? saleStatusReferenceHeight : 0;
        var businessDisplay = showBusinessSupplies
            ? BuildBusinessHudDisplay(
                businessSupplies,
                businessHudTargetKey,
                businessDisplayMode,
                remoteStaffTimers,
                businessTrackingOnline,
                DateTimeOffset.UtcNow, expandedKeys, valueKeys, businessTargetCount)
            : null;
        var vinewoodPopup = showVinewoodTimerPopup
            ? BuildVinewoodPopupDisplay(remoteStaffTimers, DateTimeOffset.UtcNow)
            : null;
        if (vinewoodPopup is not null)
            vinewoodPopup = WrapVinewoodPopupDisplay(graphics, fonts, vinewoodPopup, width);
        var vinewoodPopupIsIdle = IsIdleVinewoodPopup(vinewoodPopup);
        var vinewoodPopupHeight = vinewoodPopup is null
            ? 0
            : vinewoodPopupIsIdle
                ? Math.Max(24, 8 + (int)Math.Ceiling(fonts.StaffAssignment.GetHeight(graphics)))
                : Math.Max(
                    24,
                    8 + vinewoodPopup.Rows.Count * Math.Max(
                        StaffAssignmentIconSize,
                        (int)Math.Ceiling(fonts.StaffAssignment.GetHeight(graphics)))
                    + Math.Max(0, vinewoodPopup.Rows.Count - 1) * 2);
        var mainTintTop = vinewoodPopup is null
            ? 1
            : VinewoodPopupTop + vinewoodPopupHeight + 5;
        var channelHeaderY = mainTintTop;
        var hasExternalHeader = !string.IsNullOrWhiteSpace(channelLabel) || !string.IsNullOrWhiteSpace(sessionLabel) || clockInHeader;
        if (hasExternalHeader) mainTintTop += 30; // Header bottom touches tint top.

        var clockHeight = (int)Math.Ceiling(fonts.Clock.GetHeight(graphics)) + 10;
        var businessSupplyLines = businessDisplay?.Lines ?? [];
        var businessSupplyHeight = businessSupplyLines.Count == 0
            ? 0
            : GetBusinessSupplyHeight(graphics, fonts, businessSupplyLines.Count);
        // Equal top/right insets relative to the main HUD, including when the
        // staff assignment strip shifts its top edge down.
        var businessSupplyY = mainTintTop + PaddingRight;
        var footerHeight = (drawSaleStatus ? SaleStatusGap + saleStatusHeight : 0)
                         + (drawSaleStatus ? SaleStatusCanvasBottomGap : 0);
        var tintBottom = height - footerHeight - Math.Max(0, liveMessageReservedHeight);
        var contentBottom = clockInHeader
            ? tintBottom - PaddingY
            : tintBottom - clockHeight;
        // The clock remains inside the main HUD glass. Only the sale queue is
        // placed on its own glass surface below that panel.
        DrawHudTint(
            graphics,
            width,
            height,
            mainTintTop,
            tintBottom,
            backgroundTintAlpha,
            tintColor);

        var availableWidth = Math.Max(80, width - PaddingLeft - PaddingRight);
        PrepareMessageLayoutCache(availableWidth, fontScalePercent, mediaScalePercent, emojiScalePercent);
        var continuationFlags = new bool[messages.Count];
        for (var i = 1; i < messages.Count; i++)
            continuationFlags[i] = messages[i].Id != saleTurnMessageId
                                   && IsContinuation(messages[i - 1], messages[i]);

        var layouts = messages
            .Select((message, index) => GetMessageLayout(
                graphics,
                fonts,
                message,
                availableWidth,
                mediaScalePercent,
                continuationFlags[index]))
            .ToArray();
        var gaps = new int[Math.Max(0, layouts.Length - 1)];
        for (var i = 0; i < gaps.Length; i++)
        {
            // Reply and forwarded content belong to the same message group as
            // their author/content, but they must never shrink the spacing to
            // the next group. Only a true same-author continuation is tight.
            gaps[i] = continuationFlags[i + 1] ? SameAuthorGap : nicknameGroupGap;
        }
        var totalHeight = layouts.Sum(x => x.Height) + gaps.Sum();
        var topClip = PaddingY + TopReservedY;
        if (businessSupplyHeight > 0)
            topClip = Math.Max(topClip, businessSupplyY + businessSupplyHeight + SaleStatusGap);
        if (vinewoodPopup is not null)
            topClip = Math.Max(topClip, mainTintTop + SaleStatusGap);
        topClip = Math.Max(topClip, mainTintTop + SaleStatusGap);
        // A header clock needs no bottom reservation. A bottom clock reserves
        // its own row inside the main tint, while clipping keeps chat content
        // above it.
        var availableHeight = Math.Max(1, contentBottom - topClip);
        var contentHeightWithBottomGap = totalHeight + LatestMessageBottomGap;
        var maxScroll = Math.Max(0, contentHeightWithBottomGap - availableHeight);
        var scroll = Math.Clamp(requestedScrollPixels, 0, maxScroll);
        var y = contentHeightWithBottomGap <= availableHeight && scroll == 0
            ? topClip
            : contentBottom - totalHeight - LatestMessageBottomGap + scroll;

        // Old messages can start above the content area when the history is
        // taller than the HUD. Clip the whole message layer so it never paints
        // over the channel header or the bottom clock.
        var messageY = new int[layouts.Length];
        var layoutY = y;
        for (var i = 0; i < layouts.Length; i++)
        {
            messageY[i] = layoutY;
            layoutY += layouts[i].Height + (i == layouts.Length - 1 ? 0 : gaps[i]);
        }

        var liveInnerWidth = Math.Max(20, availableWidth - LiveMessagePaddingX * 2);
        var liveMessageLayout = liveMessage is null
            ? null
            : GetMessageLayout(
                graphics,
                fonts,
                liveMessage,
                liveInnerWidth,
                mediaScalePercent,
                isContinuation: false);
        // All history and live-card layouts have now been visited. Release
        // discarded messages immediately rather than retaining old text and
        // token graphs until the cache's emergency size limit is reached.
        foreach (var cached in _messageLayoutCache)
            if (cached.Value.LastUsedPass != _layoutRenderPass)
                _messageLayoutCache.Remove(cached.Key);

        var contentState = graphics.Save();
        var liveMessageRectangle = liveMessageLayout is null || liveMessageReservedHeight <= SaleStatusGap
            ? RectangleF.Empty
            : new RectangleF(PaddingLeft, tintBottom + SaleStatusGap,
                availableWidth, liveMessageReservedHeight - SaleStatusGap);
        // The live card occupies a separate strip below the history glass.
        // The sale HUD moves down by that strip's height, without moving history.
        var messageClipBottom = contentBottom;
        var messageClipHeight = Math.Max(1, messageClipBottom - topClip);
        graphics.SetClip(new Rectangle(0, topClip, width, messageClipHeight), CombineMode.Intersect);

        var sceneArea = Rectangle.Intersect(new Rectangle(0, topClip, width, messageClipHeight), new Rectangle(0, 0, width, height));
        var visibleText = layouts.Where((layout, index) => messageY[index] + layout.Height > topClip
            && messageY[index] < messageClipBottom).Select(layout => layout.Message).ToArray();
        var iconSources = GetTextSceneSources(visibleText);
        var cacheText = sceneArea.Width > 0 && sceneArea.Height > 0
            && (long)sceneArea.Width * sceneArea.Height * 4 <= 16L * 1024 * 1024 && iconSources is not null;
        var sceneKey = cacheText ? new TextSceneKey(messages, fonts, sceneArea, height, scroll,
            backgroundTintAlpha, nicknameBadgeAlpha, nicknameGroupGap, tintColor.ToArgb(), saleTurnMessageId, iconSources!) : null;
        if (sceneKey is not null && sceneKey == _textSceneKey && _textScene is not null)
        {
            var mode = graphics.CompositingMode;
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(_textScene, sceneArea.Location);
            graphics.CompositingMode = mode;
        }
        else
        {
            _textScene?.Dispose(); _textScene = null; _textSceneKey = null;
        for (var i = 0; i < layouts.Length; i++)
        {
            var layout = layouts[i];
            if (messageY[i] + layout.Height > topClip && messageY[i] < height)
                DrawMessage(
                    graphics,
                    fonts,
                    layout,
                    PaddingLeft,
                    messageY[i],
                    availableWidth,
                    nicknameBadgeAlpha,
                    tintColor,
                    layout.Message.Id == saleTurnMessageId);
        }

            if (sceneKey is not null)
            {
                graphics.Flush(FlushIntention.Sync);
                _textScene = bitmap.Clone(sceneArea, PixelFormat.Format32bppPArgb);
                _textSceneKey = sceneKey;
            }
        }

        if (messages.Count == 0 && !string.IsNullOrWhiteSpace(status))
        {
            using var statusBrush = new SolidBrush(Color.FromArgb(180, 225, 228, 234));
            DrawSoftText(graphics, status, fonts.Body, statusBrush, PaddingLeft, topClip + 4);
        }
        graphics.Restore(contentState);

        if (liveMessageLayout is not null && !liveMessageRectangle.IsEmpty)
            DrawLiveMessage(
                graphics,
                fonts,
                liveMessageLayout,
                liveMessageRectangle,
                nicknameBadgeAlpha,
                tintColor,
                backgroundTintAlpha);

        DrawChannelHeader(graphics, fonts, channelLabel, width, tintColor, clockInHeader, clockOnLeft, channelHeaderY, sessionLabel);
        if (businessSupplyHeight > 0)
            DrawBusinessSupplies(
                graphics,
                fonts,
                businessSupplyLines,
                width,
                businessSupplyY,
                businessSupplyHeight,
                tintColor,
                businessDisplay!.IsOnline,
                businessDisplay.IconKey,
                businessDisplayMode,
                channelLabel,
                clockInHeader,
                clockOnLeft, businessDisplay.LineIconKeys, businessIconOnly);
        if (vinewoodPopup is not null)
            DrawVinewoodTimerPopup(
                graphics,
                fonts,
                vinewoodPopup,
                width,
                vinewoodPopupHeight,
                staffAssignmentTintAlpha);
        var footerY = tintBottom + Math.Max(0, liveMessageReservedHeight);
        if (drawSaleStatus)
        {
            footerY += SaleStatusGap;
            DrawSaleStatus(
                graphics,
                fonts,
                saleStatus!,
                width,
                footerY,
                saleStatusHeight,
                tintColor,
                saleTintAlpha);
            footerY += saleStatusHeight;
        }
        if (!clockInHeader) DrawBottomClock(graphics, fonts, width, tintBottom, clockOnLeft);
        graphics.Flush(FlushIntention.Sync);
        FinishAnimationScene(bitmap, maxScroll, mainTintTop, tintBottom);
        var rendered = new RenderedHud(bitmap, maxScroll, mainTintTop, tintBottom, _returnRenderBitmap);
        rendered.ChatMessages = layouts.Where((layout,i)=>messageY[i]+layout.Height>sceneArea.Top && messageY[i]<sceneArea.Bottom && sceneArea.Height>0)
            .Select(layout=>(layout.Message.ChannelId,layout.Message.Id)).TakeLast(64).ToArray();
        if(liveMessage is not null && !liveMessageRectangle.IsEmpty)
            rendered.ChatMessages=rendered.ChatMessages.Append((liveMessage.ChannelId,liveMessage.Id)).ToArray();
        returnBitmapOnFailure = false;
        return rendered;
        }
        finally
        {
            if (returnBitmapOnFailure)
            {
                ClearAnimationScene();
                ReturnRenderBitmap(bitmap);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearAnimationScene();
        _staffHangarIcon?.Dispose();
        _staffWarehouseIcon?.Dispose();
        _businessBunkerIcon?.Dispose();
        _businessAcidLabIcon?.Dispose();
        foreach(var icon in _extraBusinessIcons.Values)icon.Dispose();
        _textScene?.Dispose(); _textScene = null; _textSceneKey = null;
        _textCache.Dispose();
        _cachedFonts?.Dispose();
        _cachedFonts = null;
        _messageLayoutCache.Clear();
        lock (_bitmapPoolGate)
        {
            _pooledBitmap?.Dispose();
            _pooledBitmap = null;
        }
        if (!ReferenceEquals(_emojiFamily, _bodyFamily)) _emojiFamily.Dispose();
        _bodyFamily.Dispose();
        _privateFonts.Dispose();
    }

    [GeneratedRegex(@"<(a)?:([^:>]+):(\d+)>", RegexOptions.Compiled)]
    private static partial Regex CustomEmojiRegex();
}

internal sealed class RenderedHud(
    Bitmap bitmap,
    int maxScrollPixels,
    int mainTintTop,
    int mainTintBottom,
    Action<Bitmap> release,
    Rectangle? dirtyBounds = null) : IDisposable
{
    private Action<Bitmap>? _release = release;
    public Bitmap Bitmap { get; } = bitmap;
    internal (ulong Channel,ulong Id)[] ChatMessages { get; set; } = [];
    // null = complete scene; empty = identical frame; otherwise changed pixels.
    public Rectangle? DirtyBounds { get; } = dirtyBounds;
    public int MaxScrollPixels { get; } = maxScrollPixels;
    public int MainTintTop { get; } = mainTintTop;
    public int MainTintBottom { get; } = mainTintBottom;

    public void Dispose()
    {
        var returnToPool = Interlocked.Exchange(ref _release, null);
        returnToPool?.Invoke(Bitmap);
    }
}

