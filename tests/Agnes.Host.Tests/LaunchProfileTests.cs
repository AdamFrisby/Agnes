using Agnes.Host.Hosting;
using Agnes.Protocol;

namespace Agnes.Host.Tests;

public class LaunchProfileTests
{
    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), $"agnes-launch-profiles-{Guid.NewGuid():n}");

    private static LaunchProfile Sample(string id = "", string name = "Scratch OpenCode")
        => new(id, name, "opencode", WorkingDirectory: null, UseWorktree: true, SkipPermissions: true,
            McpApproval: "Trust", GitCredentialMode: "Ask", UseSandbox: false, ModelId: "gpt-5");

    // ---- store ----

    [Fact]
    public void Save_assigns_an_id_when_blank_and_upserts_by_id()
    {
        var store = new LaunchProfileStore();

        var saved = store.Save(Sample());
        Assert.False(string.IsNullOrWhiteSpace(saved.Id));

        // Re-saving the same id updates in place rather than duplicating.
        store.Save(saved with { Name = "Scratch OpenCode (updated)" });
        var all = store.List();
        Assert.Single(all);
        Assert.Equal("Scratch OpenCode (updated)", all[0].Name);
    }

    [Fact]
    public void Profiles_round_trip_through_a_reload_with_every_captured_option()
    {
        var dir = NewTempDir();
        try
        {
            var store = new LaunchProfileStore(dir);
            var pinned = store.Save(new LaunchProfile(string.Empty, "Prod Claude", "claude-code",
                WorkingDirectory: Path.Combine(Path.GetTempPath(), "repo"), UseWorktree: false, SkipPermissions: false,
                McpApproval: "Ask", GitCredentialMode: "Trust", UseSandbox: true, ModelId: null));
            var scratch = store.Save(Sample());

            // A brand-new instance over the same directory sees everything that was saved, byte-for-byte.
            var reloaded = new LaunchProfileStore(dir);
            Assert.Equal(2, reloaded.List().Count);

            var back = reloaded.Find(scratch.Id);
            Assert.NotNull(back);
            Assert.Equal("opencode", back!.AdapterId);
            Assert.Null(back.WorkingDirectory);
            Assert.True(back.UseWorktree);
            Assert.True(back.SkipPermissions);
            Assert.Equal("Trust", back.McpApproval);
            Assert.Equal("Ask", back.GitCredentialMode);
            Assert.False(back.UseSandbox);
            Assert.Equal("gpt-5", back.ModelId);

            var pinnedBack = reloaded.Find(pinned.Id);
            Assert.Equal(Path.Combine(Path.GetTempPath(), "repo"), pinnedBack!.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Update_by_id_and_delete_are_reflected_after_reload()
    {
        var dir = NewTempDir();
        try
        {
            var a = new LaunchProfileStore(dir).Save(Sample(name: "A"));
            var b = new LaunchProfileStore(dir).Save(Sample(name: "B"));

            // Update A in place through a fresh instance.
            new LaunchProfileStore(dir).Save(a with { Name = "A2" });
            // Delete B.
            Assert.True(new LaunchProfileStore(dir).Delete(b.Id));
            Assert.False(new LaunchProfileStore(dir).Delete("does-not-exist"));

            var reloaded = new LaunchProfileStore(dir);
            Assert.Single(reloaded.List());
            Assert.Equal("A2", reloaded.Find(a.Id)!.Name);
            Assert.Null(reloaded.Find(b.Id));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_blank_directory_keeps_the_store_in_memory_only()
    {
        var inMemory = new LaunchProfileStore(directory: null);
        inMemory.Save(Sample());
        Assert.Single(inMemory.List());

        var another = new LaunchProfileStore(directory: "   ");
        Assert.Empty(another.List());
    }

    // ---- back-compat ----

    [Fact]
    public void Legacy_json_without_connected_service_profile_id_deserializes_to_null()
    {
        var dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            // A file written before the ConnectedServiceProfileId seam existed.
            var legacy = """
            [
              {
                "id": "legacy1",
                "name": "Legacy",
                "adapterId": "claude-code",
                "workingDirectory": null,
                "useWorktree": false,
                "skipPermissions": true,
                "mcpApproval": "Ask",
                "gitCredentialMode": "Off",
                "useSandbox": true,
                "modelId": null
              }
            ]
            """;
            File.WriteAllText(Path.Combine(dir, LaunchProfileStore.FileName), legacy);

            var store = new LaunchProfileStore(dir);
            var loaded = store.Find("legacy1");
            Assert.NotNull(loaded);
            Assert.Null(loaded!.ConnectedServiceProfileId);
            Assert.True(loaded.SkipPermissions);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Connected_service_profile_id_seam_round_trips_when_set()
    {
        // The seam is ignored for launch this pass, but the field must persist so later wiring needs no schema change.
        var dir = NewTempDir();
        try
        {
            var store = new LaunchProfileStore(dir);
            var saved = store.Save(Sample() with { ConnectedServiceProfileId = "svc-42" });
            var reloaded = new LaunchProfileStore(dir).Find(saved.Id);
            Assert.Equal("svc-42", reloaded!.ConnectedServiceProfileId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- materialization (OpenSessionFromProfile) ----

    [Fact]
    public void Materializing_captures_the_exact_options_into_the_open_request()
    {
        var repo = Path.Combine(Path.GetTempPath(), "pinned-repo");
        var profile = new LaunchProfile("p1", "Prod", "claude-code",
            WorkingDirectory: repo, UseWorktree: true, SkipPermissions: false,
            McpApproval: "Ask", GitCredentialMode: "Trust", UseSandbox: true, ModelId: "opus");

        var open = profile.ToOpenSessionRequest();

        Assert.Equal("claude-code", open.AdapterId);
        Assert.Equal(repo, open.WorkingDirectory);
        Assert.True(open.UseWorktree);
        Assert.False(open.SkipPermissions);
        Assert.Equal("Ask", open.McpApproval);
        Assert.Equal("Trust", open.GitCredentialMode);
        Assert.True(open.UseSandbox);
        Assert.Equal("opus", open.ModelId);
    }

    [Fact]
    public void A_dir_agnostic_profile_uses_the_override_directory()
    {
        var profile = Sample(); // WorkingDirectory == null
        var overrideDir = Path.Combine(Path.GetTempPath(), "chosen-at-launch");

        var open = profile.ToOpenSessionRequest(overrideDir);

        Assert.Equal(overrideDir, open.WorkingDirectory);
        // The rest of the captured options are unchanged.
        Assert.Equal("opencode", open.AdapterId);
        Assert.True(open.SkipPermissions);
    }

    [Fact]
    public void The_override_wins_over_a_pinned_directory()
    {
        var pinned = Path.Combine(Path.GetTempPath(), "pinned");
        var chosen = Path.Combine(Path.GetTempPath(), "chosen");
        var profile = Sample() with { WorkingDirectory = pinned };

        Assert.Equal(chosen, profile.ToOpenSessionRequest(chosen).WorkingDirectory);
        // No override falls back to the pinned directory.
        Assert.Equal(pinned, profile.ToOpenSessionRequest().WorkingDirectory);
    }

    [Fact]
    public void A_dir_agnostic_profile_with_no_override_is_a_clear_error()
    {
        var profile = Sample(); // WorkingDirectory == null
        var ex = Assert.Throws<InvalidOperationException>(() => profile.ToOpenSessionRequest());
        Assert.Contains("directory-agnostic", ex.Message, StringComparison.Ordinal);
        // A blank/whitespace override doesn't count as a directory.
        Assert.Throws<InvalidOperationException>(() => profile.ToOpenSessionRequest("   "));
    }
}
