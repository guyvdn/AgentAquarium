using AgentAquarium.Domain;
using AgentAquarium.Sources.ClaudeCode;

namespace AgentAquarium.Tests;

// Fixtures are trimmed copies of real Claude Code 2.1 transcript lines.
public class TranscriptParserTests
{
    private const string Ts = "\"timestamp\":\"2026-10-15T07:18:57.782Z\"";

    [Fact]
    public void Assistant_tool_use_reports_context_tool_call_and_working()
    {
        var line = TranscriptParser.Parse($$$"""
            {"type":"assistant","cwd":"D:\\repo","gitBranch":"main",{{{Ts}}},"message":{"role":"assistant",
             "content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{}}],"stop_reason":"tool_use",
             "usage":{"input_tokens":2,"cache_creation_input_tokens":37927,"cache_read_input_tokens":100,"output_tokens":86} }}
            """);

        Assert.Equal(38115, Assert.Single(line.Observations.OfType<ContextObserved>()).Tokens);
        Assert.Equal("Read", Assert.Single(line.Observations.OfType<ToolCallObserved>()).ToolName);
        Assert.Equal(AgentStatus.Working, Assert.Single(line.Observations.OfType<StatusObserved>()).Status);
        Assert.Equal(["toolu_1"], line.ToolUseIds);
        Assert.Equal("D:\\repo", line.Cwd);
        Assert.Equal("main", line.GitBranch);
    }

    [Fact]
    public void Assistant_end_turn_is_idle()
    {
        var line = TranscriptParser.Parse($$$"""
            {"type":"assistant",{{{Ts}}},"message":{"content":[{"type":"text","text":"done"}],"stop_reason":"end_turn"}}
            """);

        Assert.Equal(AgentStatus.Idle, Assert.Single(line.Observations.OfType<StatusObserved>()).Status);
    }

    [Fact]
    public void Failed_tool_result_is_an_error_and_leaves_status_alone()
    {
        var line = TranscriptParser.Parse($$$"""
            {"type":"user",{{{Ts}}},"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","is_error":true,"content":"denied"}]}}
            """);

        Assert.Single(line.Observations.OfType<ToolErrorObserved>());
        Assert.Empty(line.Observations.OfType<StatusObserved>());
    }

    [Fact]
    public void A_question_to_the_user_is_waiting_until_answered()
    {
        var waits = new Dictionary<string, string>(StringComparer.Ordinal);
        var observations = new List<AgentObservation>();

        var asked = TranscriptParser.Parse($$$"""
            {"type":"assistant",{{{Ts}}},"message":{"content":[{"type":"tool_use","id":"toolu_q","name":"AskUserQuestion","input":{}}],"stop_reason":"tool_use"}}
            """);
        ClaudeCodeSource.TrackWaits(waits, asked, observations);

        var status = Assert.Single(observations.OfType<StatusObserved>());
        Assert.Equal((AgentStatus.Waiting, "a question"), (status.Status, status.WaitingFor));
        Assert.Equal("toolu_q", Assert.Single(waits).Key);
        observations.Clear();

        var answered = TranscriptParser.Parse($$$"""
            {"type":"user",{{{Ts}}},"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_q","content":"Yes"}]}}
            """);
        ClaudeCodeSource.TrackWaits(waits, answered, observations);

        Assert.Empty(waits);
        Assert.Equal(AgentStatus.Working, Assert.Single(observations.OfType<StatusObserved>()).Status);
    }

    [Fact]
    public void A_tool_called_alongside_a_question_does_not_answer_it()
    {
        var waits = new Dictionary<string, string>(StringComparer.Ordinal);
        var observations = new List<AgentObservation>();

        string[] lines =
        [
            $$$"""{"type":"assistant",{{{Ts}}},"message":{"content":[{"type":"tool_use","id":"toolu_q","name":"AskUserQuestion","input":{}}],"stop_reason":null}}""",
            $$$"""{"type":"assistant",{{{Ts}}},"message":{"content":[{"type":"tool_use","id":"toolu_r","name":"Read","input":{}}],"stop_reason":"tool_use"}}""",
            $$$"""{"type":"user",{{{Ts}}},"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_r","content":"..."}]}}""",
        ];
        foreach (var json in lines)
            ClaudeCodeSource.TrackWaits(waits, TranscriptParser.Parse(json), observations);

        Assert.All(observations.OfType<StatusObserved>(), s => Assert.Equal(AgentStatus.Waiting, s.Status));
        Assert.Equal("toolu_q", Assert.Single(waits).Key);
    }

    [Fact]
    public void A_turn_that_moves_on_drops_an_unanswered_question()
    {
        var waits = new Dictionary<string, string>(StringComparer.Ordinal) { ["toolu_q"] = "plan approval" };
        var observations = new List<AgentObservation>();

        var next = TranscriptParser.Parse($$$"""
            {"type":"assistant",{{{Ts}}},"message":{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}}
            """);
        ClaudeCodeSource.TrackWaits(waits, next, observations);

        Assert.Empty(waits);
        Assert.Equal(AgentStatus.Idle, Assert.Single(observations.OfType<StatusObserved>()).Status);
    }

    [Fact]
    public void Prompt_starts_work()
    {
        var line = TranscriptParser.Parse($$$"""
            {"type":"user",{{{Ts}}},"message":{"role":"user","content":[{"type":"text","text":"What applications still run on SQL?"}]}}
            """);

        Assert.Equal(AgentStatus.Working, Assert.Single(line.Observations.OfType<StatusObserved>()).Status);
    }

    [Fact]
    public void Subagent_handback_ends_the_turn()
    {
        var line = TranscriptParser.Parse($$$"""
            {"type":"assistant",{{{Ts}}},"message":{"content":[{"type":"tool_use","id":"toolu_9","name":"SubagentHandback","input":{"message":"report"} }],"stop_reason":"tool_use"}}
            """);

        Assert.Equal(AgentStatus.Idle, Assert.Single(line.Observations.OfType<StatusObserved>()).Status);
    }

    [Fact]
    public void Interrupt_is_idle()
    {
        var line = TranscriptParser.Parse($$$"""
            {"type":"user",{{{Ts}}},"message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}
            """);

        Assert.Equal(AgentStatus.Idle, Assert.Single(line.Observations.OfType<StatusObserved>()).Status);
    }

    [Fact]
    public void Compact_boundary_reports_post_compaction_size()
    {
        var line = TranscriptParser.Parse($$$"""
            {"type":"system","subtype":"compact_boundary",{{{Ts}}},"compactMetadata":{"trigger":"manual","preTokens":286801,"postTokens":13108}}
            """);

        var c = Assert.Single(line.Observations.OfType<CompactionObserved>());
        Assert.Equal((286801, 13108), (c.PreTokens, c.PostTokens));
    }

    [Fact]
    public void Ai_title_sets_title()
    {
        var line = TranscriptParser.Parse("""{"type":"ai-title","aiTitle":"SQL-based applications","sessionId":"x"}""");

        Assert.Equal("SQL-based applications", line.Title);
        Assert.Empty(line.Observations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("""{"type":"queue-operation"}""")]
    [InlineData("""{"type":"assistant","message":{}}""")]
    public void Unknown_or_broken_lines_are_ignored(string json) =>
        Assert.Empty(TranscriptParser.Parse(json).Observations);

    [Theory]
    [InlineData(@"D:\GuyVdN\AgentAquarium", "AgentAquarium")]
    [InlineData(@"D:\Euricom\internal\devops\.claude\worktrees\lunch-linux-postgres", "devops")]
    [InlineData(@"C:\Users\guyvd\.t3\worktrees\internal-devops\t3code-462b73a5", "internal-devops")]
    [InlineData("/home/me/src/app/", "app")]
    public void Project_name_looks_past_worktree_folders(string cwd, string expected) =>
        Assert.Equal(expected, ClaudeCodeSource.ProjectName(cwd));

    [Theory]
    [InlineData("sdk-ts", @"C:\Users\guyvd\.t3\worktrees\internal-devops\t3code-462b73a5", "T3 Code")]
    [InlineData("cli", @"D:\GuyVdN\AgentAquarium", "CLI")]
    [InlineData("sdk-ts", @"D:\repo", "SDK")]
    [InlineData(null, @"D:\repo", null)]
    public void Host_names_where_the_session_runs(string? entrypoint, string cwd, string? expected) =>
        Assert.Equal(expected, ClaudeCodeSource.Host(entrypoint, cwd));
}
