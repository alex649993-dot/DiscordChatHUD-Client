using DiscordChatHUD.Logging;
using System.Text.Json;
using System.Net.Http.Json;
using System.Net.Http.Headers;
namespace DiscordChatHUD.Services;
internal static class ConnectionDiagnostics
{
    internal static T At<T>(string stage, Func<T> action)
    {
        try { return action(); }
        
        catch (Exception ex) { if (!ex.Data.Contains("HUD.ConnectionStage")) ex.Data["HUD.ConnectionStage"] = stage; throw; }
    }
    internal static string Describe(Exception error, string fallbackStage)
    {
        var stage = error.Data["HUD.ConnectionStage"] as string ?? fallbackStage;
        var cause = error;
        var kind = cause switch {
            OperationCanceledException => "TIMEOUT",
            JsonException => "JSON",
            InvalidDataException => "DATA",
            UnauthorizedAccessException => "ACCESS",
            IOException => "FILE",
            HttpRequestException => "HTTP",
            InvalidOperationException => "STATE",
            _ => "UNEXPECTED"
        };
        var http = cause is HttpRequestException request && request.StatusCode is {} status ? $"-{(int)status}" : "";
        var code = $"{stage}-{kind}{http}";
        error.Data["HUD.ConnectionCode"] = code;
        // Never include exception text, response bodies, account IDs, paths or tokens.
        AppLog.Warn($"계정 연결 오류 code={code} type={cause.GetType().Name} hresult={cause.HResult:X8}");
        var hint = kind switch {
            "TIMEOUT" => "서버 응답 시간이 초과되었습니다. 잠시 후 다시 시도해주세요.",
            "FILE" or "ACCESS" => "PC 설정 파일을 읽거나 저장하지 못했습니다. 폴더 권한과 다른 실행 창을 확인해주세요.",
            "JSON" or "DATA" => "연결에 필요한 데이터의 형식을 확인하지 못했습니다.",
            "HTTP" => "서버 요청을 완료하지 못했습니다. 잠시 후 다시 시도해주세요.",
            _ => "계정 연결을 완료하지 못했습니다."
        };
        return $"{hint}\n오류 코드: {code}\n저장된 로그인은 유지됩니다. 이 화면을 운영자에게 알려주세요.";
    }
    internal static async Task Report(Uri server, string? token, Exception error)
    {
        if (string.IsNullOrEmpty(token) || error.Data["HUD.ConnectionCode"] is not string code) return;
        try
        {
            using var http = RelayTransport.Create(server, TimeSpan.FromSeconds(3), 4096);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.PostAsJsonAsync("v1/client-diagnostic", new { code });
        }
        catch { /* Reporting is best effort and never changes login/session state. */ }
    }}
