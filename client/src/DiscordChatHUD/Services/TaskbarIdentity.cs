using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DiscordChatHUD.Logging;

namespace DiscordChatHUD.Services;

/// <summary>
/// DiscordChatHUD.exe 와 DiscordChatHUD_Config.exe 는 같은 프로그램이지만 Windows 작업 표시줄은
/// 실행 파일 경로로 버튼을 묶는다. 자동 실행 · 업데이트 재시작이 사용자가 고정한 것과 다른 이름으로
/// 뜨면 고정 아이콘 옆에 버튼이 하나 더 생긴다. 고정된 쪽 이름으로 다시 실행해 한 버튼에 모은다.
/// </summary>
internal static class TaskbarIdentity
{
    internal const string HandoffArgument = "--taskbar-handoff";
    internal const string ConfigIdentityArgument = "--config-identity";
    private static readonly string[] ClientFiles = ["DiscordChatHUD.exe", "DiscordChatHUD_Config.exe"];

    /// <summary>
    /// 고정된 다른 이름의 실행 파일로 넘겨 실행했으면 true. 호출한 프로세스는 바로 끝내야 한다.
    /// </summary>
    internal static bool TryHandOffToPinned(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || args.Contains(HandoffArgument, StringComparer.OrdinalIgnoreCase)) return false;
            if (Environment.ProcessPath is not { } current) return false;
            var directory = AppContext.BaseDirectory;
            var pinned = PinnedClientFiles(PinFolder(), directory);
            var target = ChooseTarget(current, directory, pinned);
            if (target is null || HasDownloadMarker(target)) return false;

            var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = directory };
            foreach (var argument in HandOffArguments(Path.GetFileName(current), args)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return false;
            AppLog.Info($"작업 표시줄 고정 아이콘에 맞춰 {Path.GetFileName(target)}(으)로 다시 실행");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"작업 표시줄 고정 확인 실패 · 그대로 실행: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 지금 이름이 고정돼 있거나 아무것도 고정돼 있지 않으면 그대로 둔다. 둘 다 고정돼 있어도 그대로 둔다.
    /// </summary>
    internal static string? ChooseTarget(string currentPath, string directory, IReadOnlyCollection<string> pinnedFiles)
    {
        var currentName = Path.GetFileName(currentPath);
        if (!ClientFiles.Contains(currentName, StringComparer.OrdinalIgnoreCase)) return null;
        if (!string.Equals(Path.GetFullPath(Path.GetDirectoryName(currentPath)!).TrimEnd('\\'),
                Path.GetFullPath(directory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
        if (pinnedFiles.Count != 1) return null;
        var pinned = pinnedFiles.First();
        if (string.Equals(pinned, currentName, StringComparison.OrdinalIgnoreCase)) return null;
        var target = Path.Combine(directory, pinned);
        return File.Exists(target) ? target : null;
    }

    /// <summary>원래 이름이 정하던 동작(설정창 · 시작 상태 점검)을 인자로 옮긴다.</summary>
    internal static List<string> HandOffArguments(string currentName, IEnumerable<string> args)
    {
        var result = args
            .Where(argument => !argument.StartsWith("--cleanup-update=", StringComparison.Ordinal))
            .ToList();
        if (currentName.Contains("Config", StringComparison.OrdinalIgnoreCase))
        {
            string[] commands = ["--reset-auto", "--watch-gta", "--preview", "--auto-config", "--auto-hud", "--hud", "--config"];
            if (!result.Any(argument => commands.Contains(argument, StringComparer.OrdinalIgnoreCase))) result.Add("--config");
            result.Add(ConfigIdentityArgument);
        }
        result.Add(HandoffArgument);
        return result;
    }

    internal static string PinFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

    /// <summary>작업 표시줄에 고정된 바로 가기 중 이 설치 폴더의 클라이언트 실행 파일 이름.</summary>
    internal static IReadOnlyCollection<string> PinnedClientFiles(string pinFolder, string directory)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(pinFolder)) return found;
        var root = Path.GetFullPath(directory).TrimEnd('\\');
        foreach (var link in Directory.EnumerateFiles(pinFolder, "*.lnk"))
        {
            var target = ResolveShortcut(link);
            if (string.IsNullOrWhiteSpace(target)) continue;
            string full;
            try { full = Path.GetFullPath(target); } catch { continue; }
            var name = Path.GetFileName(full);
            if (!ClientFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (!string.Equals(Path.GetDirectoryName(full)?.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)) continue;
            found.Add(ClientFiles.First(file => file.Equals(name, StringComparison.OrdinalIgnoreCase)));
        }
        return found;
    }

    internal static string? ResolveShortcut(string path)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(path, 0);
            var buffer = new System.Text.StringBuilder(1024);
            ((IShellLinkW)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            var target = buffer.ToString();
            return target.Length == 0 ? null : target;
        }
        catch { return null; }
        finally { if (link is not null) Marshal.FinalReleaseComObject(link); }
    }

    // 처음 받은 파일이면 다른 이름으로 넘길 때 SmartScreen 경고가 뜰 수 있으니 넘기지 않는다.
    private static bool HasDownloadMarker(string path)
    {
        try { return File.Exists(path + ":Zone.Identifier"); }
        catch { return true; }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int size, IntPtr findData, uint flags);
    }
}
