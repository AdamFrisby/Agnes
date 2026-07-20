namespace Agnes.Sandbox.Incus;

/// <summary>Guest-side scripts installed via cloud-init: the run wrapper and the credential writer.</summary>
internal static class IncusGuest
{
    internal const string RunWrapperPath = "/usr/local/bin/agnes-run";
    internal const string AgentEnvFile = "/run/agnes/agent-env";
    internal const string GuestPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    /// <summary>
    /// Runs the agent as the unprivileged user with a scrubbed environment (ported from CodeyBox's
    /// exec wrapper, simplified for the interactive path: <c>exec</c> replaces the shell so the
    /// agent's stdin/stdout are the incus-exec pipe). Reads secrets from the root-owned env file.
    /// NB: no <c>setsid</c> — a detached session makes a long-lived stream-json agent exit when it
    /// reads stdin for a second turn (empirically: claude quits after turn 1). incus-exec allocates
    /// no controlling tty here anyway, so setsid adds no isolation; keeping the agent in the exec's
    /// own session preserves the persistent multi-turn stdin (and gives cleaner teardown).
    /// </summary>
    internal static string RunWrapper(IncusOptions o) => $$"""
        #!/bin/bash
        set -uo pipefail
        env_file={{AgentEnvFile}}
        declare -a envs=( "HOME={{o.GuestHome}}" "PATH={{GuestPath}}" "LANG=C.UTF-8" )
        if [ -r "$env_file" ]; then
          while IFS= read -r -d '' e; do envs+=("$e"); done < "$env_file"
        fi
        umask 077
        exec setpriv --no-new-privs --reuid={{o.GuestUserId}} --regid={{o.GuestGroupId}} --clear-groups -- env -i -- "${envs[@]}" "$@"
        """;

    internal static string CloudInit(IncusOptions o) => $$"""
        #cloud-config
        package_update: true
        packages:
          - python3
        users:
          - name: agnes
            uid: "{{o.GuestUserId}}"
            shell: /bin/bash
            lock_passwd: true
        write_files:
          - path: {{RunWrapperPath}}
            permissions: '0755'
            content: |
        {{Indent(RunWrapper(o), 6)}}
        runcmd:
          - [ mkdir, -p, /run/agnes ]
          - [ chmod, '0755', /run/agnes ]
          - [ mkdir, -p, "{{o.GuestHome}}" ]
          - [ chown, "{{o.GuestUserId}}:{{o.GuestGroupId}}", "{{o.GuestHome}}" ]
          # Always create the working directory so `incus exec --cwd /work` can't fail (127) when the
          # host working directory isn't bind-mounted (an unset/invalid dir); the mount overlays it
          # when present. Without this the agent never starts and the first prompt breaks its pipe.
          - [ mkdir, -p, /work ]
          - [ touch, /run/agnes/ready ]
        """;

    /// <summary>
    /// Hardened credential file writer (ported from CodeyBox's python writer): O_NOFOLLOW,
    /// mode 0600 files / 0700 dirs, atomic temp+replace, path-allowlist confined to $HOME.
    /// Reads the payload from stdin. Invoked as the unprivileged user with HOME set.
    /// </summary>
    internal const string CredentialWriterPython = """
        import errno, os, secrets, stat, sys
        O_DIRECTORY = getattr(os, "O_DIRECTORY", 0)
        O_NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)
        O_CLOEXEC = getattr(os, "O_CLOEXEC", 0)
        DIRECTORY_MODE = 0o700
        FILE_MODE = 0o600
        MAX_BYTES = 4 * 1024 * 1024

        def fail(m):
            print(m, file=sys.stderr); raise SystemExit(2)

        def validate_rel(value):
            if not value or value.startswith("/") or value.endswith("/") or "//" in value or "\\" in value:
                fail("invalid relative path")
            parts = value.split("/")
            for p in parts:
                if not p or p in (".", ".."):
                    fail("invalid path component")
            return parts

        def open_dir(parent_fd, name):
            return os.open(name, os.O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, dir_fd=parent_fd)

        def ensure_parents(root_fd, parts):
            cur = os.dup(root_fd)
            for p in parts:
                try:
                    nxt = open_dir(cur, p)
                except FileNotFoundError:
                    os.mkdir(p, DIRECTORY_MODE, dir_fd=cur); nxt = open_dir(cur, p)
                try:
                    if not stat.S_ISDIR(os.fstat(nxt).st_mode):
                        fail("parent is not a directory")
                    os.fchmod(nxt, DIRECTORY_MODE)
                finally:
                    os.close(cur)
                cur = nxt
            return cur

        def write_file(parent_fd, name, data):
            for _ in range(16):
                cand = f".{name}.tmp.{secrets.token_hex(8)}"
                try:
                    fd = os.open(cand, os.O_WRONLY | os.O_CREAT | os.O_EXCL | O_NOFOLLOW | O_CLOEXEC, FILE_MODE, dir_fd=parent_fd)
                    break
                except FileExistsError:
                    continue
            else:
                fail("temp name allocation failed")
            try:
                with os.fdopen(fd, "wb", closefd=True) as h:
                    h.write(data); h.flush(); os.fchmod(h.fileno(), FILE_MODE)
                os.replace(cand, name, src_dir_fd=parent_fd, dst_dir_fd=parent_fd)
            finally:
                try:
                    os.unlink(cand, dir_fd=parent_fd)
                except FileNotFoundError:
                    pass

        def main():
            if len(sys.argv) != 2:
                fail("usage: writer <home-relative-path>")
            rel = sys.argv[1]
            parts = validate_rel(rel)
            data = sys.stdin.buffer.read(MAX_BYTES + 1)
            if len(data) > MAX_BYTES:
                fail("payload too large")
            home = os.environ.get("HOME")
            if not home or not os.path.isdir(home):
                fail("HOME is not accessible")
            root_fd = os.open(home, os.O_RDONLY | O_DIRECTORY | O_CLOEXEC)
            parent_fd = None
            try:
                parent_fd = ensure_parents(root_fd, parts[:-1])
                write_file(parent_fd, parts[-1], data)
            finally:
                if parent_fd is not None:
                    os.close(parent_fd)
                os.close(root_fd)

        try:
            main()
        except SystemExit:
            raise
        except Exception as ex:
            fail(f"materialisation failed: {ex.__class__.__name__}: {ex}")
        """;

    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(l => pad + l));
    }
}
