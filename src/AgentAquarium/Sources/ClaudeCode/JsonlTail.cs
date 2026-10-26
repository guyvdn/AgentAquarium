using System.Text;

namespace AgentAquarium.Sources.ClaudeCode;

/// <summary>
/// Follows an append-only JSONL file that another process is writing. Returns only complete
/// lines: a trailing partial line is held back until its newline arrives. Splitting on the
/// <c>\n</c> byte is safe for UTF-8, because that byte never occurs inside a multi-byte sequence.
/// </summary>
internal sealed class JsonlTail(string path, int maxChunkBytes = JsonlTail.DefaultMaxChunkBytes)
{
    public const int DefaultMaxChunkBytes = 1024 * 1024;

    private byte[] _partial = [];

    public string Path { get; } = path;

    /// <summary>Bytes consumed so far, including a held-back partial line.</summary>
    public long Offset { get; private set; }

    /// <summary>True when the last read stopped at the chunk or the caller's limit and more is waiting.</summary>
    public bool Behind { get; private set; }

    /// <summary>
    /// Reads at most one chunk of newly appended data. A file that shrank was rewritten,
    /// so it is read again from the start. Throws on I/O errors; the caller decides what to skip.
    /// </summary>
    /// <param name="limit">Reads no further than this offset; the rest waits for the next call.</param>
    public IReadOnlyList<string> ReadNewLines(long limit = long.MaxValue)
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;

        if (length < Offset)
        {
            Offset = 0;
            _partial = [];
        }

        var available = Math.Max(0, Math.Min(length, limit) - Offset);
        if (available == 0)
        {
            Behind = false;
            return [];
        }

        var chunk = new byte[Math.Min(available, maxChunkBytes)];
        stream.Seek(Offset, SeekOrigin.Begin);
        var read = stream.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false);
        Offset += read;
        Behind = Offset < Math.Min(length, limit);

        return Split(chunk.AsSpan(0, read));
    }

    private List<string> Split(ReadOnlySpan<byte> chunk)
    {
        var lines = new List<string>();
        var data = _partial.Length == 0 ? chunk : [.. _partial, .. chunk];
        int newline;
        while ((newline = data.IndexOf((byte)'\n')) >= 0)
        {
            var line = data[..newline];
            if (line.Length > 0 && line[^1] == (byte)'\r')
                line = line[..^1];
            lines.Add(Encoding.UTF8.GetString(line));
            data = data[(newline + 1)..];
        }
        _partial = data.ToArray();
        return lines;
    }
}
