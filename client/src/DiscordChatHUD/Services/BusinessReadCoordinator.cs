using DiscordChatHUD.Logging;

namespace DiscordChatHUD.Services;

internal sealed record BusinessReadSample(
    string Key, BusinessScreenReader.Reading? Bars, IReadOnlyList<NightclubObservation> Items);

internal static class BusinessReadCoordinator
{
    internal static async Task<BusinessReadSample> ReadAsync(
        Func<CancellationToken, Task<BusinessReadSample>> capture,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        BusinessReadSample? previous = null;
        string? identified = null;
        string failure = "연속 판독 결과를 확정하지 못했습니다. 기존 값은 유지합니다.";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 1)
                await delay(TimeSpan.FromMilliseconds(previous is null ? 350 : 80), ct);
            BusinessReadSample current;
            try
            {
                current = await capture(ct);
                ct.ThrowIfCancellationRequested();
                if (identified is not null && current.Key != identified)
                    throw new InvalidOperationException("판독 중 사업장 화면이 변경되었습니다. 기존 값은 유지합니다.");
                identified = current.Key;
                if (current.Key != "nightclub" && current.Bars is not { Ok: true, StockPercent: not null, SupplyPercent: not null })
                    throw new InvalidDataException(current.Bars?.Message ?? "재고와 보급 막대를 확인하지 못했습니다.");
                if (current.Key == "nightclub" && current.Items.Count != 7)
                    throw new InvalidDataException("나이트클럽 7개 상품을 모두 확인하지 못했습니다.");
            }
            catch (InvalidDataException ex)
            {
                failure = ex.Message; previous = null;
                AppLog.Info($"사업장 판독 재확인: {attempt}/3 · {failure}");
                continue;
            }
            if (previous is not null)
            {
                if (current.Key == "nightclub")
                {
                    if (current.Items.OrderBy(x => x.Key).SequenceEqual(previous.Items.OrderBy(x => x.Key)))
                        return current;
                    failure = "상품 수량이 연속 화면에서 다릅니다. 기존 값은 유지합니다.";
                }
                else
                {
                    var stable = BusinessScreenReader.ConfirmStable(previous.Bars!, current.Bars!);
                    if (stable.Ok) return current with { Bars = stable };
                    failure = stable.Message;
                }
                AppLog.Info($"사업장 판독 재확인: {attempt}/3 · {failure}");
            }
            previous = current;
        }
        throw new InvalidDataException(failure);
    }
}
