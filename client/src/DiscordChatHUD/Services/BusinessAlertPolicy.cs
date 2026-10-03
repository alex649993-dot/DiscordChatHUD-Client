using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

/// <summary>
/// 사업장 상태가 알릴 만한 지점을 지날 때만 한 번씩 알린다.
///
/// 상태를 계속 들고 있다가 조건이 참으로 "바뀌는 순간"에만 알림을 낸다.
/// 조건이 계속 참인 동안에는 다시 알리지 않고, 거짓으로 돌아가면 다시
/// 무장한다. 그래서 보급을 채우면 다음에 또 알림을 받는다.
/// </summary>
internal sealed class BusinessAlertPolicy
{
    internal sealed record Alert(string BusinessKey, string Title, string Body);

    private sealed class State
    {
        public bool SupplyWarned;
        public bool StockWarned;
        public bool BoostWasActive;
        public bool DailyBoostWasActive;
        public bool Seen;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public bool SupplyLowEnabled { get; set; } = true;
    public bool StockFullEnabled { get; set; } = true;
    public bool BoostEndedEnabled { get; set; } = true;
    public int SupplyWarningMinutes { get; set; } = 10;

    public IReadOnlyList<Alert> Evaluate(
        IEnumerable<BusinessSupplyEntry> entries,
        DateTimeOffset now, bool trackingOnline = true)
    {
        var alerts = new List<Alert>();
        var threshold = TimeSpan.FromMinutes(Math.Clamp(SupplyWarningMinutes, 1, 120));
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Key)) continue;
            if (!_states.TryGetValue(entry.Key, out var state))
            {
                state = new State();
                _states[entry.Key] = state;
            }

            var progress = BusinessSupplyCalculator.Calculate(entry, now, trackingOnline);

            // 보급 소진 임박. 남은 시간이 임계값 아래로 "내려오는" 순간에만 알린다.
            var deliveryPending = entry.SupplyDeliveryRequestedAtUtc is not null
                                  && BusinessSupplyCalculator.SupplyDeliveryRemaining(entry, now) is { } deliveryLeft
                                  && deliveryLeft > TimeSpan.Zero;
            var supplyLow = entry.Enabled
                            && !deliveryPending
                            && progress.IsOnline
                            && progress.UntilSupplyEmpty is { } left
                            && left > TimeSpan.Zero
                            && left <= threshold;
            if (SupplyLowEnabled && supplyLow && !state.SupplyWarned && state.Seen)
            {
                var minutes = Math.Max(1, (int)Math.Round(progress.UntilSupplyEmpty!.Value.TotalMinutes));
                alerts.Add(new Alert(
                    entry.Key,
                    $"{progress.Name} · 보급 부족",
                    $"약 {minutes}분 뒤 보급이 바닥납니다."));
            }
            state.SupplyWarned = supplyLow;

            // 재고 가득. 더 만들어도 버려지는 지점이다.
            var stockFull = entry.Enabled && progress.StockPercent >= 100;
            if (StockFullEnabled && stockFull && !state.StockWarned && state.Seen)
            {
                alerts.Add(new Alert(
                    entry.Key,
                    $"{progress.Name} · 재고 가득",
                    "재고가 최대입니다. 지금부터 생산분은 버려집니다."));
            }
            state.StockWarned = stockFull;

            // 멘션 부스트 종료.
            if (BoostEndedEnabled
                && state.Seen
                && state.BoostWasActive
                && !progress.MansionBoostActive)
            {
                alerts.Add(new Alert(
                    entry.Key,
                    $"{progress.Name} · 부스트 종료",
                    "멘션 부스트가 끝났습니다."));
            }
            state.BoostWasActive = progress.MansionBoostActive;

            // LSD 연구소 일일 부스트 종료 (80개 생산 또는 24시간).
            if (BoostEndedEnabled
                && state.Seen
                && state.DailyBoostWasActive
                && !progress.DailyBoostActive)
            {
                alerts.Add(new Alert(
                    entry.Key,
                    $"{progress.Name} · 일일 부스트 종료",
                    "80개 생산 또는 24시간이 지나 생산 2배가 끝났습니다."));
            }
            state.DailyBoostWasActive = progress.DailyBoostActive;

            // 첫 평가에서는 알리지 않는다. 프로그램을 켜자마자 이미 참인 조건까지
            // 쏟아내면 알림이 의미를 잃는다.
            state.Seen = true;
        }
        return alerts;
    }

    public void Reset() => _states.Clear();
}
