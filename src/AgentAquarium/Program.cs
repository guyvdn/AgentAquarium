using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using AgentAquarium.Domain;
using AgentAquarium.Sources.ClaudeCode;
using AgentAquarium.Sources.Codex;
using AgentAquarium.Sources.Demo;
using AgentAquarium.Tank;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<TankOptions>(builder.Configuration.GetSection("Tank"));
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<IAgentSink>(sp => sp.GetRequiredService<AgentRegistry>());
builder.Services.AddSingleton<UsageBoard>();
builder.Services.AddSingleton<IUsageSink>(sp => sp.GetRequiredService<UsageBoard>());

// Sources. Each one translates its own world into IAgentSink calls; add new ones here.
builder.Services.Configure<ClaudeCodeOptions>(builder.Configuration.GetSection("Sources:ClaudeCode"));
builder.Services.AddSingleton<PermissionRequests>();
builder.Services.AddHostedService<ClaudeCodeSource>();
builder.Services.AddHostedService<ClaudeUsageSource>();
builder.Services.Configure<CodexOptions>(builder.Configuration.GetSection("Sources:Codex"));
builder.Services.AddHostedService<CodexSource>();
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection("Sources:Demo"));
builder.Services.AddHostedService<DemoSource>();

var app = builder.Build();

app.UseDefaultFiles();
// No build step, so no fingerprinted file names: make the browser revalidate every file
// (a cheap 304 via ETag) instead of guessing a lifetime and running stale modules.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapGet("/api/agents", (AgentRegistry registry) => registry.Snapshot());

app.MapGet("/api/usage", (UsageBoard usage) => usage.Current is { } current ? Results.Ok(current) : Results.NoContent());

app.MapGet("/api/usages", (UsageBoard usage) => usage.Snapshot());

// Claude Code's PermissionRequest hook, from the plugin in plugin/. An empty 2xx leaves the decision to the user.
app.MapPost("/api/hooks/permission-request", (PermissionRequestHook hook, PermissionRequests requests, TimeProvider clock) =>
{
    if (hook is { SessionId.Length: > 0, ToolName.Length: > 0 })
        requests.Add(new PermissionRequest(hook.SessionId, hook.ToolName, hook.ToolInput, hook.AgentId, clock.GetUtcNow()));
    return Results.NoContent();
});

app.MapGet("/api/agents/stream", (AgentRegistry registry, UsageBoard usage, CancellationToken ct) =>
    TypedResults.ServerSentEvents(Stream(registry, usage, ct)));

app.Run();

// "snapshot" every tick; "usage" on connect and whenever it changes.
static async IAsyncEnumerable<SseItem<object>> Stream(
    AgentRegistry registry, UsageBoard usage, [EnumeratorCancellation] CancellationToken ct)
{
    var sent = new Dictionary<string, UsageSnapshot>(StringComparer.Ordinal);
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
    do
    {
        yield return new SseItem<object>(registry.Snapshot(), "snapshot");
        foreach (var current in usage.Snapshot())
            if (sent.GetValueOrDefault(current.Source) != current)
            {
                sent[current.Source] = current;
                yield return new SseItem<object>(current, "usage");
            }
    } while (await timer.WaitForNextTickAsync(ct));
}
