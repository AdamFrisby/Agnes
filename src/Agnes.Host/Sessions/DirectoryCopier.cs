using System.Diagnostics;

namespace Agnes.Host.Sessions;

/// <summary>
/// Copies a session's working folder when forking. Prefers <c>cp -a --reflink=auto</c> (fast,
/// copy-on-write on btrfs/XFS/recent ZFS, preserves permissions/symlinks/timestamps) and falls back to
/// a managed recursive copy if <c>cp</c> is unavailable or fails. The whole tree is copied verbatim —
/// including <c>.git</c> and untracked files — so the fork is a faithful, independent snapshot.
/// </summary>
public static class DirectoryCopier
{
    public static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Source working directory does not exist: {source}");
        }

        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"Fork target already exists: {destination}");
        }

        if (await TryCopyWithCpAsync(source, destination, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        CopyManaged(new DirectoryInfo(source), destination, cancellationToken);
    }

    private static async Task<bool> TryCopyWithCpAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo("cp")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // `-a` archive (recursive, preserve, no-deref symlinks); `--reflink=auto` uses CoW when the
            // filesystem supports it and degrades to a full copy otherwise. `-T` copies source *into*
            // destination as the destination itself (no nesting). GNU coreutils; a no-op elsewhere → fallback.
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add("--reflink=auto");
            psi.ArgumentList.Add("-T");
            psi.ArgumentList.Add(source);
            psi.ArgumentList.Add(destination);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (proc.ExitCode == 0)
            {
                return true;
            }

            // cp partially copied then failed — clear the half-written target so the fallback starts clean.
            TryDelete(destination);
            _ = stderr;
            return false;
        }
        catch
        {
            TryDelete(destination);
            return false;
        }
    }

    private static void CopyManaged(DirectoryInfo source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in source.GetDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Skip symlinked directories' contents — recreate the link target as-is where possible.
            if (dir.LinkTarget is { } linkTarget)
            {
                Directory.CreateSymbolicLink(Path.Combine(destination, dir.Name), linkTarget);
                continue;
            }

            CopyManaged(dir, Path.Combine(destination, dir.Name), cancellationToken);
        }

        foreach (var file in source.GetFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, file.Name);
            if (file.LinkTarget is { } linkTarget)
            {
                File.CreateSymbolicLink(target, linkTarget);
            }
            else
            {
                file.CopyTo(target, overwrite: false);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
