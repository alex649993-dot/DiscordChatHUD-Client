using System.Drawing.Imaging;
using DiscordChatHUD.Interop;
using DiscordChatHUD.Logging;

namespace DiscordChatHUD.Services;

/// <summary>
/// GTA 사업장 노트북 화면에 있는 막대(재고 / 연구 / 보급)를 읽는다.
///
/// 좌표를 박아 두지 않는다. 막대는 색으로 찾고, 채워진 길이를 트랙 전체
/// 길이로 나눠 퍼센트를 낸다. 그래서 해상도나 UI 배율이 달라져도 동작한다.
///
/// 트랙의 오른쪽 끝은 채워진 부분이 아니라 빈 구간의 테두리로 찾는다.
/// 채워진 끝으로 잡으면 두 막대가 똑같이 절반씩 차 있을 때 둘 다 100%로
/// 읽어 버린다.
///
/// 레거시 색 판독과 MC의 밝은 트랙 테두리 판독을 분리한다.
/// MC는 채움 길이와 무관하게 두 사각형의 전체 경계를 먼저 확인한다.
/// </summary>
// BusinessScreenReader — 사업장 화면별 판독: 빈 화면, 오토바이 클럽, 벙커·LSD 막대 읽기.
internal static partial class BusinessScreenReader
{

    // All-zero legacy bars have no colored seed. Require complete, aligned empty rectangles.
    private static Reading ReadEmptyLegacy(byte[] pixels, int stride, int width, int height, int count, Reading failure)
    {
        bool Border(int x, int y)
        {
            var i = y * stride + x * 4;
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            return r + g + b > OutlineBrightness && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 80;
        }
        var lines = new List<(int X, int Y, int Right)>();
        for (var y = 1; y < height - 1; y++)
        {
            var start = -1;
            for (var x = 0; x <= width; x++)
            {
                if (x < width && Border(x, y)) { if (start < 0) start = x; }
                else if (start >= 0)
                {
                    if (x - start > width * MinimumTrackWidthRatio) lines.Add((start, y, x - 1));
                    start = -1;
                }
            }
        }
        var rects = new List<Rectangle>();
        var tolerance = Math.Max(1, width / 1000);
        foreach (var top in lines)
        foreach (var bottom in lines)
        {
            var h = bottom.Y - top.Y;
            if (h < Math.Max(6, height / 160) || h > height * .06
                || Math.Abs(top.X - bottom.X) > tolerance || Math.Abs(top.Right - bottom.Right) > tolerance) continue;
            if (top.X <= 0 || top.Right >= width - 1) continue;
            var valid = true;
            for (var y = top.Y + 1; y < bottom.Y; y++)
            {
                if (!Border(top.X, y) || !Border(top.Right, y)) { valid = false; break; }
                for (var x = top.X + tolerance + 2; x < top.Right - tolerance - 1; x++)
                {
                    var i = y * stride + x * 4;
                    // Dark blue/green/orange fill can be below the brightness
                    // threshold. A failed colored read must never become 0%.
                    if (Brightness(pixels, stride, x, y) > OutlineBrightness
                        || Classify(pixels[i + 2], pixels[i + 1], pixels[i]) is not null)
                    { valid = false; break; }
                }
                if (!valid) break;
            }
            if (!valid) continue;
            var rect = Rectangle.FromLTRB(top.X, top.Y, top.Right + 1, bottom.Y + 1);
            if (!rects.Any(r => Math.Abs(r.Y - rect.Y) <= tolerance && Math.Abs(r.Bottom - rect.Bottom) <= tolerance && Math.Abs(r.X - rect.X) <= tolerance)) rects.Add(rect);
        }
        var groups = new List<List<Rectangle>>();
        foreach (var rect in rects)
        {
            var group = rects.Where(r => Math.Abs(r.X - rect.X) <= tolerance && Math.Abs(r.Width - rect.Width) <= tolerance * 2
                && Math.Abs(r.Height - rect.Height) <= tolerance * 2).OrderBy(r => r.Y).ToList();
            if (group.Count != count || groups.Any(g => g[0] == group[0])) continue;
            var gaps = group.Zip(group.Skip(1), (a,b) => b.Y - a.Y).ToArray();
            if (gaps.Any(g => g < rect.Height * 1.5 || g > rect.Height * 6) || gaps.Max() - gaps.Min() > tolerance * 2) continue;
            groups.Add(group);
        }
        return groups.Count == 1 ? new Reading(true, "판독 완료", 0, 0, count == 3 ? 0 : null) : failure;
    }

    private static ReaderFamily? ResolveFamily(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (McKeys.Contains(key.Trim())) return ReaderFamily.MotorcycleClub;
        if (LegacyKeys.Contains(key.Trim())) return ReaderFamily.Legacy;
        return null;
    }

    /// <summary>MC 화면은 공통 초록색 트랙 두 개만 읽는다. 막대만으로는 다섯 사업장 중
    /// 어느 것인지 증명할 수 없으므로 expectedBusinessKey가 필수이고, 두 값이 함께
    /// 확정되지 않으면 전체 판독을 실패시킨다.</summary>
    private static Reading ReadMotorcycleClub(byte[] pixels, int stride, int width, int height)
    {
        // The outline is blue-grey; white empty track pixels are NOT edge evidence.
        bool Border(int x, int y)
        {
            var i = y * stride + x * 4;
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            return r is >= 130 and <= 235 && g >= r - 5 && b >= g - 5
                && b - r is >= 5 and <= 65 && g - r <= 40;
        }
        var lines = new List<(int X, int Y, int Right)>();
        for (var y = 0; y < height; y++)
        {
            var start = -1;
            for (var x = 0; x <= width; x++)
            {
                if (x < width && Border(x, y)) { if (start < 0) start = x; }
                else if (start >= 0)
                {
                    if (x - start >= width * MinimumTrackWidthRatio) lines.Add((start, y, x - 1));
                    start = -1;
                }
            }
        }
        var rectangles = new List<Rectangle>();
        var tolerance = Math.Max(2, width / 1000);
        foreach (var top in lines)
        foreach (var bottom in lines)
        {
            var h = bottom.Y - top.Y;
            if (h < Math.Max(6, height / 160) || h > height * .10 || h > (top.Right - top.X) * .10
                || Math.Abs(top.X - bottom.X) > tolerance || Math.Abs(top.Right - bottom.Right) > tolerance) continue;
            var hits = 0;
            for (var y = top.Y; y <= bottom.Y; y++)
            {
                bool left = false, right = false;
                for (var dx = -tolerance; dx <= tolerance; dx++)
                {
                    if (top.X + dx >= 0 && top.X + dx < width) left |= Border(top.X + dx, y);
                    if (top.Right + dx >= 0 && top.Right + dx < width) right |= Border(top.Right + dx, y);
                }
                if (left && right) hits++;
            }
            if (hits < (h + 1) * .9) continue;
            var centerY = (top.Y + bottom.Y) / 2;
            if (Enumerable.Range(1, 3).Any(n => Border(top.X + (top.Right - top.X) * n / 4, centerY))) continue;
            var outsideTop = top.Y - tolerance - 1;
            var outsideBottom = bottom.Y + tolerance + 1;
            if (outsideTop < 0 || outsideBottom >= height) continue;
            if (Enumerable.Range(1, 3).Any(n =>
                Brightness(pixels, stride, top.X + (top.Right - top.X) * n / 4, outsideTop) < 690
                || Brightness(pixels, stride, top.X + (top.Right - top.X) * n / 4, outsideBottom) < 690)) continue;
            var rect = Rectangle.FromLTRB(top.X, top.Y, top.Right + 1, bottom.Y + 1);
            if (!rectangles.Any(r => Math.Abs(r.X - rect.X) <= tolerance
                && Math.Abs(r.Y - rect.Y) <= tolerance && Math.Abs(r.Bottom - rect.Bottom) <= tolerance)) rectangles.Add(rect);
        }
        var pairs = new List<(Rectangle Stock, Rectangle Supply)>();
        foreach (var a in rectangles)
        foreach (var b in rectangles)
        {
            var gap = b.Y - a.Y;
            if (Math.Abs(a.X - b.X) > tolerance || Math.Abs(a.Width - b.Width) > tolerance * 2
                || Math.Abs(a.Height - b.Height) > Math.Max(3, a.Height * .25)
                || gap < a.Height * 1.5 || gap > a.Height * 6) continue;
            pairs.Add((a, b));
        }
        if (pairs.Count != 1) return Fail("MC 재고/보급의 테두리 두 개를 확정하지 못했습니다.");
        int? Measure(Rectangle rect)
        {
            var results = new List<int>();
            for (var y = rect.Y + rect.Height / 3; y <= rect.Y + rect.Height * 2 / 3; y++)
            {
                var left = rect.Left;
                while (left < rect.Right && Border(left, y)) left++;
                var right = rect.Right - 1;
                while (right > left && Border(right, y)) right--;
                if (right - left < width * MinimumTrackWidthRatio) return null;
                var end = left - 1; var unknown = 0; var greenHits = 0;
                for (var x = left; x <= right; x++)
                {
                    var i = y * stride + x * 4;
                    int blue = pixels[i], green = pixels[i + 1], red = pixels[i + 2];
                    if (green > 70 && green > red + 35 && green > blue + 35) { end = x; greenHits++; }
                    else if (red + green + blue < 690) unknown++;
                }
                // Reject arbitrary colored content and isolated green marks inside an empty track.
                if (unknown > (right - left + 1) * .03 || (end >= left && greenHits < (end - left + 1) * .94)) return null;
                results.Add((int)Math.Round((end - left + 1) * 100d / (right - left + 1)));
            }
            return results.Max() - results.Min() <= 2 ? results.Order().ElementAt(results.Count / 2) : null;
        }
        var stock = Measure(pairs[0].Stock); var supply = Measure(pairs[0].Supply);
        if (stock is null || supply is null) return Fail("MC 재고/보급 채움을 함께 확정하지 못했습니다.");
        return new Reading(true, "판독 완료", stock, supply, null);
    }

    // Legacy-only entry point retained for existing pixel-level callers.
    internal static Reading ReadPixels(byte[] pixels, int stride, int width, int height)
    {
        if (MedianBrightness(pixels, stride, width, height) >= LightScreenBrightness)
            return Fail("레거시 화면이 아닙니다.");
        return ReadPixels(pixels, stride, width, height, Theme.Dark);
    }

    private static string Name(Theme theme) => theme == Theme.Light ? "밝은" : "어두운";

    private static Reading ReadPixels(byte[] pixels, int stride, int width, int height, Theme theme)
    {
        var bands = FindBands(pixels, stride, width, height);
        if (bands.Count == 0) return Fail("막대를 찾지 못했습니다. 사업장 노트북 화면에서 눌러 주세요.");

        var lefts = new List<int>();
        foreach (var band in bands)
            if (!lefts.Any(value => Math.Abs(value - band.Left) <= LeftTolerance))
                lefts.Add(band.Left);

        // 같은 x 에서 시작하는 밴드를 묶어 후보를 만든다. 색이 여러 개인 쪽,
        // 칸이 많은 쪽, 그리고 왼쪽 순서로 본다. 어느 후보가 진짜인지는
        // 끝까지 읽어 봐야 알 수 있으므로 순서대로 시도한다.
        // (예전에는 '서로 다른 색 2개 이상'인 후보만 봤다. 그래서 막대가 모두
        //  같은 초록인 오토바이 클럽 화면을 통째로 거부했다.)
        var maximumRowGap = Math.Max(4, height / 200);
        var candidates = lefts
            .Select(left =>
            {
                var group = bands.Where(band => Math.Abs(band.Left - left) <= LeftTolerance).ToList();
                return (
                    Left: left,
                    Bands: group,
                    Clusters: BuildClusters(group, maximumRowGap),
                    Kinds: group.Select(band => band.Kind).Distinct().Count());
            })
            .Where(item => item.Clusters.Count >= 1 && item.Clusters.Count <= MaximumSlots)
            .OrderByDescending(item => item.Kinds)
            .ThenByDescending(item => item.Clusters.Count)
            .ThenBy(item => item.Left)
            .ToList();
        if (candidates.Count == 0)
            return Fail("막대다운 묶음을 찾지 못했습니다. 사업장 노트북 화면인지 확인해 주세요.");

        var lastMessage = "막대다운 묶음을 찾지 못했습니다. 사업장 노트북 화면인지 확인해 주세요.";
        foreach (var candidate in candidates)
        {
            var reading = TryRead(
                pixels, stride, width, height, theme,
                bands, candidate.Left, candidate.Bands, candidate.Clusters);
            if (reading.Ok) return reading;
            lastMessage = reading.Message;
        }
        return Fail(lastMessage);
    }

    private static Reading TryRead(
        byte[] pixels, int stride, int width, int height, Theme theme,
        List<Band> allBands, int left, List<Band> selected, List<Cluster> clusters)
    {
        var rowRight = new Dictionary<int, int>();
        foreach (var band in selected)
        foreach (var (y, right) in band.Rows)
            rowRight[y] = rowRight.TryGetValue(y, out var known) ? Math.Max(known, right) : right;
        var fillRight = rowRight.Values.Max();

        var edges = new List<int>();
        foreach (var cluster in clusters)
        {
            var (agreement, edge) = FindClusterEdge(pixels, stride, width, theme, cluster, rowRight);
            if (edge is { } value && agreement >= EdgeAgreement) edges.Add(value);
        }
        // A colored run is not evidence of the track end: at 100% it reaches
        // the end and using it as a fallback turns an unbounded bar into a
        // falsely certain 100%. Legacy readings therefore require corroborated
        // outline evidence from the selected bands.
        if (edges.Count == 0)
            return Fail("레거시 트랙 오른쪽 경계를 확인하지 못했습니다.");
        var trackRight = MostCommon(edges);
        if (trackRight < fillRight)
            return Fail("레거시 트랙 경계가 채움보다 앞에 있습니다.");
        var track = Math.Max(1, trackRight - left + 1);
        if (track < width * MinimumTrackWidthRatio)
            return Fail($"막대로 보기엔 너무 짧습니다({track}px). 사업장 노트북 화면인지 확인해 주세요.");

        // 채운 길이는 색 덩어리의 길이로 재지 않는다. 막대 안에 눈금이나 광택이
        // 있으면 색이 중간에 끊기고, 끊긴 뒤쪽은 시작 x 가 달라 다른 덩어리로
        // 취급돼 버려진다. 그러면 95% 인 막대가 3% 로 읽힌다(실측).
        // 대신 트랙 안에서 그 색이 마지막으로 나오는 지점을 직접 찾는다.
        var slots = new List<(double CenterY, BarKind? Color, int Percent)>();
        foreach (var cluster in clusters)
        {
            // A single bright/noisy row must not extend the whole bar.
            // Trim blended borders and require the interior rows to agree.
            var rows = cluster.Rows.Order().ToArray();
            var trim = rows.Length >= 8 ? rows.Length / 5 : 0;
            var ends = rows.Skip(trim).Take(rows.Length - trim * 2)
                .Select(y => FindFillEnd(pixels, stride, y, left, trackRight, cluster.Color)).Order().ToArray();
            var end = ends[ends.Length / 2];
            var tolerance = Math.Max(2, (int)Math.Ceiling(track * .015));
            if (ends.Count(value => Math.Abs(value - end) <= tolerance) < ends.Length * EdgeAgreement)
                return Fail("막대 채움 경계가 불안정합니다. 화면 가림이나 움직임이 멈추면 다시 확인합니다.");
            var filled = end > 0 ? end - left + 1 : 0;
            slots.Add((cluster.CenterY, cluster.Color, Math.Clamp((int)Math.Round(filled * 100d / track), 0, 100)));
        }

        foreach (var center in FindEmptySlots(pixels, stride, width, height, theme, clusters, left, trackRight))
            slots.Add((center, null, 0));

        var ordered = slots.OrderBy(slot => slot.CenterY).ToList();
        if (ordered.Count > MaximumSlots)
            ordered = ordered.Where(slot => slot.Color is not null).OrderBy(slot => slot.CenterY).ToList();
        // 칸이 하나뿐이면 막대가 아니라 UI 덩어리다. 화면 왼쪽의 빨간 메뉴
        // 블록이 여기서 걸러진다 — 그 옆에는 짝이 되는 빈 칸이 없다.
        if (ordered.Count < 2)
            return Fail("막대다운 묶음을 찾지 못했습니다. 사업장 노트북 화면인지 확인해 주세요.");

        // A legacy colored slot must agree with its actual stock/research/supply color.
        // Only genuinely empty outlined slots may infer their meaning from vertical order.
        if (!SlotOrder.TryGetValue(ordered.Count, out var order)) return Fail("레거시 막대 개수가 다릅니다.");
        var percents = new Dictionary<BarKind, int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Color is { } color && color != order[i])
                return Fail("레거시 막대 색과 순서가 일치하지 않습니다.");
            percents[order[i]] = ordered[i].Percent;
        }

        int? Get(BarKind kind) => percents.TryGetValue(kind, out var value) ? value : null;
        var stock = Get(BarKind.Stock);
        var supply = Get(BarKind.Supply);
        var research = Get(BarKind.Research);
        if (stock is null && supply is null) return Fail("재고와 보급 막대를 찾지 못했습니다.");

        AppLog.Info(
            $"사업장 화면 판독({Name(theme)}): 트랙 x{left}~{trackRight}({track}px) · {ordered.Count}칸 · "
            + $"재고 {stock?.ToString() ?? "-"}% / 보급 {supply?.ToString() ?? "-"}% / 연구 {research?.ToString() ?? "-"}%");
        // 값이 이상할 때 원인을 코드 없이 알 수 있도록, 고른 칸과 버려진 묶음을
        // 같이 남긴다. 버려진 쪽이 더 길면 막대가 끊겨 있다는 뜻이다.
        foreach (var slot in ordered)
            AppLog.Info($"  · 칸 y{slot.CenterY:0} 색 {slot.Color?.ToString() ?? "없음"} {slot.Percent}%");
        foreach (var band in allBands.Except(selected)
                     .Where(band => band.Rows.Max(row => row.Right) - band.Left > track / 4)
                     .OrderByDescending(band => band.Rows.Max(row => row.Right) - band.Left)
                     .Take(4))
            AppLog.Info($"  · {band.Kind} 버림 x{band.Left}~{band.Rows.Max(row => row.Right)} ({band.Rows.Count}줄)");
        return new Reading(true, "판독 완료", stock, supply, research);
    }
}
