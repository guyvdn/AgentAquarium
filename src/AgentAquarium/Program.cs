using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using AgentAquarium.Domain;
using AgentAquarium.Sources.ClaudeCode;
using AgentAquarium.Sources.Demo;
using AgentAquarium.Tank;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<TankOptions>(builder.Configuration.GetSection("Tank"));
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<IAgentSink>(sp => sp.GetRequiredService<AgentRegistry>());

// Sources. Each one translates its own world into IAgentSink calls; add new ones here.
builder.Services.Configure<ClaudeCodeOptions>(builder.Configuration.GetSection("Sources:ClaudeCode"));
builder.Services.AddHostedService<ClaudeCodeSource>();
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection("Sources:Demo"));
builder.Services.AddHostedService<DemoSource>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/agents", (AgentRegistry registry) => registry.Snapshot());

app.MapGet("/api/agents/stream", (AgentRegistry registry, CancellationToken ct) =>
    TypedResults.ServerSentEvents(Stream(registry, ct), eventType: "snapshot"));

app.Run();

static async IAsyncEnumerable<IReadOnlyList<AgentSnapshot>> Stream(
    AgentRegistry registry, [EnumeratorCancellation] CancellationToken ct)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
    do
    {
        yield return registry.Snapshot();
    } while (await timer.WaitForNextTickAsync(ct));
}
