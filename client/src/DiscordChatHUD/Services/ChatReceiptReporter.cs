using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;
namespace DiscordChatHUD.Services;
internal sealed record ChatReceipt(ulong Channel,ulong Message,string Stage,double ElapsedMs,DateTimeOffset ObservedUtc,bool Initial,bool? Visible,bool? Pinned);
internal sealed record ChatReceiptBatch(int Version,ChatReceipt[] Events);
internal sealed class ChatReceiptReporter : IAsyncDisposable
{
 private readonly Channel<ChatReceipt> _queue=Channel.CreateBounded<ChatReceipt>(new BoundedChannelOptions(256){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
 private readonly HttpClient _http;private readonly CancellationTokenSource _stop=new();private readonly ulong _channel;private readonly Task _run;
 internal volatile bool Enabled;
 internal ChatReceiptReporter(Uri server,string token,ulong channel,HttpMessageHandler? handler=null)
 {
 _channel=channel;_http=handler is null?RelayTransport.Create(server,TimeSpan.FromSeconds(3)):new HttpClient(handler){BaseAddress=server,Timeout=TimeSpan.FromSeconds(3)};
 _http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
 ChatTiming.Observed+=Observe;_run=Run();
 }
 private void Observe(ChatReceipt item){if(Enabled && item.Channel==_channel)_queue.Writer.TryWrite(item);}
 private async Task Run()
 {
 try {while(!_stop.IsCancellationRequested){await Task.Delay(3000,_stop.Token);var items=new List<ChatReceipt>();while(items.Count<64&&_queue.Reader.TryRead(out var item))items.Add(item);if(items.Count==0)continue;
 try{using var response=await _http.PostAsJsonAsync("v1/chat-receipts",new ChatReceiptBatch(typeof(ChatTiming).Assembly.GetName().Version!.Build,items.ToArray()),RelayProtocol.Json,_stop.Token);
 if(response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)Enabled=false;
 if(!response.IsSuccessStatusCode)await Task.Delay(15000,_stop.Token);
 }catch(OperationCanceledException)when(_stop.IsCancellationRequested){break;}catch{await Task.Delay(15000,_stop.Token);}
 }}catch(OperationCanceledException){}
 }
 public async ValueTask DisposeAsync(){ChatTiming.Observed-=Observe;_stop.Cancel();await _run;_http.Dispose();_stop.Dispose();}
}
