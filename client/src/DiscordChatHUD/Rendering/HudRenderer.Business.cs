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

// HudRenderer — 사업장 HUD, 나이트클럽, 바인우드 타이머 팝업, 직원 배정 표시.
internal sealed partial class HudRenderer
{

    private static BusinessHudDisplay? BuildBusinessHudDisplay(
        IReadOnlyList<BusinessSupplyEntry> businessSupplies,
        string targetKey,
        string displayMode,
        IReadOnlyList<RemoteStaffTimerEntry> remoteStaffTimers,
        bool businessTrackingOnline,
        DateTimeOffset now,
        IReadOnlyList<string>? expandedKeys = null, IReadOnlyList<string>? valueKeys = null, int businessTargetCount = 1)
    {
        if (businessTargetCount == 2 || string.Equals(displayMode, "bunker_nightclub", StringComparison.OrdinalIgnoreCase))
        {
            var displays = (expandedKeys ?? ["bunker", "nightclub"]).Distinct().Take(2)
                .Select(key =>
                {
                    var mode = displayMode == "bunker_nightclub" ? "default" : displayMode;
                    var display = BuildBusinessHudDisplay(businessSupplies, key, mode, remoteStaffTimers, businessTrackingOnline, now, null, valueKeys);
                    return display;
                })
                .Where(display => display is not null).ToArray();
            return new BusinessHudDisplay(displays.SelectMany(display => display!.Lines).ToList(), displays.Any(display => display!.IsOnline), null,
                displays.SelectMany(display => display!.Lines.Select((_,i)=>i==0?display.IconKey:null)).ToList());
        }
        switch ((targetKey ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "nightclub":
                return BuildNightclubHudDisplay(businessSupplies, displayMode, businessTrackingOnline, valueKeys?.Contains("nightclub") == true);
            case "vinewood":
                return BuildVinewoodHudDisplay(remoteStaffTimers, displayMode, now);
            default:
            {
                var selected = businessSupplies.FirstOrDefault(entry =>
                    string.Equals(entry.Key, targetKey, StringComparison.OrdinalIgnoreCase));
                if (selected is null) return null;
                var effective = selected.Clone();
                var progress = BusinessSupplyCalculator.Calculate(effective, now, businessTrackingOnline);
                var iconKey = effective.Key;
                return new BusinessHudDisplay(
                    BuildBusinessSupplyLines(progress, effective, displayMode, now, false, valueKeys?.Contains(targetKey) == true),
                    progress.IsOnline,
                    iconKey);
            }
        }
    }

    internal static string BuildTimedHudSignature(
        IReadOnlyList<BusinessSupplyEntry> businessSupplies,
        string targetKey,
        string displayMode,
        IReadOnlyList<RemoteStaffTimerEntry> remoteStaffTimers,
        bool businessTrackingOnline,
        bool showBusinessHud,
        bool showStaffAssignmentHud,
        DateTimeOffset now,
        IReadOnlyList<string>? expandedKeys = null, IReadOnlyList<string>? valueKeys = null, int businessTargetCount = 1)
    {
        var parts = new List<string>();
        if (showBusinessHud)
        {
            var business = BuildBusinessHudDisplay(
                businessSupplies,
                targetKey,
                displayMode,
                remoteStaffTimers,
                businessTrackingOnline,
                now, expandedKeys, valueKeys, businessTargetCount);
            if (business is not null)
            {
                parts.Add(business.IsOnline ? "business:online" : "business:offline");
                parts.Add(business.IconKey ?? string.Empty);
                parts.AddRange(business.Lines);
            }
        }

        if (showStaffAssignmentHud)
        {
            var popup = BuildVinewoodPopupDisplay(remoteStaffTimers, now);
            foreach (var row in popup.Rows)
            foreach (var item in row)
                parts.Add($"staff:{item.Key}:{item.TimeText}:{item.IsPaused}");
        }

        return string.Join("\u001f", parts);
    }

    private static List<string> BuildBusinessSupplyLines(
        BusinessSupplyProgress entry,
        BusinessSupplyEntry source,
        string displayMode,
        DateTimeOffset now,
        bool useBusinessIcon, bool showValue = false)
    {
        var profile = BusinessSupplyCatalog.Find(entry.Key, entry.Name);
        var value = showValue ? $" · ${BusinessSupplyCalculator.StockUnitsToValue(entry.StockUnits, profile, source.UpgradeTier):N0}" : string.Empty;
        var stock = $"재고 {entry.StockPercent}%{value}";
        var supply = entry.SupplyPercent is { } percent
            ? $"보급 {percent}%"
            : string.Empty;
        var deliveryRemaining = BusinessSupplyCalculator.SupplyDeliveryRemaining(source, now);
        var remaining = deliveryRemaining is { } delivery && delivery > TimeSpan.Zero
            ? FormatRemoteStaffDuration(delivery)
            : !entry.IsOnline
            ? "계산 정지"
            : entry.StockUnits >= entry.StockCapacity - 0.0001d
                ? "재고 가득 참"
                : entry.SupplyUnits is <= 0.0001d
                    ? "보급 소진"
                    : entry.UntilSupplyEmpty is { } timeRemaining
                        ? FormatBusinessDuration(timeRemaining)
                        : "생산 대기";
        // Mansion (3x) and the Acid Lab daily boost (2x) stack multiplicatively.
        var boost = (entry.MansionBoostActive, entry.DailyBoostActive) switch
        {
            (true, true) => $" · 6× 부스트 {(int)Math.Ceiling(entry.DailyBoostUnitsRemaining)}개",
            (false, true) => $" · 2× 부스트 {(int)Math.Ceiling(entry.DailyBoostUnitsRemaining)}개",
            (true, false) => " · 3×",
            _ => string.Empty
        };
        var stockFullTimer = source.ShowStockFullTimer && entry.UntilStockFull is { } fullRemaining
            ? $" · 가득 {FormatBusinessDuration(fullRemaining)}"
            : string.Empty;
        return (displayMode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "compact" => [useBusinessIcon
                ? $"{entry.StockPercent}%{value} / {(entry.SupplyPercent is { } compactSupply ? $"{compactSupply}%" : "—")}{stockFullTimer}"
                : $"{entry.Name} · {entry.StockPercent}%{value} / {(entry.SupplyPercent is { } otherSupply ? $"{otherSupply}%" : "—")}{stockFullTimer}"],
            "expanded" or "pair" =>
            [
                $"{entry.Name} · {stock}",
                $"{(string.IsNullOrEmpty(supply) ? "보급 없음" : supply)} · {remaining}{boost}{stockFullTimer}"
            ],
            _ =>
            [
                useBusinessIcon
                    ? $"재고 {entry.StockPercent}%{value} · {(string.IsNullOrEmpty(supply) ? "보급 없음" : supply)} · {remaining}{stockFullTimer}"
                    : $"{entry.Name} · 재고 {entry.StockPercent}%{value} · {(string.IsNullOrEmpty(supply) ? "보급 없음" : supply)} · {remaining}{stockFullTimer}"
            ]
        };
    }

    private static BusinessHudDisplay BuildNightclubHudDisplay(
        IReadOnlyList<BusinessSupplyEntry> businessSupplies,
        string displayMode,
        bool businessTrackingOnline, bool showValue = false)
    {
        var entries = businessSupplies
            .Where(entry => BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile)
                            && profile.Category == BusinessCategory.Nightclub)
            .Select(entry => BusinessSupplyCalculator.Calculate(entry))
            .ToArray();
        var stock = (int)Math.Round(entries.Sum(entry => entry.StockUnits));
        var capacity = entries.Sum(entry => entry.StockCapacity);
        var percent = capacity <= 0
            ? 0
            : (int)Math.Round(stock * 100d / capacity);
        var technicians = entries.Count(entry => entry.IsOnline);
        var value = showValue ? $" · ${entries.Sum(entry => NightclubUnitValue(entry.Key) * Math.Floor(entry.StockUnits + 1e-9)):N0}" : string.Empty;
        var lines = (displayMode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "compact" => new List<string> { $"나이트클럽 · 재고 {percent}%{value}" },
            "expanded" or "pair" =>
            [
                $"나이트클럽 · 재고 {percent}%{value}",
                $"재고 {stock}/{capacity} · 기술자 {technicians}/5"
            ],
            _ => new List<string> { $"나이트클럽 · 재고 {percent}%{value} · 기술자 {technicians}/5" }
        };
        return new BusinessHudDisplay(lines, businessTrackingOnline && technicians > 0, "nightclub");
    }

    // Warehouse gross value, before Tony's fee and sale/event bonuses.
    internal static int NightclubUnitValue(string key) => key switch
    {
        "nightclub_cargo" => 10000, "nightclub_sporting" => 5000,
        "nightclub_south_american" => 27000, "nightclub_pharmaceutical" => 11475,
        "nightclub_cash" => 4725, "nightclub_organic" => 2025, "nightclub_printing" => 1350,
        _ => 0
    };

    private static BusinessHudDisplay BuildVinewoodHudDisplay(
        IReadOnlyList<RemoteStaffTimerEntry> remoteStaffTimers,
        string displayMode,
        DateTimeOffset now)
    {
        var timers = remoteStaffTimers
            .Select(timer =>
            {
                var stored = TimeSpan.FromSeconds(Math.Max(0d, timer.RemainingSeconds));
                var remaining = !timer.HasStarted
                    ? (TimeSpan?)null
                    : timer.StartedAtUtc is { } started
                        ? stored - (now - started)
                        : stored;
                var running = timer.StartedAtUtc is not null && remaining is { } value && value > TimeSpan.Zero;
                var paused = timer.StartedAtUtc is null && remaining is { } pausedValue && pausedValue > TimeSpan.Zero;
                return (Timer: timer, Remaining: remaining, Running: running, Paused: paused);
            })
            .ToArray();
        var active = timers
            .Where(timer => timer.Running)
            .OrderBy(timer => timer.Remaining)
            .ToArray();
        var paused = timers
            .Where(timer => timer.Paused)
            .OrderBy(timer => timer.Remaining)
            .ToArray();
        var completed = timers.Count(timer => timer.Timer.HasStarted
                                              && timer.Remaining.HasValue
                                              && timer.Remaining.Value <= TimeSpan.Zero);
        var next = active.Length > 0
            ? $"{active[0].Timer.Name} {FormatRemoteStaffDuration(active[0].Remaining!.Value)}"
            : paused.Length > 0
                ? $"정지 {paused[0].Timer.Name} {FormatRemoteStaffDuration(paused[0].Remaining!.Value)}"
                : "대기 중";
        var lines = (displayMode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "compact" => new List<string> { $"직원 배정 · {next}" },
            "expanded" =>
            [
                "직원 배정",
                $"진행 {active.Length}/6 · 정지 {paused.Length}/6 · 완료 {completed}/6 · {next}"
            ],
            _ => new List<string> { $"직원 배정 · 진행 {active.Length}/6 · 정지 {paused.Length}/6 · {next}" }
        };
        return new BusinessHudDisplay(lines, active.Length > 0, null);
    }

    private static VinewoodPopupDisplay BuildVinewoodPopupDisplay(
        IReadOnlyList<RemoteStaffTimerEntry> remoteStaffTimers,
        DateTimeOffset now)
    {
        var timers = remoteStaffTimers
            .Select(timer =>
            {
                var stored = TimeSpan.FromSeconds(Math.Max(0d, timer.RemainingSeconds));
                var remaining = !timer.HasStarted
                    ? (TimeSpan?)null
                    : timer.StartedAtUtc is { } started
                        ? stored - (now - started)
                        : stored;
                var running = timer.StartedAtUtc is not null && remaining is { } value && value > TimeSpan.Zero;
                var paused = timer.StartedAtUtc is null && remaining is { } pausedValue && pausedValue > TimeSpan.Zero;
                return (Timer: timer, Remaining: remaining, Running: running, Paused: paused);
            })
            .ToArray();
        // Keep the six assignments in their fixed Config order so the icons
        // never jump around as their remaining times change.
        var visible = timers
            .Where(timer => timer.Running || timer.Paused)
            .Select(timer => new StaffAssignmentPopupItem(
                timer.Timer.Key,
                FormatRemoteStaffMinutes(timer.Remaining!.Value),
                timer.Paused))
            .ToArray();
        if (visible.Length == 0)
            return new VinewoodPopupDisplay([[new StaffAssignmentPopupItem("idle", "대기")]]);
        return new VinewoodPopupDisplay([visible]);
    }

    private VinewoodPopupDisplay WrapVinewoodPopupDisplay(
        Graphics graphics,
        RenderFonts fonts,
        VinewoodPopupDisplay display,
        int width)
    {
        var availableWidth = Math.Max(
            1,
            width - PaddingLeft - PaddingRight - VinewoodPopupPaddingX * 2);
        var wrappedRows = new List<IReadOnlyList<StaffAssignmentPopupItem>>();
        foreach (var sourceRow in display.Rows)
        {
            var row = new List<StaffAssignmentPopupItem>();
            var rowWidth = 0;
            foreach (var item in sourceRow)
            {
                var itemWidth = MeasureStaffAssignmentItem(graphics, fonts, item);
                if (row.Count > 0 && rowWidth + StaffAssignmentItemGap + itemWidth > availableWidth)
                {
                    wrappedRows.Add(row.ToArray());
                    row = new List<StaffAssignmentPopupItem>();
                    rowWidth = 0;
                }

                if (row.Count > 0) rowWidth += StaffAssignmentItemGap;
                row.Add(item);
                rowWidth += itemWidth;
            }
            if (row.Count > 0) wrappedRows.Add(row.ToArray());
        }
        return new VinewoodPopupDisplay(wrappedRows);
    }

    private static bool IsIdleVinewoodPopup(VinewoodPopupDisplay? display)
        => display is { Rows.Count: 1 }
           && display.Rows[0].Count == 1
           && string.Equals(display.Rows[0][0].Key, "idle", StringComparison.OrdinalIgnoreCase);

    private void DrawVinewoodTimerPopup(
        Graphics graphics,
        RenderFonts fonts,
        VinewoodPopupDisplay display,
        int width,
        int height,
        int staffAssignmentTintAlpha)
    {
        // Active assignments keep the sale HUD width. The idle placeholder is
        // a compact content-sized chip so adding sale rows cannot enlarge it.
        var fullPopupWidth = Math.Max(120, width - PaddingLeft - PaddingRight);
        var idle = IsIdleVinewoodPopup(display);
        var popupWidth = idle
            ? Math.Min(
                fullPopupWidth,
                Math.Max(
                    72,
                    MeasureStaffAssignmentRow(graphics, fonts, display.Rows[0])
                    + VinewoodPopupPaddingX * 2))
            : fullPopupWidth;
        var popupLeft = idle
            ? width - PaddingRight - popupWidth
            : PaddingLeft;
        var rectangle = new RectangleF(popupLeft, VinewoodPopupTop, popupWidth, height);
        using var path = RoundedRectangle(rectangle, 18);
        using var fill = new SolidBrush(Color.FromArgb(staffAssignmentTintAlpha, 12, 13, 16));
        graphics.FillPath(fill, path);
        using var textBrush = new SolidBrush(Color.FromArgb(245, 245, 247));
        var lineHeight = (int)Math.Ceiling(fonts.StaffAssignment.GetHeight(graphics));
        for (var rowIndex = 0; rowIndex < display.Rows.Count; rowIndex++)
        {
            var row = display.Rows[rowIndex];
            var rowIconHeight = row.Count == 0
                ? 0
                : row.Max(GetStaffAssignmentIconSize);
            var rowHeight = Math.Max(lineHeight, rowIconHeight);
            var rowWidth = MeasureStaffAssignmentRow(graphics, fonts, row);
            var cursorX = rectangle.Right - VinewoodPopupPaddingX - rowWidth;
            var contentHeight = display.Rows.Count * rowHeight + Math.Max(0, display.Rows.Count - 1) * 2;
            var rowTop = rectangle.Top
                         + Math.Max(0, (rectangle.Height - contentHeight) / 2f)
                         + rowIndex * (rowHeight + 2);
            foreach (var item in row)
            {
                var iconSize = GetStaffAssignmentIconSize(item);
                var iconBounds = new RectangleF(
                    cursorX,
                    rowTop + (rowHeight - iconSize) / 2f,
                    iconSize,
                    iconSize);
                DrawStaffAssignmentIcon(graphics, fonts, item.Key, iconBounds);
                if (item.IsPaused)
                {
                    using var pause = new SolidBrush(Color.FromArgb(250, 250, 82, 114));
                    graphics.FillRectangle(pause, iconBounds.Right - 6, iconBounds.Bottom - 6, 2, 5);
                    graphics.FillRectangle(pause, iconBounds.Right - 3, iconBounds.Bottom - 6, 2, 5);
                }
                cursorX += iconSize + StaffAssignmentIconTextGap;
                DrawSoftText(
                    graphics,
                    item.TimeText,
                    fonts.StaffAssignment,
                    textBrush,
                    cursorX,
                    rowTop + (rowHeight - lineHeight) / 2f);
                cursorX += MeasureText(graphics, item.TimeText, fonts.StaffAssignment) + StaffAssignmentItemGap;
            }
        }
    }

    private int MeasureStaffAssignmentRow(
        Graphics graphics,
        RenderFonts fonts,
        IReadOnlyList<StaffAssignmentPopupItem> row)
    {
        var measured = row.Sum(item =>
            MeasureStaffAssignmentItem(graphics, fonts, item)
            + StaffAssignmentItemGap);
        return Math.Max(1, measured - StaffAssignmentItemGap);
    }

    private int MeasureStaffAssignmentItem(
        Graphics graphics,
        RenderFonts fonts,
        StaffAssignmentPopupItem item)
        => GetStaffAssignmentIconSize(item)
           + StaffAssignmentIconTextGap
           + MeasureText(graphics, item.TimeText, fonts.StaffAssignment);

    private static int GetStaffAssignmentIconSize(StaffAssignmentPopupItem item)
        => string.Equals(item.Key, "idle", StringComparison.OrdinalIgnoreCase)
            ? StaffAssignmentIdleDotSize
            : StaffAssignmentIconSize;

    private void DrawStaffAssignmentIcon(
        Graphics graphics,
        RenderFonts fonts,
        string key,
        RectangleF bounds)
    {
        if (string.Equals(key, "idle", StringComparison.OrdinalIgnoreCase))
        {
            using var idleBrush = new SolidBrush(Color.FromArgb(230, 235, 237, 242));
            graphics.FillEllipse(idleBrush, bounds);
            return;
        }

        var warehouseNumber = key.StartsWith("large_warehouse_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(key["large_warehouse_".Length..], out var number)
                ? Math.Clamp(number, 1, 5)
                : 0;
        var icon = string.Equals(key, "hangar", StringComparison.OrdinalIgnoreCase)
            ? _staffHangarIcon
            : warehouseNumber > 0
                ? _staffWarehouseIcon
                : null;
        if (icon is not null)
        {
            graphics.DrawImage(icon, ContainRectangle(icon.Size, bounds));
        }
        else
        {
            using var fallback = new SolidBrush(Color.FromArgb(238, 245, 245, 247));
            graphics.FillEllipse(fallback, bounds.Left + 4, bounds.Top + 4, bounds.Width - 8, bounds.Height - 8);
        }

        if (warehouseNumber > 0)
        {
            var label = warehouseNumber.ToString(CultureInfo.InvariantCulture);
            var labelWidth = MeasureText(graphics, label, fonts.StaffAssignmentNumber);
            var labelHeight = (int)Math.Ceiling(fonts.StaffAssignmentNumber.GetHeight(graphics));
            var numberCircle = new RectangleF(
                bounds.Left + (bounds.Width - StaffAssignmentNumberCircleSize) / 2f,
                bounds.Bottom - StaffAssignmentNumberCircleSize,
                StaffAssignmentNumberCircleSize,
                StaffAssignmentNumberCircleSize);
            using var numberCircleBrush = new SolidBrush(Color.FromArgb(238, 48, 50, 55));
            graphics.FillEllipse(numberCircleBrush, numberCircle);
            using var numberBrush = new SolidBrush(Color.FromArgb(245, 232, 234, 238));
            DrawSoftText(
                graphics,
                label,
                fonts.StaffAssignmentNumber,
                numberBrush,
                bounds.Left + (bounds.Width - labelWidth) / 2f,
                bounds.Bottom - labelHeight);
        }
    }

    private static string FormatRemoteStaffMinutes(TimeSpan value)
        => $"{Math.Max(0, (int)Math.Ceiling(value.TotalMinutes))}m";

    private static string FormatRemoteStaffDuration(TimeSpan value)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(value.TotalSeconds));
        return $"{seconds / 60:D2}:{seconds % 60:D2}";
    }

    private static string FormatBusinessDuration(TimeSpan value)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(value.TotalSeconds));
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:D2}:{seconds % 60:D2}"
            : $"{seconds / 60}:{seconds % 60:D2}";
    }

    private static int GetBusinessSupplyHeight(Graphics graphics, RenderFonts fonts, int lineCount)
    {
        var lineHeight = (int)Math.Ceiling(fonts.SaleStatus.GetHeight(graphics));
        return BusinessHudPaddingY * 2
               + Math.Max(Math.Min(fonts.BusinessIconSize, lineHeight),
                   lineCount * lineHeight + Math.Max(0, lineCount - 1) * SaleStatusLineGap);
    }

    private void DrawBusinessSupplies(
        Graphics graphics,
        RenderFonts fonts,
        IReadOnlyList<string> lines,
        int width,
        int y,
        int height,
        Color tintColor,
        bool isOnline,
        string? iconKey,
        string displayMode,
        string channelLabel,
        bool clockInHeader,
        bool clockOnLeft, IReadOnlyList<string?>? lineIconKeys = null, bool iconOnly = false)
    {
        var cardRight = width - PaddingRight;
        // Keep the existing right-aligned content placement. The glass and its
        // outline span the main panel with equal left/right insets.
        var cardLeftLimit = PaddingLeft;
        var availableWidth = Math.Max(1, cardRight - cardLeftLimit);
        string? Key(int index) => lineIconKeys is not null && index<lineIconKeys.Count ? lineIconKeys[index] : index==0 ? iconKey : null;
        Image? Icon(string? key) => key switch { "bunker"=>_businessBunkerIcon,"acid_lab"=>_businessAcidLabIcon,_=>key is not null?_extraBusinessIcons.GetValueOrDefault(key):null };
        if(iconOnly)lines=lines.Select((line,index)=>
        {
            var key=Key(index);
            if(Icon(key) is null)return line;
            var separator=line.IndexOf(" · ",StringComparison.Ordinal);
            return separator>=0?line[(separator+3)..]:line;
        }).ToArray();
        var textWidth = lines.Count == 0
            ? 0
            : lines.Select((line, index) => MeasureText(
                    graphics,
                    line,
                    index == 0 ? fonts.SaleStatusBold : fonts.SaleStatus))
                .Max();
        var rectangleWidth = Math.Min(availableWidth, textWidth + SaleStatusPadding * 2 + TextWrapSafety);
        var contentLeft = cardRight - rectangleWidth;
        var rectangle = new RectangleF(cardLeftLimit, y, availableWidth, height);

        // At narrow widths, contain all card contents within the space left
        // after the channel header reservation.
        var cardState = graphics.Save();
        graphics.SetClip(rectangle, CombineMode.Intersect);
        try
        {

            using var path = RoundedRectangle(rectangle, 9);
            using var fill = new SolidBrush(Color.FromArgb(PercentToAlpha(_businessTintAlpha), tintColor.R, tintColor.G, tintColor.B));
            graphics.FillPath(fill, path);
            using var brush = new SolidBrush(Color.FromArgb(238, 245, 247, 250));
            if (isOnline)
            {
                // Inset stroke leaves the outer card bounds/margins unchanged.
                var borderBounds = RectangleF.Inflate(rectangle, -1f, -1f);
                if (borderBounds.Width > 0 && borderBounds.Height > 0)
                {
                    using var borderPath = RoundedRectangle(borderBounds, 8);
                    using var border = new Pen(BusinessActiveBorder, 1.5f);
                    graphics.DrawPath(border, borderPath);
                }
            }
            var lineHeight = (int)Math.Ceiling(fonts.SaleStatus.GetHeight(graphics));
            var iconSize = Math.Min(fonts.BusinessIconSize, lineHeight);
            var sharedTextX = Math.Max(contentLeft + SaleStatusPadding, rectangle.Left + SaleStatusPadding + (iconOnly ? iconSize + 6 : 0));
            // A dual-business display carries per-line keys. Center each line independently.
            // Keep the glass bounds symmetric as before.
            var independentLines = lineIconKeys is not null;
            for (var i = 0; i < lines.Count; i++)
            {
                var lineFont = i == 0 ? fonts.SaleStatusBold : fonts.SaleStatus;
                var key = Key(i);
                var icon = Icon(key);
                var minimumX = rectangle.Left + SaleStatusPadding + (iconOnly && icon is not null ? iconSize + 6 : 0);
                var maximumWidth = Math.Max(1, (int)Math.Floor(rectangle.Right - (independentLines ? minimumX : sharedTextX) - SaleStatusPadding));
                var text = Ellipsize(graphics, lines[i], lineFont, maximumWidth);
                var measuredWidth = MeasureText(graphics, text, lineFont);
                // Center each line as its own icon + text group when the icon fits.
                // Text keeps priority over optional icons on narrow HUDs.
                var centeredIconWidth = independentLines && icon is not null
                    && measuredWidth + iconSize + 6 <= rectangle.Width - SaleStatusPadding * 2
                    ? iconSize + 6 : 0;
                var textX = independentLines
                    ? Math.Max(minimumX, rectangle.Left + (rectangle.Width - measuredWidth + centeredIconWidth) / 2f)
                    : sharedTextX;
                // Icons occupy only spare space to the left. Never move or shorten text to make room.
                var iconLeft = textX - iconSize - 6;
                if(icon is not null && (!independentLines || centeredIconWidth > 0) && iconLeft >= rectangle.Left + SaleStatusPadding)
                {
                    var bounds = new RectangleF(iconLeft, rectangle.Top + BusinessHudPaddingY + i * (lineHeight + SaleStatusLineGap) + (lineHeight-iconSize)/2f, iconSize,iconSize);
                    graphics.DrawImage(icon,ContainRectangle(icon.Size,bounds));
                }
                DrawSoftText(
                    graphics,
                    text,
                    lineFont,
                    brush,
                    textX,
                    rectangle.Top + BusinessHudPaddingY + i * (lineHeight + SaleStatusLineGap));
            }
        }
        finally
        {
            graphics.Restore(cardState);
        }
    }

    private sealed record BusinessHudDisplay(
        IReadOnlyList<string> Lines,
        bool IsOnline,
        string? IconKey, IReadOnlyList<string?>? LineIconKeys = null);
    private sealed record StaffAssignmentPopupItem(string Key, string TimeText, bool IsPaused = false);
}
