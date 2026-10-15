using System.Text;
using AgentAquarium.Sources.ClaudeCode;

namespace AgentAquarium.Tests;

public sealed class JsonlTailTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"jsonltail-{Guid.NewGuid():N}.jsonl");

    public void Dispose() => File.Delete(_path);

    private void Append(string text) => Append(Encoding.UTF8.GetBytes(text));

    private void Append(byte[] bytes)
    {
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
    }

    [Fact]
    public void Partial_line_is_held_back_until_completed()
    {
        var tail = new JsonlTail(_path);
        Append("{\"a\":1}\n{\"b\":");

        Assert.Equal(["{\"a\":1}"], tail.ReadNewLines());

        Append("2}\n");
        Assert.Equal(["{\"b\":2}"], tail.ReadNewLines());
        Assert.Empty(tail.ReadNewLines());
    }

    [Fact]
    public void Multibyte_character_split_across_reads_survives()
    {
        var tail = new JsonlTail(_path);
        var bytes = Encoding.UTF8.GetBytes("{\"fish\":\"🐟\"}\n");
        var split = Array.IndexOf(bytes, (byte)0xF0) + 2; // inside the 4-byte emoji

        Append(bytes[..split]);
        Assert.Empty(tail.ReadNewLines());
        Append(bytes[split..]);

        Assert.Equal(["{\"fish\":\"🐟\"}"], tail.ReadNewLines());
    }

    [Fact]
    public void Crlf_line_endings_are_trimmed()
    {
        var tail = new JsonlTail(_path);
        Append("one\r\ntwo\r\n");

        Assert.Equal(["one", "two"], tail.ReadNewLines());
    }

    [Fact]
    public void Truncated_file_is_read_again_from_the_start()
    {
        var tail = new JsonlTail(_path);
        Append("old line that is long\n");
        tail.ReadNewLines();

        File.WriteAllText(_path, "new\n");

        Assert.Equal(["new"], tail.ReadNewLines());
    }

    [Fact]
    public void Large_backlog_is_read_in_chunks()
    {
        var tail = new JsonlTail(_path, maxChunkBytes: 8);
        Append("aaaa\nbbbb\ncccc\n");

        var lines = new List<string>(tail.ReadNewLines());
        Assert.True(tail.Behind);
        while (tail.Behind)
            lines.AddRange(tail.ReadNewLines());

        Assert.Equal(["aaaa", "bbbb", "cccc"], lines);
    }

    [Fact]
    public void Missing_file_throws_for_the_caller_to_handle()
    {
        var tail = new JsonlTail(_path);

        Assert.Throws<FileNotFoundException>(() => tail.ReadNewLines());
    }
}
