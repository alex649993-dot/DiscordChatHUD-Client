using System.Media;
using System.Text;
using DiscordChatHUD.Logging;

namespace DiscordChatHUD.Services;
internal static class NotificationSound
{
    private static int playing;
    internal static byte[] CreateWave()
    {
        const int rate = 22050, samples = rate / 2;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)rate, local = t % .25;
            double envelope = Math.Min(1, local / .015) * Math.Max(0, 1 - local / .24);
            writer.Write((short)(Math.Sin(2 * Math.PI * (t < .25 ? 660 : 880) * t) * 11000 * envelope));
        }
        return stream.ToArray();
    }
    internal static void Play()
    {
        if (Interlocked.Exchange(ref playing, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try { using var stream = new MemoryStream(CreateWave()); using var player = new SoundPlayer(stream); player.PlaySync(); }
            catch (Exception ex) { AppLog.Warn($"알림음 재생 실패: {ex.GetType().Name}"); }
            finally { Interlocked.Exchange(ref playing, 0); }
        });
    }
}
