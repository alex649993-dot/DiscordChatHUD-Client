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
internal static partial class BusinessScreenReader
{
    private enum ReaderFamily { Legacy, MotorcycleClub }
    private static readonly HashSet<string> McKeys = new(StringComparer.OrdinalIgnoreCase)
        { "cocaine", "meth", "cash", "weed", "documents" };
    private static readonly HashSet<string> LegacyKeys = new(StringComparer.OrdinalIgnoreCase)
        { "bunker", "acid_lab" };
    // 아주 짧게 차 있는 막대도 잡아야 한다. 보급이 1%일 때가 정확히
    // 확인하고 싶은 순간인데, 여기를 크게 잡으면 그때를 못 읽는다.
    private const int MinimumRunLength = 3;
    // 같은 자리에서 이만큼 연속된 줄이어야 막대로 인정한다. 글자 획을 걸러낸다.
    private const int MinimumBandRows = 4;
    // 트랙 테두리로 인정할 대비. 어두운 화면에서는 이보다 밝아야 하고,
    // 밝은 화면에서는 이보다 어두워야 한다(R+G+B, 0~765).
    private const int OutlineBrightness = 330;
    // 프레임 밝기 중앙값이 이보다 크면 밝은 화면으로 본다.
    private const int LightScreenBrightness = 384;
    // 채움 끝을 찾을 때 보는 창의 크기와, 그 안에 있어야 하는 최소 개수.
    private const int FillWindow = 8;
    private const int FillHits = 3;
    // 막대 시작 x 가 이 안에서 어긋나는 건 같은 자리로 본다.
    private const int LeftTolerance = 4;
    // 트랙 오른쪽 끝으로 인정하려면 밴드의 이만큼이 같은 x 를 가리켜야 한다.
    private const double EdgeAgreement = 0.70;
    // 캡처가 사실상 검은 화면인지 판단할 기준.
    private const int BlackFrameBrightness = 24;
    // 진짜 막대는 화면 폭의 30% 안팎을 차지한다(1920 화면에서 600px). 이보다
    // 훨씬 짧으면 막대가 아니라 UI 조각을 잡은 것이다. 이 검사가 없어서
    // 9px 짜리 트랙을 정상으로 보고 엉뚱한 값을 적용한 적이 있다.
    private const double MinimumTrackWidthRatio = 0.15;
    // 한 화면에 있는 막대 칸 수(재고 / 연구 / 보급).
    private const int MaximumSlots = 3;

    internal enum BarKind { Stock, Research, Supply }

    private enum Theme { Dark, Light }

    internal sealed record Reading(
        bool Ok,
        string Message,
        int? StockPercent,
        int? SupplyPercent,
        int? ResearchPercent);

    private sealed record Band(BarKind Kind, int Left, List<(int Y, int Right)> Rows);

    /// <summary>세로로 한 칸을 이루는 막대 하나. 색이 같아도 칸은 따로 센다.</summary>
    private sealed record Cluster(BarKind Color, double CenterY, List<int> Rows);

    private static Reading Fail(string message) => new(false, message, null, null, null);

    /// <summary>
    /// 창을 떠서 막대를 읽는다. 실패하면 이유를 담아 돌려준다.
    /// HUD 가 화면을 가리는 문제는 호출하는 쪽이 잠깐 숨겨서 해결한다.
    /// 캡처 이미지에서 HUD 자리만 지우는 방법은 쓰지 않는다 — HUD 가 트랙의
    /// 오른쪽 테두리를 덮고 있으면 그 테두리까지 지워져 오히려 잘못 읽힌다.
    /// </summary>
    public static Reading Read(IntPtr window, string? expectedBusinessKey = null)
    {
        Bitmap? frame = null;
        try
        {
            frame = Capture(window, out var how);
            if (frame is null)
                return Fail("화면을 가져오지 못했습니다. GTA가 실행 중인지 확인해 주세요.");
            if (IsEssentiallyBlack(frame))
                return Fail(
                    "화면이 검게 캡처됩니다. 전체화면(독점) 모드에서는 읽을 수 없습니다. "
                    + "GTA 그래픽 설정에서 '테두리 없는 창모드'로 바꿔 주세요.");
            AppLog.Info($"사업장 화면 캡처: {frame.Width}x{frame.Height} · 방식 {how}");
            var reading = ReadFrame(frame, expectedBusinessKey);
            if (!reading.Ok) AppLog.Warn($"사업장 화면 판독 실패: {reading.Message}");
            return reading;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"사업장 화면 판독 실패: {ex.GetType().Name} · {ex.Message.Split('\n')[0]}");
            return Fail($"화면 판독 중 오류: {ex.GetType().Name}");
        }
        finally { frame?.Dispose(); }
    }

    internal static Reading ConfirmStable(Reading first, Reading second)
    {
        if (!first.Ok) return first;
        if (!second.Ok) return second;
        // Production can cross a one-percent boundary between two captures.
        // The same identified business and the same bar layout still have to agree.
        static bool Close(int? a, int? b) => a.HasValue == b.HasValue
            && (!a.HasValue || Math.Abs(a.Value - b!.Value) <= 1);
        if (!Close(first.StockPercent, second.StockPercent)
            || !Close(first.SupplyPercent, second.SupplyPercent)
            || !Close(first.ResearchPercent, second.ResearchPercent))
            return Fail("연속 판독 결과가 다릅니다. 노트북 화면이 멈춘 상태에서 다시 눌러 주세요. 기존 값은 유지합니다.");
        return second;
    }

    /// <summary>비트맵에서 막대를 읽는다.</summary>
    public static Reading ReadFrame(Bitmap frame, string? expectedBusinessKey = null)
    {
        var family = ResolveFamily(expectedBusinessKey);
        if (family is null)
            return Fail("지원하지 않는 사업장 대상입니다. 벙커/LSD 또는 MC 사업장을 선택해 주세요.");
        var width = frame.Width;
        var height = frame.Height;
        if (family == ReaderFamily.MotorcycleClub)
        {
            var mcPixels = CopyPixels(frame, out var mcStride);
            return ReadMotorcycleClub(mcPixels, mcStride, width, height);
        }
        // Most current screens are wider than 2560px. Try the common scale first:
        // it scans far fewer pixels, while the original frame remains the fallback.
        if (width > 1600)
        {
            using var normalized = new Bitmap(1280, Math.Max(1, (int)Math.Round(height * 1280d / width)), PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(normalized))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(frame, new Rectangle(0, 0, normalized.Width, normalized.Height));
            }
            var compact = ReadFrame(normalized, expectedBusinessKey);
            if (compact.Ok) return compact;
        }
        var pixels = CopyPixels(frame, out var stride);
        if (MedianBrightness(pixels, stride, width, height) >= LightScreenBrightness)
            return Fail("선택한 벙커/LSD 대상과 화면 유형이 다릅니다.");
        var reading = ReadPixels(pixels, stride, width, height, Theme.Dark);
        if (!reading.Ok) reading = ReadEmptyLegacy(pixels, stride, width, height,
            string.Equals(expectedBusinessKey, "bunker", StringComparison.OrdinalIgnoreCase) ? 3 : 2, reading);
        if (reading.Ok && (string.Equals(expectedBusinessKey, "bunker", StringComparison.OrdinalIgnoreCase)
            ? reading.ResearchPercent is null : reading.ResearchPercent is not null))
            return Fail("선택한 벙커/LSD 대상과 막대 구성이 다릅니다.");
        return reading;
    }

    // ── 화면 캡처 ────────────────────────────────────────────────────────

    private static bool IsEssentiallyBlack(Bitmap frame)
    {
        long total = 0;
        var samples = 0;
        for (var y = 0; y < frame.Height; y += Math.Max(1, frame.Height / 40))
        {
            for (var x = 0; x < frame.Width; x += Math.Max(1, frame.Width / 40))
            {
                var p = frame.GetPixel(x, y);
                total += p.R + p.G + p.B;
                samples++;
            }
        }
        return samples > 0 && total / samples < BlackFrameBrightness;
    }

    /// <summary>
    /// 바탕화면에서 그 창이 있는 자리만 복사한다. 게임 프로세스에는 아무것도
    /// 보내지 않고 메모리도 읽지 않는다. OBS 나 디스코드 화면공유와 같은
    /// 방식이다. 전체화면(독점) 모드에서는 검은 화면이 나올 수 있고, 그건
    /// 호출한 쪽에서 걸러서 안내한다.
    /// </summary>
    internal static Bitmap? Capture(IntPtr window, out string how)
    {
        how = "none";
        if (window == IntPtr.Zero) return null;
        if (!IsWindow(window) || IsIconic(window) || GetForegroundWindow() != window) return null;
        if (!GetClientRect(window, out var client) || client.Right <= client.Left || client.Bottom <= client.Top) return null;
        var origin = new POINT();
        if (!ClientToScreen(window, ref origin)) return null;
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0) return null;
        var captureBounds = new Rectangle(origin.X, origin.Y, width, height);
        if (!SystemInformation.VirtualScreen.Contains(captureBounds)) return null;

        Bitmap? copy = null;
        try
        {
            copy = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(copy))
                graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(width, height));
            if (GetForegroundWindow() != window || IsIconic(window)) { copy.Dispose(); return null; }
            how = "CopyFromScreen";
            return copy;
        }
        catch (Exception ex)
        {
            copy?.Dispose();
            AppLog.Warn($"화면 복사 실패: {ex.GetType().Name}");
            return null;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
}
