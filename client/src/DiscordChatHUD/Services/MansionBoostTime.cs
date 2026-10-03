namespace DiscordChatHUD.Services;
internal static class MansionBoostTime
{
    internal static DateTimeOffset? StartForRemaining(DateTimeOffset now, TimeSpan remaining)
    {
        if(remaining<TimeSpan.Zero || remaining>TimeSpan.FromHours(24))throw new ArgumentOutOfRangeException(nameof(remaining));
        return remaining==TimeSpan.Zero ? null : now-TimeSpan.FromHours(24)+remaining;
    }
    internal static TimeSpan Remaining(DateTimeOffset? started,DateTimeOffset now)
    {
        if(started is null)return TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Clamp(Math.Ceiling((started.Value+TimeSpan.FromHours(24)-now).TotalSeconds),0,86400));
    }
}
