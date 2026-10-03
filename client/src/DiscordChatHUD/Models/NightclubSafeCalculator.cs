namespace DiscordChatHUD.Models;

internal static class NightclubSafeCalculator
{
    public static TimeSpan GetCycleDuration(NightclubSafeState state)
    {
        var multiplier = state.SpeedMultiplier is >= 2 and <= 4 ? state.SpeedMultiplier : 1;
        return TimeSpan.FromMinutes(NightclubSafeState.CycleMinutes / (double)multiplier);
    }

    // 인기도 감소. 기준은 인력(Staff) 업그레이드를 산 상태다.
    //
    // 업그레이드가 없으면 게임 내 하루(48분)마다 5%p 떨어진다 — 이 수치는 여러
    // 공략이 일치한다. 업그레이드를 사면 "느려진다"까지만 알려져 있고 정확한
    // 수치를 공개한 출처는 없어서, 통용되는 값인 '절반'을 쓴다. 다른 값으로
    // 바꾸려면 이 상수 하나만 고치면 된다.
    public const double PopularityDecayPerCycle = 2.5;

    // 적립액 표가 5% 단위로 끊기고 UI도 5단위로 움직이므로 인기도는 5단위로만
    // 내린다. 주기마다 남는 소수는 state 에 쌓아 두었다가 5가 차면 한 번 내린다.
    public const int PopularityStep = 5;

    // The detailed popularity table is not published in Rockstar's support
    // documentation. These current community-documented values are the
    // post-Title Update 1.59 Nightclub safe payouts.
    public static int GetIncome(int popularityPercent)
    {
        var popularity = Math.Clamp((int)Math.Round(popularityPercent / 5d) * 5, 0, 100);
        return popularity switch
        {
            >= 95 => 50_000,
            90 => 45_000,
            85 => 25_000,
            80 => 24_000,
            75 => 23_000,
            70 => 22_000,
            65 => 21_000,
            60 => 20_000,
            55 => 10_000,
            50 => 9_500,
            45 => 9_000,
            40 => 8_500,
            35 => 8_000,
            30 => 2_500,
            25 => 2_200,
            20 => 2_000,
            15 => 1_800,
            10 => 1_600,
            _ => 1_500
        };
    }

    public static bool Advance(NightclubSafeState state, double elapsedSeconds)
    {
        if (!state.HasStarted || state.IsPaused || elapsedSeconds <= 0d) return false;

        var durationSeconds = GetCycleDuration(state).TotalSeconds;
        var remaining = state.RemainingSeconds > 0d
            ? Math.Min(state.RemainingSeconds, durationSeconds)
            : durationSeconds;
        var elapsed = elapsedSeconds;
        var changed = false;
        while (elapsed >= remaining)
        {
            elapsed -= remaining;
            // 적립은 그 주기 동안 유지한 인기도로 계산하고, 그 뒤에 인기도를 내린다.
            // 100%로 시작하면 첫 주기는 $50,000 을 받고 95%가 된다.
            state.Cash = Math.Min(
                NightclubSafeState.Capacity,
                state.Cash + GetIncome(state.PopularityPercent));
            state.PopularityDecayCarry += PopularityDecayPerCycle;
            while (state.PopularityDecayCarry >= PopularityStep - 0.0001d)
            {
                state.PopularityDecayCarry -= PopularityStep;
                state.PopularityPercent = Math.Max(0, state.PopularityPercent - PopularityStep);
            }
            if (state.PopularityPercent <= 0) state.PopularityDecayCarry = 0d;
            remaining = durationSeconds;
            changed = true;
        }

        var nextRemaining = Math.Max(0d, remaining - elapsed);
        if (Math.Abs(state.RemainingSeconds - nextRemaining) > 0.001d)
        {
            state.RemainingSeconds = nextRemaining;
            changed = true;
        }
        return changed;
    }
}
