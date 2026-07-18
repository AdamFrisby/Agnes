using Agnes.Abstractions;
using Agnes.Acp;
using Agnes.Agents.ClaudeCode;
using Agnes.Agents.OpenCode;
using Agnes.Recording;
using Microsoft.Extensions.Logging;

// Records a real ACP agent session to a fixture for use as test data.
//
//   dotnet run --project tools/Agnes.Record -- \
//     --agent opencode --out recordings/opencode-qa.json --name "OpenCode Q&A" \
//     "What is 12 * 34? Reply with only the number."
//
// Each non-flag argument is a prompt (one turn). Permission requests are auto-approved.

var options = ParseArgs(args);
if (options is null)
{
    Console.Error.WriteLine("usage: Agnes.Record --agent <opencode|claude-code> [--out <file>] [--name <name>] [--cwd <dir>] <prompt> [<prompt> ...]");
    return 1;
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));

Directory.CreateDirectory(options.Cwd);
AcpAgentAdapter adapter = options.Agent switch
{
    "claude-code" => ClaudeCodeAgent.Create(loggerFactory),
    _ => OpenCodeAgent.Create(loggerFactory),
};

Console.WriteLine($">> launching {adapter.Descriptor.DisplayName} and recording…");
await using var session = await adapter.StartSessionAsync(new AgentSessionOptions { WorkingDirectory = options.Cwd });

var recorder = new SessionRecorder();
using var stop = new CancellationTokenSource();

// Consume the event stream: record every event and auto-approve permission requests.
var pump = Task.Run(async () =>
{
    try
    {
        await foreach (var @event in session.Events.ReadAllAsync(stop.Token))
        {
            recorder.Record(@event);
            Console.WriteLine($"   [{recorder.Count:D3}] {Describe(@event)}");

            if (@event is PermissionRequestedEvent permission)
            {
                var option = permission.Options.FirstOrDefault(o =>
                                 o.Kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.AllowAlways)
                             ?? permission.Options.FirstOrDefault();
                if (option is not null)
                {
                    await session.RespondToPermissionAsync(permission.RequestId, option.OptionId);
                }
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
});

foreach (var prompt in options.Prompts)
{
    Console.WriteLine($">> prompt: {prompt}");
    var reason = await session.PromptAsync([new TextContent(prompt)]);
    Console.WriteLine($">> turn ended: {reason}");
}

await Task.Delay(500); // let any trailing events arrive
stop.Cancel();
await pump;

var name = options.Name ?? $"{adapter.Descriptor.DisplayName} recording";
var recording = recorder.Build(name, adapter.Descriptor.Id, adapter.Descriptor.DisplayName);
var outPath = options.Out ?? $"recording-{options.Agent}.json";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
RecordingStore.Save(outPath, recording);
Console.WriteLine($">> saved {recording.Events.Count} events ({recording.DurationMs} ms) to {outPath}");
return 0;

static string Describe(SessionEvent e) => e switch
{
    MessageChunkEvent m => $"message[{m.Role}] {(m.Content as TextContent)?.Text}",
    ThoughtChunkEvent t => $"thought {(t.Content as TextContent)?.Text}",
    ToolCallEvent tc => $"tool {tc.Kind} {tc.Title} ({tc.Status})",
    ToolCallUpdateEvent u => $"tool-update {u.ToolCallId} {u.Status}",
    PlanEvent p => $"plan ({p.Entries.Count} entries)",
    PermissionRequestedEvent p => $"permission? {p.Title}",
    PermissionResolvedEvent r => $"permission {r.Outcome}",
    TurnEndedEvent te => $"turn-ended {te.Reason}",
    AgentErrorEvent err => $"error {err.Message}",
    _ => e.GetType().Name,
};

static RecordOptions? ParseArgs(string[] args)
{
    string agent = "opencode";
    string? outPath = null, name = null;
    var cwd = Path.Combine(Path.GetTempPath(), "agnes-record");
    var prompts = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--agent": agent = args[++i]; break;
            case "--out": outPath = args[++i]; break;
            case "--name": name = args[++i]; break;
            case "--cwd": cwd = args[++i]; break;
            default: prompts.Add(args[i]); break;
        }
    }

    return prompts.Count == 0 ? null : new RecordOptions(agent, outPath, name, cwd, prompts);
}

internal sealed record RecordOptions(string Agent, string? Out, string? Name, string Cwd, IReadOnlyList<string> Prompts);
