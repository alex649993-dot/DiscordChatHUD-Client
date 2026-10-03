using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using DiscordChatHUD.Models;
namespace DiscordChatHUD.Services;

// Metadata only. Disk writes never run on the gateway, HTTP or UI thread.
internal static class ChatTiming
{
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(1024) { FullMode=BoundedChannelFullMode.Wait, SingleReader=true });
    private static readonly object Gate = new();
    private sealed class State(long tick) { internal long Tick=tick; internal int Stages; }
    private static readonly Dictionary<(ulong,ulong),State> Seen = new();
    private static readonly Queue<(ulong,ulong)> Order = new();
    private static string PathName = Path.Combine(AppContext.BaseDirectory,"Data","chat-timing.log");
    private static string Role = "client";
    private static int Version = typeof(ChatTiming).Assembly.GetName().Version!.Build;
    private static int Started, Dropped;
    internal static event Action<ChatReceipt>? Observed;
    internal static void ConfigureServer(int version) { Role="relay"; Version=version; PathName=Path.Combine(AppContext.BaseDirectory,"chat-timing.log"); }
    internal static void Write(string stage,string fields)
    {
        var line=$"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} role={Role} version={Version} stage={stage} {fields}";
        if(!Queue.Writer.TryWrite(line)) Interlocked.Increment(ref Dropped);
        if(Interlocked.Exchange(ref Started,1)==0) _=Task.Run(Drain);
    }
    private static async Task Drain()
    {
        while(await Queue.Reader.WaitToReadAsync())
        {
            var lines=new List<string>(128);
            var dropped=Interlocked.Exchange(ref Dropped,0);
            if(dropped>0)lines.Add($"{DateTimeOffset.UtcNow:O} stage=dropped count={dropped}");
            while(lines.Count<128 && Queue.Reader.TryRead(out var line))lines.Add(line);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                if(File.Exists(PathName) && new FileInfo(PathName).Length>=2*1024*1024)File.Move(PathName,PathName+".previous",true);
                await File.AppendAllLinesAsync(PathName,lines);
            }
            catch { /* Diagnostic failure must not affect message delivery. */ }
        }
    }
    internal static void Receive(IEnumerable<ChatMessage> messages,string stage,long revision=0,bool initial=false)
    {
        foreach(var message in messages.TakeLast(64))
        {
            lock(Gate)
            {
                var key=(message.ChannelId,message.Id);
                if(Seen.ContainsKey(key))continue;
                while(Order.Count>=1024)Seen.Remove(Order.Dequeue());
                Seen[key]=new(Stopwatch.GetTimestamp());Order.Enqueue(key);
            }
            if(stage=="client-receive") Observed?.Invoke(new(message.ChannelId,message.Id,"received",0,DateTimeOffset.UtcNow,initial,null,null));
            var age=(DateTimeOffset.UtcNow-message.Timestamp).TotalMilliseconds;
            Write(stage,$"channel={message.ChannelId} message={message.Id} revision={revision} initial={initial} textOnly={message.Media.Count==0 && message.Forward is null} messageUtc={message.Timestamp:O} ageMsClockDependent={age.ToString("F1",CultureInfo.InvariantCulture)}");
        }
    }
    internal static void Stage(IEnumerable<(ulong Channel,ulong Id)> messages,int bit,string stage,string flags="")
    {
        foreach(var key in messages.TakeLast(64))
        {
            long tick;
            lock(Gate)
            {
                if(!Seen.TryGetValue(key,out var state)||(state.Stages&bit)!=0)continue;
                state.Stages|=bit;tick=state.Tick;
            }
            if(stage is "ui-dispatch" or "surface-submitted") Observed?.Invoke(new(key.Channel,key.Id,stage,Stopwatch.GetElapsedTime(tick).TotalMilliseconds,DateTimeOffset.UtcNow,false,
                flags.Contains("visible=True")?true:flags.Contains("visible=False")?false:null,
                flags.Contains("pinned=True")?true:flags.Contains("pinned=False")?false:null));
            Write(stage,$"channel={key.Channel} message={key.Id} receiveElapsedMs={Stopwatch.GetElapsedTime(tick).TotalMilliseconds.ToString("F1",CultureInfo.InvariantCulture)} {flags}");
        }
    }
}
