using System.Text.Json;

namespace DiscordChatHUD.Services;
internal static class ReleaseNotes
{
    internal static UpdateReleaseNote[] Local()
    {
        using var stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("ReleaseNotes.json")!;
        return JsonSerializer.Deserialize<UpdateReleaseNote[]>(stream) ?? [];
    }
    internal static string Format(UpdateManifest manifest, int current)
    {
        var entries = (manifest.History ?? [])
            .Where(entry => entry.Version > current && entry.Version <= manifest.Version)
            .GroupBy(entry => entry.Version).Select(group => group.Last()).OrderBy(entry => entry.Version).ToList();
        if (!entries.Any(entry => entry.Version == manifest.Version))
            entries.Add(new(manifest.Version, manifest.Notes));
        return FormatEntries(entries);
    }
    internal static string FormatEntries(IEnumerable<UpdateReleaseNote> entries)
        => string.Join("\r\n\r\n", entries.Select(entry => $"Preview {entry.Version}\r\n\r\n" +
            string.Join("\r\n\r\n", entry.Notes.ReplaceLineEndings("\n").Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))));
}
