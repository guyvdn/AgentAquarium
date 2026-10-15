using System.Text.Json;
using AgentAquarium.Sources.ClaudeCode;
using OpenToolUse = AgentAquarium.Sources.ClaudeCode.ClaudeCodeSource.OpenToolUse;

namespace AgentAquarium.Tests;

public class PermissionMatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static PermissionRequest Asked(string tool, string input, int atSeconds) =>
        new("s", tool, Json(input), null, T0.AddSeconds(atSeconds));

    [Fact]
    public void Parallel_calls_of_one_tool_match_on_their_arguments()
    {
        Dictionary<string, OpenToolUse> open = new(StringComparer.Ordinal)
        {
            ["toolu_a"] = new("Bash", T0, Json("""{"command":"ls"}""")),
            ["toolu_b"] = new("Bash", T0, Json("""{"command":"rm -rf out"}""")),
        };

        Assert.Equal("toolu_b", ClaudeCodeSource.PickToolCall(open, Asked("Bash", """{"command":"rm -rf out"}""", 5)));
    }

    [Fact]
    public void A_call_made_after_the_prompt_is_not_the_one_it_is_about()
    {
        // The prompted call was answered and its result read before the hook's post was; this one came later.
        Dictionary<string, OpenToolUse> open = new(StringComparer.Ordinal) { ["toolu_later"] = new("Bash", T0.AddMilliseconds(5_500), Json("{}")) };

        Assert.Null(ClaudeCodeSource.PickToolCall(open, Asked("Bash", "{}", 5)));
    }

    [Fact]
    public void Without_matching_arguments_only_the_one_open_call_of_that_tool_is_taken()
    {
        Dictionary<string, OpenToolUse> open = new(StringComparer.Ordinal)
        {
            ["toolu_read"] = new("Read", T0, Json("{}")),
            ["toolu_write"] = new("Write", T0.AddSeconds(1), Json("""{"file_path":"a"}""")),
        };

        Assert.Equal("toolu_write", ClaudeCodeSource.PickToolCall(open, Asked("Write", """{"file_path":"edited by a hook"}""", 5)));
    }

    [Fact]
    public void Without_matching_arguments_two_open_calls_are_too_close_to_call()
    {
        // One may be a long command that was allowed by a rule and is running, not waiting.
        Dictionary<string, OpenToolUse> open = new(StringComparer.Ordinal)
        {
            ["toolu_a"] = new("Bash", T0, Json("""{"command":"dotnet test"}""")),
            ["toolu_b"] = new("Bash", T0, Json("""{"command":"rm -rf out"}""")),
        };

        Assert.Null(ClaudeCodeSource.PickToolCall(open, Asked("Bash", """{"command":"edited by a hook"}""", 5)));
    }
}
