using System.Runtime.InteropServices;
using System.Text;
using Agnes.Host.Hosting;

namespace Agnes.Host.Ops;

/// <summary>
/// Assembles the owner-only diagnostic bundle attached to a bug report: host/runtime metadata, the recent
/// error telemetry, and the tail of the host log ring. Pure assembly over its injected sources (log ring +
/// telemetry store + identity + adapter list) — no ambient state, so it is directly testable. The result is
/// UTF-8 text, capped at the same byte budget the sinks enforce: oversized content is truncated to the cap on
/// whole-character boundaries so the payload is never larger than the limit and never invalid UTF-8.
/// </summary>
public sealed class DiagnosticCollector
{
    private readonly HostLogRingBuffer _log;
    private readonly ErrorTelemetryStore _telemetry;
    private readonly HostIdentity _identity;
    private readonly Func<IReadOnlyList<string>> _adapters;
    private readonly long _maxBytes;

    public DiagnosticCollector(
        HostLogRingBuffer log,
        ErrorTelemetryStore telemetry,
        HostIdentity identity,
        Func<IReadOnlyList<string>> adapters,
        long maxBytes)
    {
        _log = log;
        _telemetry = telemetry;
        _identity = identity;
        _adapters = adapters;
        _maxBytes = Math.Max(1, maxBytes);
    }

    /// <summary>Builds the bundle as UTF-8 bytes, truncated to the configured byte cap.</summary>
    public byte[] Collect()
    {
        var sb = new StringBuilder();

        sb.Append("== Agnes host diagnostics ==\n");
        sb.Append("Generated: ").Append(DateTimeOffset.UtcNow.ToString("O")).Append('\n');
        sb.Append("Host: ").Append(_identity.DisplayName).Append(" (").Append(_identity.HostId).Append(")\n");
        sb.Append("Version: ").Append(_identity.Version).Append('\n');
        sb.Append("Runtime: ").Append(RuntimeInformation.FrameworkDescription).Append('\n');
        sb.Append("OS: ").Append(RuntimeInformation.OSDescription)
          .Append(" (").Append(RuntimeInformation.OSArchitecture).Append(")\n");

        var adapters = _adapters();
        sb.Append("Adapters: ").Append(adapters.Count == 0 ? "(none)" : string.Join(", ", adapters)).Append('\n');

        var errors = _telemetry.Snapshot();
        sb.Append("\n== Recent errors (").Append(errors.Count).Append(") ==\n");
        foreach (var e in errors)
        {
            sb.Append(e.Timestamp.ToString("O")).Append(' ').Append(e.Source).Append(": ").Append(e.Message).Append('\n');
        }

        var lines = _log.Snapshot();
        sb.Append("\n== Recent host log (").Append(lines.Count).Append(") ==\n");
        foreach (var line in lines)
        {
            sb.Append(line).Append('\n');
        }

        return CapUtf8(sb.ToString(), _maxBytes);
    }

    /// <summary>Encodes <paramref name="text"/> as UTF-8, trimming whole characters off the end until it fits
    /// within <paramref name="maxBytes"/> (so the result is never over the cap and never a split code unit).</summary>
    private static byte[] CapUtf8(string text, long maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        var cap = (int)Math.Min(maxBytes, int.MaxValue);
        // Each char is at least one UTF-8 byte, so `cap` chars is a safe upper bound to start trimming from.
        var chars = Math.Min(text.Length, cap);
        while (chars > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, chars)) > cap)
        {
            chars--;
        }

        return Encoding.UTF8.GetBytes(text[..chars]);
    }
}
