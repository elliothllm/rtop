using System.Text;

namespace Rtop.Logs;

/// <summary>Reads the tail of a log file without pulling a multi-megabyte watcher log into memory.</summary>
public static class LogTail
{
    private const int ChunkSize = 64 * 1024;

    public static IReadOnlyList<string> Read(string path, int lines)
    {
        if (!File.Exists(path))
        {
            return [$"No file at {path}."];
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new List<byte>();
            var newlines = 0;
            var position = stream.Length;

            while (position > 0 && newlines <= lines)
            {
                var take = (int)Math.Min(ChunkSize, position);
                position -= take;
                stream.Seek(position, SeekOrigin.Begin);

                var chunk = new byte[take];
                stream.ReadExactly(chunk, 0, take);
                buffer.InsertRange(0, chunk);
                newlines += chunk.Count(value => value == (byte)'\n');
            }

            var split = Encoding.UTF8.GetString([.. buffer]).Split('\n');
            var trimmed = split.Length <= lines ? split : split[^lines..];

            return [.. trimmed.Select(line => line.TrimEnd('\r'))];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [$"Could not read {path}: {exception.Message}"];
        }
    }
}
