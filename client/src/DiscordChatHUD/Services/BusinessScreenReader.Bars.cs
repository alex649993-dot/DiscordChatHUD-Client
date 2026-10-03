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
// BusinessScreenReader — 픽셀에서 막대 찾기: 밝기, 색 분류, 띠·묶음, 채움 끝, 빈 칸.
internal static partial class BusinessScreenReader
{

    // ── 픽셀 ─────────────────────────────────────────────────────────────

    private static byte[] CopyPixels(Bitmap frame, out int stride)
    {
        var data = frame.LockBits(
            new Rectangle(0, 0, frame.Width, frame.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            var buffer = new byte[Math.Abs(data.Stride) * frame.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            return buffer;
        }
        finally { frame.UnlockBits(data); }
    }

    private static int Brightness(byte[] pixels, int stride, int x, int y)
    {
        var i = y * stride + x * 4;
        return pixels[i] + pixels[i + 1] + pixels[i + 2];
    }

    /// <summary>프레임 밝기의 중앙값. 화면이 밝은지 어두운지 판단하는 데 쓴다.</summary>
    private static int MedianBrightness(byte[] pixels, int stride, int width, int height)
    {
        var samples = new List<int>();
        var stepX = Math.Max(1, width / 64);
        var stepY = Math.Max(1, height / 64);
        for (var y = 0; y < height; y += stepY)
        for (var x = 0; x < width; x += stepX)
            samples.Add(Brightness(pixels, stride, x, y));
        if (samples.Count == 0) return 0;
        samples.Sort();
        return samples[samples.Count / 2];
    }

    /// <summary>
    /// 트랙 테두리인지. 어두운 화면에서는 바탕보다 밝고, 밝은 화면에서는
    /// 바탕보다 어둡다. 막대 색 자체는 어느 쪽에서도 테두리가 아니다 —
    /// 이 검사가 없으면 밝은 화면에서 초록 막대(밝기 151)를 테두리로 봐서
    /// 트랙이 채운 만큼으로 줄어들고 언제나 100%가 나온다.
    /// </summary>
    private static bool IsOutline(byte[] pixels, int stride, int x, int y, Theme theme)
    {
        var i = y * stride + x * 4;
        if (Classify(pixels[i + 2], pixels[i + 1], pixels[i]) is not null) return false;
        var brightness = pixels[i] + pixels[i + 1] + pixels[i + 2];
        return theme == Theme.Dark ? brightness >= OutlineBrightness : brightness <= OutlineBrightness;
    }

    /// <summary>
    /// 막대 색 판정. 실제 화면에서 잰 값 기준이다.
    /// 어두운 화면: 재고 파랑 (12,114,176) · 연구 초록 (20,127,32) · 보급 주황 (194,58,19).
    /// 밝은 화면(오토바이 클럽): 막대는 초록 (0,138,13) 하나뿐이라 모두 Research 로
    /// 잡히고, 이름은 세로 순서로 붙인다.
    /// 채널 차이로만 보므로 밝기 설정이 달라도 견딘다.
    /// </summary>
    private static BarKind? Classify(byte r, byte g, byte b)
    {
        int red = r, green = g, blue = b;
        if (Math.Max(red, Math.Max(green, blue)) < 60) return null;
        if (blue > red + 55 && blue > green + 25 && green > red) return BarKind.Stock;
        if (red > blue + 70 && red > green + 55) return BarKind.Supply;
        if (green > red + 45 && green > blue + 45) return BarKind.Research;
        return null;
    }

    private static List<Band> FindBands(byte[] pixels, int stride, int width, int height)
    {
        // 막대 안에 광택이나 눈금이 있어 색 판정이 잠깐 끊길 수 있다. 끊긴
        // 뒤쪽은 시작 x 가 달라 완전히 다른 덩어리로 취급되고, 그러면 막대의
        // 앞토막만 남아 길이가 실제보다 훨씬 짧게 나온다. 짧은 틈은 이어 붙인다.
        var maximumGap = Math.Max(6, width / 200);
        var map = new Dictionary<(BarKind, int), List<(int, int)>>();
        var runs = new List<(BarKind Kind, int Start, int End)>();
        for (var y = 0; y < height; y++)
        {
            var rowBase = y * stride;
            runs.Clear();
            BarKind? runKind = null;
            var runStart = 0;
            for (var x = 0; x <= width; x++)
            {
                BarKind? kind = null;
                if (x < width)
                {
                    var i = rowBase + x * 4;
                    kind = Classify(pixels[i + 2], pixels[i + 1], pixels[i]);
                }
                if (kind == runKind) continue;
                if (runKind is { } finished && x - runStart >= MinimumRunLength)
                    runs.Add((finished, runStart, x - 1));
                runKind = kind;
                runStart = x;
            }

            for (var i = runs.Count - 1; i > 0; i--)
            {
                var previous = runs[i - 1];
                var current = runs[i];
                if (current.Kind != previous.Kind) continue;
                if (current.Start - previous.End - 1 > maximumGap) continue;
                runs[i - 1] = (previous.Kind, previous.Start, current.End);
                runs.RemoveAt(i);
            }

            foreach (var run in runs)
            {
                var key = (run.Kind, run.Start);
                if (!map.TryGetValue(key, out var rows)) map[key] = rows = [];
                rows.Add((y, run.End));
            }
        }
        return map
            .Where(pair => pair.Value.Count >= MinimumBandRows)
            .Select(pair => new Band(pair.Key.Item1, pair.Key.Item2, pair.Value))
            .ToList();
    }

    /// <summary>
    /// 같은 x 에서 시작하는 밴드들을 세로로 끊어 칸으로 만든다. 색이 같아도
    /// 세로로 떨어져 있으면 다른 칸이다(오토바이 클럽 화면의 재고량 / 보급량).
    /// </summary>
    private static List<Cluster> BuildClusters(List<Band> bands, int maximumRowGap)
    {
        var rows = bands
            .SelectMany(band => band.Rows.Select(row => (row.Y, band.Kind)))
            .GroupBy(item => item.Y)
            .Select(group => (Y: group.Key, Kinds: group.Select(item => item.Kind).ToList()))
            .OrderBy(item => item.Y)
            .ToList();
        var clusters = new List<Cluster>();
        var currentRows = new List<int>();
        var currentKinds = new List<BarKind>();
        void Flush()
        {
            if (currentRows.Count < MinimumBandRows) { currentRows.Clear(); currentKinds.Clear(); return; }
            var color = currentKinds
                .GroupBy(kind => kind)
                .OrderByDescending(group => group.Count())
                .First().Key;
            clusters.Add(new Cluster(color, currentRows.Average(), [..currentRows]));
            currentRows.Clear();
            currentKinds.Clear();
        }
        foreach (var row in rows)
        {
            if (currentRows.Count > 0 && row.Y - currentRows[^1] > maximumRowGap) Flush();
            currentRows.Add(row.Y);
            currentKinds.AddRange(row.Kinds);
        }
        Flush();
        return clusters;
    }

    /// <summary>
    /// 한 줄에서 그 색이 실제로 끝나는 지점. 오른쪽부터 훑다가 최근
    /// <see cref="FillWindow"/> 픽셀 중 <see cref="FillHits"/> 개 이상이 그
    /// 색이면 거기를 끝으로 본다. 연속을 요구하지 않으므로 막대 끝에 눈금이
    /// 걸쳐 있어도 정확하다.
    /// </summary>
    private static int FindFillEnd(
        byte[] pixels, int stride, int y, int left, int trackRight, BarKind kind)
    {
        var window = new bool[FillWindow];
        var filled = 0;
        var hits = 0;
        for (var x = trackRight; x >= left; x--)
        {
            var slot = filled % FillWindow;
            if (filled >= FillWindow && window[slot]) hits--;
            var i = y * stride + x * 4;
            var match = Classify(pixels[i + 2], pixels[i + 1], pixels[i]) == kind;
            window[slot] = match;
            if (match) hits++;
            filled++;
            if (hits < FillHits) continue;
            // 창 안에서 가장 오른쪽에 있는 그 색 픽셀이 실제 끝이다.
            for (var back = x + FillWindow - 1; back >= x; back--)
            {
                if (back > trackRight) continue;
                var j = y * stride + back * 4;
                if (Classify(pixels[j + 2], pixels[j + 1], pixels[j]) == kind) return back;
            }
            return x;
        }
        return 0;
    }

    /// <summary>
    /// 한 칸의 오른쪽 테두리 x 와 그 일치율. 막대는 모든 줄이 같은 x 를 가리키고
    /// (일치율 1.0), 글자가 있는 UI 블록은 줄마다 달라 0.3 을 넘지 못한다.
    /// </summary>
    private static (double Agreement, int? Edge) FindClusterEdge(
        byte[] pixels, int stride, int width, Theme theme,
        Cluster cluster, Dictionary<int, int> rowRight)
    {
        var found = new List<int>();
        // Exclude blended top/bottom border rows: the horizontal outline is
        // otherwise mistaken for the vertical track end after scaling.
        var orderedRows = cluster.Rows.OrderBy(y => y).ToArray();
        var trim = orderedRows.Length >= 8 ? orderedRows.Length / 5 : 0;
        var sampleRows = orderedRows.Skip(trim).Take(orderedRows.Length - trim * 2).ToArray();
        foreach (var y in sampleRows)
        {
            if (!rowRight.TryGetValue(y, out var fillRight)) continue;
            // 채운 부분 바로 오른쪽부터 오른쪽으로 훑어 '처음' 만나는 테두리가
            // 트랙 끝이다. 반대로 화면 끝에서부터 훑으면 그 줄에 있는 다른 UI 를
            // 테두리로 착각해 트랙이 실제보다 길어지고, 그만큼 퍼센트가 낮게
            // 나온다(실측 95% -> 67%).
            for (var x = fillRight + 1; x < width; x++)
            {
                if (!IsOutline(pixels, stride, x, y, theme)) continue;
                found.Add(x);
                break;
            }
        }
        if (found.Count == 0) return (0d, null);
        var tolerance = Math.Max(1, width / 1280);
        var edge = found.OrderByDescending(candidate => found.Count(value => Math.Abs(value - candidate) <= tolerance))
            .ThenBy(candidate => candidate).First();
        var agree = found.Count(value => Math.Abs(value - edge) <= tolerance) / (double)sampleRows.Length;
        return (agree, edge);
    }

    // 3칸이면 재고 / 연구 / 보급, 2칸이면 재고 / 보급 순서다.
    private static readonly Dictionary<int, BarKind[]> SlotOrder = new()
    {
        [3] = [BarKind.Stock, BarKind.Research, BarKind.Supply],
        [2] = [BarKind.Stock, BarKind.Supply],
        [1] = [BarKind.Stock]
    };

    /// <summary>
    /// 0% 인 막대는 색이 없어 안 잡힌다. 트랙 오른쪽 테두리만 남은 구간을 찾아
    /// 빈 칸으로 본다. 색이 있는 칸들과 두께가 비슷하고 가까이 있는 것만
    /// 인정한다 — 그러지 않으면 창 테두리 같은 긴 세로선이 빈 칸이 된다.
    /// </summary>
    private static List<double> FindEmptySlots(
        byte[] pixels, int stride, int width, int height, Theme theme,
        List<Cluster> clusters, int left, int trackRight)
    {
        var result = new List<double>();
        if (clusters.Count == 0) return result;
        var colored = new HashSet<int>(clusters.SelectMany(cluster => cluster.Rows));
        var rows = new List<int>();
        for (var y = 0; y < height; y++)
        {
            for (var x = trackRight - 1; x <= trackRight + 1; x++)
            {
                if (x < 0 || x >= width) continue;
                if (!IsOutline(pixels, stride, x, y, theme)) continue;
                rows.Add(y);
                break;
            }
        }
        var groups = new List<(int First, int Last)>();
        foreach (var y in rows)
        {
            if (groups.Count > 0 && y - groups[^1].Last <= 2)
                groups[^1] = (groups[^1].First, y);
            else groups.Add((y, y));
        }

        var thickness = clusters.Average(cluster => cluster.Rows.Count);
        var top = clusters.Min(cluster => cluster.CenterY) - thickness * 8;
        var bottom = clusters.Max(cluster => cluster.CenterY) + thickness * 8;
        foreach (var group in groups)
        {
            var span = group.Last - group.First + 1;
            if (span < MinimumBandRows) continue;
            if (span < thickness * 0.5 || span > thickness * 2.5) continue;
            if (colored.Any(y => y >= group.First - 3 && y <= group.Last + 3)) continue;
            var center = (group.First + group.Last) / 2d;
            if (center < top || center > bottom) continue;
            // An outlined slot is empty only when its interior is uncolored,
            // not merely when the main band detector missed a short fill.
            var hasFill = false;
            for (var y = group.First + 1; y < group.Last && !hasFill; y++)
            for (var x = left + 2; x < trackRight - 1; x++)
            {
                var i = y * stride + x * 4;
                if (Classify(pixels[i + 2], pixels[i + 1], pixels[i]) is not null)
                { hasFill = true; break; }
            }
            if (!hasFill) result.Add(center);
        }
        return result;
    }

    private static int MostCommon(IEnumerable<int> values)
    {
        var groups = values
            .GroupBy(value => value)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .ToList();
        return groups.Count == 0 ? 0 : groups[0].Key;
    }
}
