using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Agnes.App.Desktop;

/// <summary>
/// One Agnes window per machine, and a way to hand it a message.
///
/// Agnes already connects to as many hosts as you like from a single window, so a second copy has nothing to
/// offer: it would compete for the same saved tabs and settings files, split your sessions across two windows,
/// and — the reason this exists — mean that clicking an <c>agnes://pair</c> link while Agnes is open launches
/// a whole second app instead of pairing in the one you're using.
///
/// Two primitives, each doing the job it's good at. A lock file decides <em>who</em> is the running instance:
/// it is held open for the process's lifetime, so the operating system releases it on exit or on a crash, and
/// there is no stale state to clean up. A named pipe carries the message to that instance (a Unix domain
/// socket underneath, on Linux and macOS).
///
/// The overriding rule is that none of this may stop Agnes starting. Every failure path here — no temp
/// directory, a pipe that won't bind, a hostile sandbox — resolves to "run as the primary anyway". The worst
/// outcome of a bug in this file should be two windows, never zero.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The message a second launch sends when it has no link to pass — "you're already running, come
    /// to the front".</summary>
    public const string ActivateOnly = "activate";

    private const int ConnectTimeoutMs = 3000;
    private const int RetryStepMs = 200;

    private readonly FileStream _lock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopping = new();

    private SingleInstance(FileStream lockFile, string pipeName)
    {
        _lock = lockFile;
        _pipeName = pipeName;
    }

    /// <summary>Raised on a background thread when another launch hands this instance a message.</summary>
    public event Action<string>? MessageReceived;

    /// <summary>
    /// Claims the role of the running instance for <paramref name="key"/>.
    ///
    /// Returns the claim when this process is (or has become) the one instance — the caller should carry on and
    /// start the app. Returns null when another instance already holds it, in which case
    /// <paramref name="message"/> has been delivered to it and this process should exit quietly.
    /// </summary>
    public static SingleInstance? TryClaim(string key, string message)
    {
        string lockPath;
        string pipeName;
        try
        {
            // Keyed per user as well as per app: two people on one machine each get their own instance, and a
            // pipe named after another user's session would be unreachable anyway.
            var scope = Hash($"{key}:{Environment.UserName}");
            lockPath = Path.Combine(Path.GetTempPath(), $"agnes-{scope}.lock");
            pipeName = $"agnes-{scope}";
        }
        catch (Exception)
        {
            return Orphan(); // no usable temp path — run, unguarded.
        }

        FileStream? held = null;
        try
        {
            // FileShare.None is the mutual exclusion: exactly one process can hold this open, and the handle
            // closing (however the process ends) is what releases it.
            held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            // Someone else holds it. Hand them the message; if that fails they may be mid-shutdown, so fall
            // through to starting normally rather than leaving the user with nothing.
            return Send(pipeName, message) ? null : Orphan();
        }
        catch (UnauthorizedAccessException)
        {
            return Orphan();
        }

        var instance = new SingleInstance(held, pipeName);
        instance.Listen();
        return instance;
    }

    /// <summary>An unguarded claim: the app runs, but nothing is forwarded to it. Used whenever the gate itself
    /// can't be established, because refusing to start would be far worse than allowing a second window.</summary>
    private static SingleInstance? Orphan() => new(null!, string.Empty);

    /// <summary>True when this claim is holding the lock, rather than being the degraded fallback.</summary>
    public bool IsGuarded => _lock is not null;

    /// <summary>
    /// Hands a message to the running instance. Retries for a moment rather than trying once, because the
    /// listener is briefly absent between accepting one connection and opening the next — and a link that
    /// silently did nothing because it arrived in that gap is exactly the bug this class exists to prevent.
    /// </summary>
    private static bool Send(string pipeName, string message)
    {
        var deadline = Environment.TickCount64 + ConnectTimeoutMs;
        do
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                client.Connect(RetryStepMs);
                var payload = Encoding.UTF8.GetBytes(message);
                client.Write(payload, 0, payload.Length);
                client.Flush();
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(RetryStepMs / 2);
            }
        }
        while (Environment.TickCount64 < deadline);

        return false;
    }

    /// <summary>
    /// Starts accepting messages. The first listener is opened <em>synchronously</em>, before this returns:
    /// a launch that claimed the instance and then clicked a link a millisecond later must not find its own
    /// pipe missing, and a fire-and-forget task offers no such guarantee.
    /// </summary>
    private void Listen()
    {
        if (_pipeName.Length == 0)
        {
            return;
        }

        var server = TryOpenServer();
        if (server is null)
        {
            return; // no pipe: still the instance, just unreachable. Better than refusing to start.
        }

        _ = Task.Run(() => AcceptLoopAsync(server));
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream first)
    {
        var server = first;
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                using (server)
                {
                    await server.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var message = await reader.ReadToEndAsync(_stopping.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        MessageReceived?.Invoke(message.Trim());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A malformed or interrupted connection loses that one message, never the listener.
            }

            if (_stopping.IsCancellationRequested || TryOpenServer() is not { } next)
            {
                return;
            }

            server = next;
        }
    }

    // One connection at a time is plenty — these arrive at human speed, one per click of a link.
    private NamedPipeServerStream? TryOpenServer()
    {
        try
        {
            return new NamedPipeServerStream(
                _pipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _lock?.Dispose();
    }
}
