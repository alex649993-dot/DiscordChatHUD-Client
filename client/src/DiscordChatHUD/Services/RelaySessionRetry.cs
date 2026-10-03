using System.Net;
namespace DiscordChatHUD.Services;

internal static class RelaySessionRetry
{
    internal static bool Transient(HttpStatusCode status) => (int)status is 408 or 429 or >= 500 and <= 599;
    internal static async Task<HttpResponseMessage> GetAsync(HttpClient http, Action<string> status,
        CancellationToken ct, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan? requestedWait = null;
            string reason;
            try
            {
                var response = await http.GetAsync("auth/session", ct);
                if (!Transient(response.StatusCode)) return response;
                reason = "HTTP " + (int)response.StatusCode;
                requestedWait = response.Headers.RetryAfter?.Delta;
                if (response.Headers.RetryAfter?.Date is { } date) requestedWait = date - DateTimeOffset.UtcNow;
                response.Dispose();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { reason = "응답 시간 초과"; }
            catch (HttpRequestException) { reason = "네트워크 연결 오류"; }
            var seconds = requestedWait is { } wait ? Math.Clamp(wait.TotalSeconds, 1, 120) : Math.Min(15, 2 * Math.Pow(2, Math.Min(attempt, 3)));
            attempt++;
            status($"저장된 로그인으로 자동 재연결 중입니다.\n{reason} · {seconds:0}초 후 재시도 ({attempt}회)\n창을 닫으면 연결 시도가 취소됩니다.");
            await delay(TimeSpan.FromSeconds(seconds), ct);
        }
    }
}
