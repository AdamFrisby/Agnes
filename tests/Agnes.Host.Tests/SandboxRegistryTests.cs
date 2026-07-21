using Agnes.Host.Sessions;

namespace Agnes.Host.Tests;

public class SandboxRegistryTests
{
    [Fact]
    public void Upsert_setstate_remove_persist_across_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agnes-sbx-{Guid.NewGuid():n}.json");
        try
        {
            var reg = new SandboxRegistry(path);
            var now = DateTimeOffset.UtcNow;
            reg.Upsert(new SandboxRecord("s1", "agnes-1", "incus", "claude-code-native", "/work/a", "ProjA", "a", "running", now, now));
            reg.Upsert(new SandboxRecord("s2", "agnes-2", "incus", "codex", "/work/b", null, "b", "running", now, now));
            reg.SetState("s1", "stopped", now.AddMinutes(1));

            // A fresh instance reads the persisted file — the key property for surviving a daemon restart.
            var reloaded = new SandboxRegistry(path);
            Assert.Equal(2, reloaded.List().Count);
            Assert.Equal("stopped", reloaded.Get("s1")!.State);
            Assert.Contains("agnes-1", reloaded.TrackedVmNames());
            Assert.Contains("agnes-2", reloaded.TrackedVmNames());

            reloaded.Remove("s2");
            Assert.Null(new SandboxRegistry(path).Get("s2"));
            Assert.Single(new SandboxRegistry(path).List());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
