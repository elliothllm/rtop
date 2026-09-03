using System.Text.Json;
using System.Text.Json.Serialization;
using Rtop.Logs;

namespace Rtop.Config;

/// <summary>
/// Optional settings. rtop works with no config at all — this only exists to widen what it looks
/// at, and to point it at a log server if you have one.
/// </summary>
public sealed class RtopConfig
{
    /// <summary>
    /// Repositories whose worktrees should always be listed, even with nothing running in them.
    /// Anything with a running process is included regardless, as is the repository you are in.
    /// </summary>
    [JsonPropertyName("repositories")] public List<string> Repositories { get; set; } = [];

    [JsonPropertyName("refreshSeconds")] public double RefreshSeconds { get; set; } = 3;

    [JsonPropertyName("logTailLines")] public int LogTailLines { get; set; } = 2000;

    /// <summary>
    /// Optional. When a process writes to a terminal there is no file to tail, so rtop can read
    /// its logs back out of Seq instead. Null means no Seq, and no attempt to reach one.
    /// </summary>
    [JsonPropertyName("seq")] public SeqOptions? Seq { get; set; }

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "rtop", "config.json");

    // Nulls are written deliberately: a `"seq": null` line is how anyone reading the file finds
    // out the option exists at all.
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static RtopConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var loaded = JsonSerializer.Deserialize<RtopConfig>(File.ReadAllText(Path));
                if (loaded is not null)
                {
                    loaded.RefreshSeconds = Math.Clamp(loaded.RefreshSeconds, 0.5, 60);
                    loaded.LogTailLines = Math.Clamp(loaded.LogTailLines, 100, 200_000);
                    return loaded;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable config must never stop the tool starting; defaults are fine.
        }

        return new RtopConfig();
    }

    /// <summary>Writes the current values out, creating the file if this is the first time.</summary>
    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
    }

    public static string Expand(string path) =>
        path.StartsWith('~')
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path.TrimStart('~').TrimStart('/'))
            : path;
}
