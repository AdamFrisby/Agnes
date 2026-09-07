// Agnes.DisplaySpike — proves and measures the graphical-sandbox capture path against a real Incus VM.
//
// It drives exactly the production code (IncusSandboxProvider -> IDisplaySource -> IncusDisplaySession);
// nothing here re-implements the protocol. Not part of Agnes.Core.slnf: every mode needs a live VM.
//
//   dotnet run --project tools/Agnes.DisplaySpike -- prepare <instance>
//       Starts the instance's private display bus and prints the two incus config values it needs.
//   dotnet run --project tools/Agnes.DisplaySpike -- measure <instance> <outDir>
//       Attaches, saves a PNG a second, injects input, and prints the numbers.
//   dotnet run --project tools/Agnes.DisplaySpike -- provision <outDir>
//       Bakes the graphical image if it is missing, then provisions a sandbox with a display
//       entirely through IncusSandboxProvider and screenshots it. The whole path, no hand-holding.
using System.Diagnostics;
using System.Globalization;
using Agnes.Sandbox;
using Agnes.Sandbox.Incus;
using Agnes.Sandbox.Incus.Graphical;
using Microsoft.Extensions.Logging;
using SkiaSharp;

var mode = args.Length > 0 ? args[0] : "measure";
var instance = args.Length > 1 ? args[1] : "agnes-display-spike";
var outDir = mode == "provision"
    ? (args.Length > 1 ? args[1] : "/tmp/agnes-display-spike")
    : (args.Length > 2 ? args[2] : "/tmp/agnes-display-spike");

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("spike");

// The live host runs its VMs in the default project on the codeybox-zfs pool; mirror that so the spike
// talks to the same Incus the daemon does.
var options = new IncusOptions
{
    ProjectName = "default",
    StoragePoolName = "codeybox-zfs",
    Bridge = "cb-net",
};

if (mode == "prepare")
{
    var bus = new DisplayBus(options, logger);
    await bus.EnsureRunningAsync(instance);
    Console.WriteLine("raw.qemu=" + bus.RawQemuFor(instance));
    Console.WriteLine("---raw.apparmor---");
    Console.WriteLine(bus.RawAppArmor());
    return 0;
}

var display = new GraphicalDisplay(1280, 800);
var provider = new IncusSandboxProvider(options, loggerFactory);

if (mode == "provision")
{
    var manifest = new SandboxImageManifest { Agents = [] }.AsGraphical();
    if (!await provider.ImageExistsAsync(manifest.Alias))
    {
        Console.WriteLine($"Baking {manifest.Alias} (this takes a few minutes)...");
        await provider.BuildImageAsync(manifest, new Progress<string>(m => Console.WriteLine("  " + m)));
    }

    Console.WriteLine("Provisioning a graphical sandbox through the provider...");
    var provisioned = await provider.CreateAsync(new SandboxSpec { Display = display });
    Console.WriteLine("  instance " + provisioned.Id);
    try
    {
        // The X session comes up a few seconds after the guest reports ready.
        await Task.Delay(TimeSpan.FromSeconds(20));
        await using var provisionedDisplay = await ((IDisplaySource)provisioned).OpenDisplayAsync();
        Console.WriteLine($"  display {provisionedDisplay.Geometry.Width}x{provisionedDisplay.Geometry.Height}");
        Directory.CreateDirectory(outDir);
        await Task.Delay(TimeSpan.FromSeconds(5));
        await SavePngAsync(provisionedDisplay, Path.Combine(outDir, "provisioned.png"));
        Console.WriteLine("  screenshot in " + outDir);
    }
    finally
    {
        await provisioned.DeleteAsync();
        Console.WriteLine("  deleted");
    }

    return 0;
}

var sandbox = await provider.AttachAsync(instance, new SandboxSpec { Display = display }, start: false);
if (sandbox is not IDisplaySource source)
{
    Console.Error.WriteLine("The sandbox handle is not an IDisplaySource — the spec had no Display.");
    return 1;
}

Directory.CreateDirectory(outDir);
var processing = new List<double>(capacity: 100_000);
var openedAt = Stopwatch.GetTimestamp();
await using var session = await OpenWithMetricsAsync();
Console.WriteLine($"opened in {Stopwatch.GetElapsedTime(openedAt).TotalMilliseconds:F0} ms — geometry {session.Geometry.Width}x{session.Geometry.Height}");

// ---- collector: drains updates, counts them, and writes a PNG a second ----
var updates = 0L;
var bytes = 0L;
var queueLatency = new List<double>(capacity: 100_000);
var rectAreas = new List<double>(capacity: 100_000);
var fullSurface = 0L;
DateTimeOffset? lastUpdateAt = null;
using var stop = new CancellationTokenSource();
var collector = Task.Run(async () =>
{
    var lastPng = Stopwatch.GetTimestamp();
    var png = 0;
    await foreach (var update in session.Updates.ReadAllAsync(stop.Token))
    {
        Interlocked.Increment(ref updates);
        Interlocked.Add(ref bytes, update.Pixels.Length);
        if (update.Width == session.Geometry.Width && update.Height == session.Geometry.Height)
        {
            Interlocked.Increment(ref fullSurface);
        }

        lock (rectAreas)
        {
            rectAreas.Add(update.Width * (double)update.Height);
        }

        lock (queueLatency)
        {
            queueLatency.Add((DateTimeOffset.UtcNow - update.At).TotalMilliseconds);
            lastUpdateAt = DateTimeOffset.UtcNow;
        }

        if (Stopwatch.GetElapsedTime(lastPng) > TimeSpan.FromSeconds(1))
        {
            lastPng = Stopwatch.GetTimestamp();
            await SavePngAsync(session, Path.Combine(outDir, $"frame-{png++:D3}.png"));
        }
    }
});

// ---- idle baseline ----
await Task.Delay(TimeSpan.FromSeconds(3));
var idleUpdates = Interlocked.Read(ref updates);
Console.WriteLine($"idle: {idleUpdates} updates in 3s ({idleUpdates / 3.0:F1}/s)");

// ---- keyboard: type a command whose effect we can read back out of the guest ----
await TypeAsync(session, "echo AGNES123 > /tmp/kt.txt");
await KeyAsync(session, "Return");
await Task.Delay(500);
Console.WriteLine("typed(basic) -> " + await ReadGuestFileAsync(instance, "/tmp/kt.txt"));

// Arrows and End, verified through readline: "echo XY", Left Left, "Z", End, redirect.
await TypeAsync(session, "echo XY");
await KeyAsync(session, "Left");
await KeyAsync(session, "Left");
await TypeAsync(session, "Z");
await KeyAsync(session, "End");
await TypeAsync(session, " > /tmp/kt2.txt");
await KeyAsync(session, "Return");
await Task.Delay(500);
Console.WriteLine("typed(arrows) -> " + await ReadGuestFileAsync(instance, "/tmp/kt2.txt"));

// Ctrl+C, to prove a modifier reaches the tty.
await TypeAsync(session, "echo NOTSENT");
await KeyDownUpAsync(session, "ctrl", "c");
await TypeAsync(session, "echo CTRLC > /tmp/kt3.txt");
await KeyAsync(session, "Return");
await Task.Delay(500);
Console.WriteLine("typed(ctrl+c) -> " + await ReadGuestFileAsync(instance, "/tmp/kt3.txt"));

// ---- input -> pixel latency: press a key, wait for the glyph ----
var latencies = new List<double>();
for (var i = 0; i < 20; i++)
{
    lock (queueLatency)
    {
        lastUpdateAt = null;
    }

    var sent = Stopwatch.GetTimestamp();
    await KeyAsync(session, "a");
    var deadline = Stopwatch.GetTimestamp();
    while (Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(2))
    {
        lock (queueLatency)
        {
            if (lastUpdateAt is not null)
            {
                latencies.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
                break;
            }
        }

        await Task.Delay(1);
    }

    await Task.Delay(120);
}

await KeyDownUpAsync(session, "ctrl", "c");
await Task.Delay(300);

// ---- throughput: flood the terminal and watch the update rate ----
processing.Clear();
lock (queueLatency)
{
    queueLatency.Clear();
}

Interlocked.Exchange(ref updates, 0);
Interlocked.Exchange(ref bytes, 0);
await TypeAsync(session, "yes AGNESAGNESAGNESAGNES");
await KeyAsync(session, "Return");
var floodStart = Stopwatch.GetTimestamp();
await Task.Delay(TimeSpan.FromSeconds(6));
var floodSeconds = Stopwatch.GetElapsedTime(floodStart).TotalSeconds;
var floodUpdates = Interlocked.Read(ref updates);
var floodBytes = Interlocked.Read(ref bytes);
await KeyDownUpAsync(session, "ctrl", "c");
await Task.Delay(500);

// ---- pointer: right-click paints openbox's root menu ----
await session.InjectAsync(new PointerMove(640, 400));
await Task.Delay(200);
Interlocked.Exchange(ref updates, 0);
var clickAt = Stopwatch.GetTimestamp();
await session.InjectAsync(new PointerButton(PointerButtonKind.Right, true));
await session.InjectAsync(new PointerButton(PointerButtonKind.Right, false));
double clickLatency = -1;
while (Stopwatch.GetElapsedTime(clickAt) < TimeSpan.FromSeconds(3))
{
    if (Interlocked.Read(ref updates) > 0)
    {
        clickLatency = Stopwatch.GetElapsedTime(clickAt).TotalMilliseconds;
        break;
    }

    await Task.Delay(1);
}

await Task.Delay(400);
await SavePngAsync(session, Path.Combine(outDir, "after-right-click.png"));
await session.InjectAsync(new PointerButton(PointerButtonKind.Left, true));
await session.InjectAsync(new PointerButton(PointerButtonKind.Left, false));

await stop.CancelAsync();
await collector.ContinueWith(_ => { }, TaskScheduler.Default);

Console.WriteLine();
Console.WriteLine("=== measured ===");
Console.WriteLine($"flood: {floodUpdates} updates in {floodSeconds:F1}s = {floodUpdates / floodSeconds:F1} updates/s");
Console.WriteLine($"flood: {floodBytes / 1024.0 / 1024.0:F1} MiB = {floodBytes / floodSeconds / 1024.0 / 1024.0:F2} MiB/s");
Console.WriteLine($"flood: mean update {floodBytes / (double)Math.Max(1, floodUpdates) / 1024.0:F1} KiB");
Console.WriteLine($"damage rectangles: {Interlocked.Read(ref fullSurface)} of {rectAreas.Count} covered the whole surface");
Report("damage rectangle area (px)", rectAreas);
Report("handler (arrival -> published)", processing);
Report("queue (published -> consumer)", queueLatency);
Report("key press -> first update", latencies);
Console.WriteLine($"right-click -> first update: {clickLatency:F1} ms");
Console.WriteLine($"PNGs in {outDir}");
return 0;

async Task<IDisplaySession> OpenWithMetricsAsync()
{
    // Reaches past IDisplaySource for the metrics hook only; everything else is the public path.
    var bus = new DisplayBus(options, logger);
    await bus.EnsureRunningAsync(instance);
    return await IncusDisplaySession.OpenAsync(
        bus.AddressFor(instance), TimeSpan.FromSeconds(30), logger, CancellationToken.None,
        elapsed =>
        {
            lock (processing)
            {
                processing.Add(elapsed.TotalMilliseconds);
            }
        });
}

static void Report(string what, List<double> samples)
{
    double[] copy;
    lock (samples)
    {
        copy = [.. samples];
    }

    if (copy.Length == 0)
    {
        Console.WriteLine($"{what}: no samples");
        return;
    }

    Array.Sort(copy);
    var mean = copy.Average();
    var p95 = copy[(int)Math.Min(copy.Length - 1, Math.Floor(copy.Length * 0.95))];
    Console.WriteLine($"{what}: n={copy.Length} mean={mean:F2} ms p95={p95:F2} ms max={copy[^1]:F2} ms");
}

static async Task SavePngAsync(IDisplaySession session, string path)
{
    var frame = await session.SnapshotAsync();
    var geometry = session.Geometry;
    if (frame.Length < geometry.Width * geometry.Height * 4)
    {
        return;
    }

    var info = new SKImageInfo(geometry.Width, geometry.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
    using var bitmap = new SKBitmap();
    var pixels = frame.ToArray();
    var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
    try
    {
        bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        await using var file = File.Create(path);
        data.SaveTo(file);
    }
    finally
    {
        handle.Free();
    }
}

static async Task TypeAsync(IDisplaySession session, string text)
{
    foreach (var c in text)
    {
        await KeyAsync(session, KeysymFor(c));
        await Task.Delay(12);
    }
}

static async Task KeyAsync(IDisplaySession session, string keysym)
{
    await session.InjectAsync(new KeyPress(keysym, true));
    await Task.Delay(8);
    await session.InjectAsync(new KeyPress(keysym, false));
}

static async Task KeyDownUpAsync(IDisplaySession session, string modifier, string key)
{
    await session.InjectAsync(new KeyPress(modifier, true));
    await KeyAsync(session, key);
    await session.InjectAsync(new KeyPress(modifier, false));
}

static string KeysymFor(char c) => c switch
{
    ' ' => "space",
    '>' => "greater",
    '<' => "less",
    '/' => "slash",
    '.' => "period",
    ',' => "comma",
    '_' => "underscore",
    '-' => "minus",
    '=' => "equal",
    '\'' => "apostrophe",
    '"' => "quotedbl",
    ':' => "colon",
    ';' => "semicolon",
    '|' => "bar",
    '$' => "dollar",
    _ => c.ToString(CultureInfo.InvariantCulture),
};

static async Task<string> ReadGuestFileAsync(string instance, string path)
{
    var psi = new ProcessStartInfo("incus") { RedirectStandardOutput = true, RedirectStandardError = true };
    psi.ArgumentList.Add("exec");
    psi.ArgumentList.Add(instance);
    psi.ArgumentList.Add("--");
    psi.ArgumentList.Add("cat");
    psi.ArgumentList.Add(path);
    using var process = Process.Start(psi)!;
    var stdout = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    return process.ExitCode == 0 ? stdout.Trim() : "<missing>";
}
