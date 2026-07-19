using Agnes.Abstractions;
using Agnes.Acp;
using Agnes.Agents.ClaudeCode;
using Agnes.Agents.Native;
using Agnes.Agents.OpenCode;
using Agnes.Recording;
using Agnes.Sandbox;
using Agnes.Sandbox.Credentials;
using Agnes.Sandbox.Incus;
using Microsoft.Extensions.Logging;

// Records a real agent session to a fixture for use as test data — optionally running the
// agent INSIDE an Incus sandbox VM (the live end-to-end sandbox path).
//
//   dotnet run --project tools/Agnes.Record -- \
//     --agent opencode --out recordings/opencode-qa.json --name "OpenCode Q&A" \
//     "What is 12 * 34? Reply with only the number."
//
//   sg incus-admin -c 'dotnet run --project tools/Agnes.Record -- \
//     --agent claude-native --sandbox incus --image agnes-claude-baseline \
//     --out recordings/sandbox-claude-qa.json --name "Sandboxed Claude Q&A" \
//     "What is 12 * 34? Reply with only the number."'
//
// Each non-flag argument is a prompt (one turn). Permission requests are auto-approved.

var options = ParseArgs(args);
if (options is null)
{
    Console.Error.WriteLine(
        "usage: Agnes.Record --agent <opencode|claude-code|claude-native> [--sandbox incus] " +
        "[--image <alias>] [--project <p>] [--pool <p>] [--bridge <b>] [--keep] " +
        "[--out <file>] [--name <name>] [--cwd <dir>] <prompt> [<prompt> ...]");
    return 1;
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));

Directory.CreateDirectory(options.Cwd);
// Headless streaming claude: --print keeps it non-interactive over pipes; stream-json both ways
// for multi-turn; --dangerously-skip-permissions because the sandbox VM is the security boundary.
string[] nativeArgs =
[
    "--print", "--output-format", "stream-json", "--input-format", "stream-json", "--verbose",
    "--dangerously-skip-permissions",
];

IAgentAdapter adapter = options.Agent switch
{
    "claude-code" => ClaudeCodeAgent.Create(loggerFactory),
    "claude-native" => ClaudeCodeNative.Create(loggerFactory, arguments: nativeArgs),
    _ => OpenCodeAgent.Create(loggerFactory),
};

// Optionally provision an Incus sandbox and run the agent inside it (credentials materialised in).
ISandbox? sandbox = null;
if (options.Sandbox == "incus")
{
    var incus = new IncusOptions
    {
        ProjectName = options.Project,
        StoragePoolName = options.Pool,
        Bridge = options.Bridge,
        DefaultImage = options.Image,
    };
    var provider = new IncusSandboxProvider(incus, loggerFactory);
    Console.WriteLine($">> provisioning Incus sandbox (image={options.Image}, project={options.Project}, pool={options.Pool}, bridge={options.Bridge})…");
    sandbox = await provider.CreateAsync(new SandboxSpec { HostWorkingDirectory = options.Cwd });
    Console.WriteLine($">> sandbox up: {sandbox.Id} [{sandbox.Info.State}]");

    var credentials = new ClaudeCredentialProvider(loggerFactory.CreateLogger<ClaudeCredentialProvider>());
    if (credentials.Handles(adapter.Descriptor.Id))
    {
        var credential = await credentials.GetAsync(adapter.Descriptor.Id);
        await sandbox.MaterializeCredentialAsync(credential);
        Console.WriteLine($">> materialised credentials ({credential.Files.Count} file(s), {credential.EnvironmentVariables.Count} env)");
    }
    else
    {
        Console.WriteLine($">> WARNING: no credential provider for '{adapter.Descriptor.Id}' — agent runs without host credentials");
    }
}

var launchOptions = new AgentSessionOptions
{
    WorkingDirectory = sandbox is null ? options.Cwd : "/work",
    Sandbox = sandbox,
};

Console.WriteLine($">> launching {adapter.Descriptor.DisplayName}{(sandbox is null ? "" : " (in sandbox)")} and recording…");
await using var session = await adapter.StartSessionAsync(launchOptions);

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

// Tear down the sandbox unless asked to keep it (test VMs shouldn't leak).
if (sandbox is not null)
{
    await session.DisposeAsync(); // stop the in-VM agent before deleting the VM
    if (options.Keep)
    {
        Console.WriteLine($">> keeping sandbox {sandbox.Id} (--keep); delete with: incus delete {sandbox.Id} --project {options.Project} --force");
    }
    else
    {
        Console.WriteLine($">> deleting sandbox {sandbox.Id}…");
        await sandbox.DeleteAsync();
    }
}

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
    string? outPath = null, name = null, sandbox = null;
    string project = "default", pool = "codeybox-zfs", bridge = "cb-net", image = "agnes-claude-baseline";
    var keep = false;
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
            case "--sandbox": sandbox = args[++i]; break;
            case "--project": project = args[++i]; break;
            case "--pool": pool = args[++i]; break;
            case "--bridge": bridge = args[++i]; break;
            case "--image": image = args[++i]; break;
            case "--keep": keep = true; break;
            default: prompts.Add(args[i]); break;
        }
    }

    return prompts.Count == 0 ? null : new RecordOptions(agent, outPath, name, cwd, prompts, sandbox, project, pool, bridge, image, keep);
}

internal sealed record RecordOptions(
    string Agent, string? Out, string? Name, string Cwd, IReadOnlyList<string> Prompts,
    string? Sandbox, string Project, string Pool, string Bridge, string Image, bool Keep);
