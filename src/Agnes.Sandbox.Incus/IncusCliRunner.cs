using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Agnes.Sandbox.Incus;

/// <summary>Runs one <c>incus</c> invocation (argv only), optionally piping stdin and streaming output.</summary>
internal sealed class IncusCliRunner
{
    private readonly ILogger _logger;

    public IncusCliRunner(ILogger logger) => _logger = logger;

    public async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        IReadOnlyList<string> argv,
        string? stdin = null,
        Action<string>? stdoutChunk = null,
        Action<string>? stderrChunk = null,
        CancellationToken cancellationToken = default)
    {
        if (argv.Count == 0)
        {
            throw new ArgumentException("argv must not be empty.", nameof(argv));
        }

        var psi = new ProcessStartInfo(argv[0])
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < argv.Count; i++)
        {
            psi.ArgumentList.Add(argv[i]);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{argv[0]}'.");

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdoutBuilder.AppendLine(e.Data); stdoutChunk?.Invoke(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderrBuilder.AppendLine(e.Data); stderrChunk?.Invoke(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    public async Task RunCheckedAsync(string what, IReadOnlyList<string> argv, string? stdin = null, CancellationToken cancellationToken = default)
    {
        var (code, _, stderr) = await RunAsync(argv, stdin, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            _logger.LogError("incus {What} failed ({Code}): {Stderr}", what, code, stderr.Trim());
            throw new InvalidOperationException($"incus {what} failed ({code}): {stderr.Trim()}");
        }
    }
}
