using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Buffers;
using System.Text;
using System.Text.Json;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

// DiscordGatewayClient — Gateway 웹소켓 접속·수신, heartbeat, identify, 전송.
internal sealed partial class DiscordGatewayClient
{

    private async Task ConnectAndReceiveAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", "DiscordChatHUD-CSharp/1.0");
        _socket = socket;
        _sequence = null;
        await socket.ConnectAsync(
            new Uri($"wss://gateway.discord.gg/?v={GatewayVersion}&encoding=json"),
            cancellationToken).ConfigureAwait(false);

        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeat = null;
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var payload = await ReceivePayloadAsync(socket, cancellationToken).ConfigureAwait(false);
                if (payload is null)
                {
                    if((int?)socket.CloseStatus==4014 && _sessionEnabled)
                    {
                        _sessionEnabled=false;
                        Volatile.Write(ref _sessionPresence,new(GtaSessionPresence.TrackedHostId,GtaSessionPresence.TrackedHostName,null,DateTimeOffset.UtcNow));
                        Volatile.Write(ref _secondarySessionPresence,new(GtaSessionPresence.SecondaryHostId,GtaSessionPresence.SecondaryHostName,null,DateTimeOffset.UtcNow));
                        AppLog.Warn("세션 인원 표시: Discord 개발자 포털 Presence Intent를 켠 뒤 서버를 재시작하세요. 채팅 연결은 기존 권한으로 재시도합니다.");
                    }
                    throw new WebSocketException("Discord Gateway가 연결을 종료함.");
                }
                var root = payload.Document.RootElement;
                var op = root.TryGetProperty("op", out var opElement) ? opElement.GetInt32() : -1;
                if (root.TryGetProperty("s", out var sequenceElement)
                    && sequenceElement.ValueKind == JsonValueKind.Number
                    && sequenceElement.TryGetInt64(out var sequence))
                {
                    _sequence = sequence;
                }

                switch (op)
                {
                    case 10:
                    {
                        var interval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                        _heartbeatAcknowledged = true;
                        heartbeat = HeartbeatLoopAsync(interval, connectionCancellation.Token);
                        await SendIdentifyAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case 11:
                        _heartbeatAcknowledged = true;
                        break;
                    case 1:
                        await SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case 7:
                        throw new WebSocketException("Discord Gateway reconnect 요청");
                    case 9:
                        await Task.Delay(Random.Shared.Next(1000, 5000), cancellationToken).ConfigureAwait(false);
                        await SendIdentifyAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case 0:
                        await DispatchSharedAsync(root, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        finally
        {
            connectionCancellation.Cancel();
            if (heartbeat is not null)
            {
                try { await heartbeat.ConfigureAwait(false); } catch { }
            }
            if (ReferenceEquals(_socket, socket)) _socket = null;
        }
    }

    private async Task HeartbeatLoopAsync(int intervalMilliseconds, CancellationToken cancellationToken)
    {
        var firstDelay = Random.Shared.Next(0, Math.Max(1, intervalMilliseconds));
        await Task.Delay(firstDelay, cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_heartbeatAcknowledged)
            {
                try { _socket?.Abort(); } catch { }
                return;
            }
            _heartbeatAcknowledged = false;
            await SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(intervalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SendIdentifyAsync(CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            op = 2,
            d = new
            {
                token = _token,
                intents = Intents | (_sessionEnabled ? 256 : 0),
                properties = new Dictionary<string, string>
                {
                    ["os"] = "windows",
                    ["browser"] = "DiscordChatHUD",
                    ["device"] = "DiscordChatHUD"
                },
                large_threshold = 250
            }
        });
        return SendTextAsync(payload, cancellationToken);
    }

    private Task SendHeartbeatAsync(CancellationToken cancellationToken)
        => SendTextAsync(JsonSerializer.Serialize(new { op = 1, d = _sequence }), cancellationToken);

    private async Task SendTextAsync(string payload, CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new WebSocketException("Gateway socket unavailable.");
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    internal sealed class GatewayPayload : IDisposable
    {
        private byte[]? _buffer;
        public JsonDocument Document { get; }

        public GatewayPayload(byte[] buffer, int length)
        {
            Document = JsonDocument.Parse(buffer.AsMemory(0, length));
            _buffer = buffer;
        }

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is null) return;
            Document.Dispose();
            GatewayBuffers.Return(buffer);
        }
    }

    internal static async Task<GatewayPayload?> ReceivePayloadAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[]? buffer = GatewayBuffers.Rent(16 * 1024);
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length >= MaxGatewayPayloadBytes) throw new InvalidDataException("Gateway payload too large.");
                    var expanded = GatewayBuffers.Rent(Math.Min(MaxGatewayPayloadBytes, buffer.Length * 2));
                    buffer.AsSpan(0, length).CopyTo(expanded);
                    GatewayBuffers.Return(buffer);
                    buffer = expanded;
                }
                var result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException("Unexpected binary Gateway payload.");
                length += result.Count;
                if (!result.EndOfMessage) continue;
                var payload = new GatewayPayload(buffer, length);
                buffer = null; // The document owns the UTF-8 bytes until dispatch completes.
                return payload;
            }
        }
        finally
        {
            if (buffer is not null) GatewayBuffers.Return(buffer);
        }
    }
}
