using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using DiscordChatHUD.Models;
namespace DiscordChatHUD.Services;

internal sealed record NightclubObservation(string Key, string Name, int Stock, int Capacity);
internal static class NightclubScreenReader
{
    private static readonly Dictionary<string, string> Keys = new()
    {
        ["cargo"]="nightclub_cargo", ["sporting"]="nightclub_sporting",
        ["imports"]="nightclub_south_american", ["pharma"]="nightclub_pharmaceutical",
        ["organic"]="nightclub_organic", ["printing"]="nightclub_printing", ["cash"]="nightclub_cash"
    };
    internal static IReadOnlyList<NightclubObservation> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<NightclubObservation>();
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (!Keys.TryGetValue(item.GetProperty("key").GetString() ?? "", out var key)) throw new InvalidDataException("알 수 없는 상품입니다.");
            var profile = BusinessSupplyCatalog.NightclubProfiles.Single(p => p.Key == key);
            var stock = item.GetProperty("stock").GetInt32(); var capacity = item.GetProperty("capacity").GetInt32();
            if (stock < 0 || stock > capacity || capacity != profile.StockCapacity || result.Any(r => r.Key == key))
                throw new InvalidDataException("상품 수량 또는 창고 용량이 현재 지원하는 구성과 다릅니다.");
            result.Add(new(key, profile.Name, stock, capacity));
        }
        if (result.Count != Keys.Count) throw new InvalidDataException("7개 상품을 모두 확인하지 못했습니다.");
        return result;
    }
    internal sealed record Inspection(string? Key, IReadOnlyList<NightclubObservation> Items);
    internal static async Task<IReadOnlyList<NightclubObservation>> ReadAsync(Bitmap frame, CancellationToken ct, bool probe = false)
    {
        var result = await InspectAsync(frame, ct);
        if (result.Key != "nightclub" && !probe) throw new InvalidDataException("나이트클럽 상품 판매 화면이 아닙니다.");
        return result.Items;
    }
    internal static string? IdentifyBusiness(string text)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"[^가-힣a-z]", "");
        var candidates = new Dictionary<string,string[]>
        {
            ["bunker"] = ["벙커", "disruptionlogistics"],
            ["acid_lab"] = ["lsd제조", "lsd연구소", "acidlab"],
            ["cocaine"] = ["코카인", "cocainelockup"],
            ["meth"] = ["필로폰", "methamphetamine", "methlab"],
            ["cash"] = ["위조지폐", "counterfeitcash"],
            ["weed"] = ["대마초", "weedfarm"],
            ["documents"] = ["위조서류", "서류위조", "문서위조", "위조문서", "documentforgery"]
        };
        var matches = candidates.Where(pair => pair.Value.Any(normalized.Contains)).Select(pair => pair.Key).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    internal static async Task<Inspection> InspectAsync(Bitmap frame, CancellationToken ct)
    {
        using var session = new Session();
        return await session.InspectAsync(frame, ct);
    }

    /// <summary>
    /// PowerShell writes its error stream as CLIXML with progress records first
    /// ("처음 사용하기 위해 모듈을 준비하는 중"), which used to fill the whole log
    /// excerpt. Keep only the error text.
    /// </summary>
    internal static string OcrFailureSummary(string diagnostic)
    {
        var errors = System.Text.RegularExpressions.Regex.Matches(diagnostic, "<S S=\"Error\">(.*?)</S>")
            .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups[1].Value)
                .Replace("_x000D__x000A_", " ").Replace("_x000A_", " ").Trim())
            .Where(text => text.Length > 0)
            .ToList();
        var text = errors.Count > 0 ? errors[0] : diagnostic.Trim();
        return text.Length <= 300 ? text : text[..300];
    }

    internal sealed record OcrTiming(double PreparationMs, double EncodeMs, double SendMs,
        double ResponseMs, double DecodeMs, double RecognizeMs, double ParseMs, int Attempts, double StartupMs);

    internal sealed class Session : IDisposable
    {
        private readonly object lifecycle = new();
        private Process? process;
        private Task<string>? errors;
        private Task? preparation;
        private bool disposed;
        private double startupMs;
        internal int? ProcessId { get { lock (lifecycle) return process?.Id; } }
        internal OcrTiming LastTiming { get; private set; } = new(0,0,0,0,0,0,0,0,0);

        // Used by F7 and the bounded GTA-start warm-up. Capture can overlap preparation.
        // Sharing the one readiness task also prevents concurrent duplicate launches.
        internal Task PrepareAsync(CancellationToken ct)
        {
            lock (lifecycle)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return preparation ??= Task.Run(() => PrepareCoreAsync(ct), ct);
            }
        }

        private async Task PrepareCoreAsync(CancellationToken ct)
        {
            var elapsed = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                Process worker;
                lock (lifecycle)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    Start();
                    worker = process!;
                }
                var ready = await worker.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (ready is null) throw new IOException("문자 인식 준비 중 프로그램이 종료되었습니다. "
                    + OcrFailureSummary(errors is null ? "" : await errors.WaitAsync(timeout.Token).ConfigureAwait(false)));
                using var document = JsonDocument.Parse(ready);
                if (!document.RootElement.TryGetProperty("ready", out var value) || !value.GetBoolean())
                    throw new IOException("문자 인식 준비 응답을 확인하지 못했습니다.");
                startupMs = elapsed.Elapsed.TotalMilliseconds;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal async Task<Inspection> InspectAsync(Bitmap frame, CancellationToken ct)
        {
            LastTiming = new(0,0,0,0,0,0,0,0,startupMs);
            Inspection? last = null;
            InvalidDataException? failure = null;
            async Task<Inspection?> Try(Bitmap image)
            {
                try { last = await InspectOnceAsync(image, ct); if (last.Key is not null) return last; }
                catch (InvalidDataException ex) { failure = ex; }
                return null;
            }
            if (await Try(frame) is { } first) return first;
            ct.ThrowIfCancellationRequested();
            using var normalized = new Bitmap(2560, 1440);
            if (frame.Width != 2560 || frame.Height != 1440)
            {
                using (var graphics = Graphics.FromImage(normalized))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(frame, new Rectangle(0, 0, 2560, 1440));
                }
                if (await Try(normalized) is { } resized) return resized;
            }
            if (frame.Width / (double)frame.Height > 2)
            {
                var width = (int)Math.Round(frame.Height * 16d / 9);
                using (var graphics = Graphics.FromImage(normalized))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(frame, new Rectangle(0, 0, 2560, 1440),
                        new Rectangle((frame.Width - width) / 2, 0, width, frame.Height), GraphicsUnit.Pixel);
                }
                if (await Try(normalized) is { } centered) return centered;
            }
            if (failure is not null) throw failure;
            return last ?? new(null, []);
        }

        // A small generated frame warms the OCR call path without capturing the user's screen.
        // Call exactly once and dispose the session; no idle helper is retained.
        internal async Task WarmUpAsync(CancellationToken ct)
        {
            await PrepareAsync(ct).ConfigureAwait(false);
            using var frame = new Bitmap(64, 32);
            using (var graphics = Graphics.FromImage(frame)) graphics.Clear(Color.Black);
            _ = await InspectOnceAsync(frame, ct).ConfigureAwait(false);
        }

        private void Start()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is not null) return;
            using var stream = typeof(NightclubScreenReader).Assembly.GetManifestResourceStream("NightclubOcr.ps1")!;
            using var reader = new StreamReader(stream);
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(reader.ReadToEnd())));
            process = Process.Start(psi) ?? throw new IOException("화면 판독을 시작하지 못했습니다.");
            errors = process.StandardError.ReadToEndAsync();
        }

        private async Task<Inspection> InspectOnceAsync(Bitmap frame, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var ready = PrepareAsync(timeout.Token);
            var phase = Stopwatch.StartNew();
            double prepare = 0, encode = 0, send = 0, responseMs = 0, decode = 0, recognize = 0, parse = 0;
            try
            {
                // Encoding still overlaps preparation, even for callers without pre-capture preparation.
                using var image = new MemoryStream();
                frame.Save(image, ImageFormat.Png);
                var encoded = Convert.ToBase64String(image.GetBuffer(), 0, (int)image.Length);
                encode = phase.Elapsed.TotalMilliseconds;
                phase.Restart();
                try { await ready.ConfigureAwait(false); }
                finally { prepare = phase.Elapsed.TotalMilliseconds; }
                timeout.Token.ThrowIfCancellationRequested();
                Process worker;
                lock (lifecycle) { ObjectDisposedException.ThrowIf(disposed, this); worker = process!; }
                var response = worker.StandardOutput.ReadLineAsync(timeout.Token).AsTask();
                phase.Restart();
                try
                {
                    await worker.StandardInput.WriteLineAsync(encoded.AsMemory(), timeout.Token).ConfigureAwait(false);
                    await worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                finally { send = phase.Elapsed.TotalMilliseconds; }
                phase.Restart();
                string? json;
                try { json = await response.ConfigureAwait(false); }
                finally { responseMs = phase.Elapsed.TotalMilliseconds; }
                if (json is null)
                {
                    var diagnostic = errors is null ? "" : await errors.WaitAsync(timeout.Token).ConfigureAwait(false);
                    throw new IOException("문자 인식 프로그램이 종료되었습니다. " + OcrFailureSummary(diagnostic));
                }
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("timing", out var timing))
                {
                    static double Milliseconds(JsonElement data, string name) => data.TryGetProperty(name, out var value)
                        && value.TryGetDouble(out var result) && double.IsFinite(result) ? Math.Max(0,result) : 0;
                    decode = Milliseconds(timing,"decodeMs"); recognize = Milliseconds(timing,"recognizeMs"); parse = Milliseconds(timing,"parseMs");
                }
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    DiscordChatHUD.Logging.AppLog.Info("사업장 OCR 재확인: " + OcrFailureSummary(error.GetString() ?? ""));
                    throw new InvalidDataException("상품 이름 또는 수량이 가려져 있습니다. 화면을 유지해 주세요.");
                }
                if (document.RootElement.TryGetProperty("notNightclub", out var notClub) && notClub.GetBoolean())
                {
                    var text = document.RootElement.TryGetProperty("screenText", out var screenText) ? screenText.GetString() ?? "" : "";
                    return new(IdentifyBusiness(text), []);
                }
                return new("nightclub", Parse(json));
            }
            catch (OperationCanceledException) { Dispose(); throw; }
            catch (IOException) { Dispose(); throw; }
            catch (JsonException ex) { Dispose(); throw new IOException("문자 인식 응답을 확인하지 못했습니다.", ex); }
            finally
            {
                var previous = LastTiming;
                LastTiming = new(previous.PreparationMs+prepare,previous.EncodeMs+encode,previous.SendMs+send,
                    previous.ResponseMs+responseMs,previous.DecodeMs+decode,previous.RecognizeMs+recognize,
                    previous.ParseMs+parse,previous.Attempts+1,startupMs);
            }
        }

        public void Dispose()
        {
            Process? worker;
            lock (lifecycle)
            {
                if (disposed) return;
                disposed = true;
                worker = process;
                process = null;
            }
            if (worker is null) return;
            try
            {
                try { worker.StandardInput.Close(); } catch (IOException) { }
                if (!worker.HasExited && !worker.WaitForExit(150))
                {
                    worker.Kill(entireProcessTree: true);
                    if (!worker.WaitForExit(2000))
                        DiscordChatHUD.Logging.AppLog.Warn($"문자 인식 보조 종료 확인 지연: PID {worker.Id}");
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception ex)
            { DiscordChatHUD.Logging.AppLog.Warn($"문자 인식 보조 종료 실패: {ex.NativeErrorCode}"); }
            finally { worker.Dispose(); }
        }
    }
}
