using System.Diagnostics;
namespace DiscordChatHUD.Services;

// Monotonic millisecond clock for media deadlines. TickCount64 on Windows can
// advance in coarse steps and turn a33ms deadline into another timer wait.
internal static class AnimationClock
{
    private static readonly long OriginTick = Environment.TickCount64;
    private static readonly long OriginStamp = Stopwatch.GetTimestamp();
    internal static long NowMilliseconds => OriginTick + (long)Stopwatch.GetElapsedTime(OriginStamp).TotalMilliseconds;
}
