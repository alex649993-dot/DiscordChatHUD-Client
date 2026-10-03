using DiscordChatHUD.Models;
namespace DiscordChatHUD.Services;
internal sealed class SwitchableChatSource : IChatSource
{
    private readonly Func<ulong,IChatSource> _factory;
    private readonly SemaphoreSlim _gate=new(1,1);
    private IChatSource _current;
    private bool _disposed;
    internal ulong Channel {get;private set;}
    internal SwitchableChatSource(Func<ulong,IChatSource> factory,ulong channel)
    { _factory=factory;Channel=channel;_current=factory(channel);Attach(_current); }
    public event Action? MessagesChanged;
    public event Action? StatusChanged;
    private void Changed()=>MessagesChanged?.Invoke();
    private void StatusChange()=>StatusChanged?.Invoke();
    private void Attach(IChatSource s){s.MessagesChanged+=Changed;s.StatusChanged+=StatusChange;}
    private void Detach(IChatSource s){s.MessagesChanged-=Changed;s.StatusChanged-=StatusChange;}
    public string ChannelLabel=>Volatile.Read(ref _current).ChannelLabel;
    public string Status=>Volatile.Read(ref _current).Status;
    public GtaSessionPresence? SessionPresence=>Volatile.Read(ref _current).SessionPresence;
    public GtaSessionPresence? SecondarySessionPresence=>Volatile.Read(ref _current).SecondarySessionPresence;
    public bool HasSaleChannel=>Volatile.Read(ref _current).HasSaleChannel;
    public (ulong? MessageId,DateTimeOffset TimestampUtc) SaleActivity=>Volatile.Read(ref _current).SaleActivity;
    public IReadOnlyList<ChatMessage> Snapshot()=>Volatile.Read(ref _current).Snapshot();
    public IReadOnlyList<ChatMessage> SaleSnapshot()=>Volatile.Read(ref _current).SaleSnapshot();
    public void Start()=>_current.Start();
    internal async Task SwitchAsync(ulong channel)
    {
        await _gate.WaitAsync();
        try
        {
            if(_disposed||Channel==channel)return;
            var next=_factory(channel); var old=_current; Detach(old);
            try{await old.DisposeAsync();}catch{await next.DisposeAsync();throw;}
            Volatile.Write(ref _current,next);Channel=channel;Attach(next);next.Start();Changed();StatusChange();
        }
        finally{_gate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try{if(_disposed)return;_disposed=true;Detach(_current);await _current.DisposeAsync();}
        finally{_gate.Release();}
    }
}
