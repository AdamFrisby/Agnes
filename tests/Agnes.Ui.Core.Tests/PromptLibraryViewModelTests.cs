using Agnes.Abstractions;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The prompt/skill library surface, from the user's side: a skill you can find is a skill you can install
/// (the section used to be delete-only while the host already served a registry), an empty section explains
/// <em>why</em> it's empty, and the single editor states which of its two jobs — new or overwrite — it is
/// currently doing.
/// </summary>
public class PromptLibraryViewModelTests
{
    [Fact]
    public async Task Registries_are_browsed_on_open_without_picking_one_first()
    {
        var host = new FakeLibraryHost();
        host.Registry("local-dir", new RegistrySkillEntry("pdf", "PDF tools", "Read PDFs", "/skills/pdf"));
        var vm = new PromptLibraryViewModel(() => host, ImmediateDispatcher.Instance);

        await vm.RefreshAsync();

        Assert.True(vm.HasSkillRegistries);
        Assert.Equal("PDF tools", Assert.Single(vm.RegistrySkills).Title);
    }

    [Fact]
    public async Task Searching_asks_every_registry_and_labels_each_result_with_its_source()
    {
        var host = new FakeLibraryHost();
        host.Registry("local-dir", new RegistrySkillEntry("pdf", "PDF tools", null, "/skills/pdf"));
        host.Registry("skillshub", new RegistrySkillEntry("anthropics/skills/pdf", "pdf", "Official", "github.com/anthropics/skills")
        {
            Publisher = "Anthropic",
            Stars = 96108,
        });
        var vm = new PromptLibraryViewModel(() => host, ImmediateDispatcher.Instance);
        await vm.RefreshAsync();

        vm.SkillQuery = "pdf";
        await vm.BrowseRegistryCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.RegistrySkills.Count);
        Assert.Equal("pdf", host.LastQuery);
        var official = vm.RegistrySkills.Single(r => r.RegistryId == "skillshub");
        Assert.Contains("Anthropic", official.Provenance, StringComparison.Ordinal);
        Assert.Contains("96,108", official.Provenance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registry_that_cannot_answer_is_named_rather_than_looking_empty()
    {
        var host = new FakeLibraryHost();
        host.Registry("local-dir", new RegistrySkillEntry("pdf", "PDF tools", null, "/skills/pdf"));
        host.FailingRegistry("skillshub", "429 rate limited");
        var vm = new PromptLibraryViewModel(() => host, ImmediateDispatcher.Instance);

        await vm.RefreshAsync();

        Assert.Single(vm.RegistrySkills);
        Assert.Contains("rate limited", vm.SkillStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_offered_skill_can_be_installed_and_then_reads_as_installed()
    {
        var host = new FakeLibraryHost();
        host.Registry("local-dir", new RegistrySkillEntry("pdf", "PDF tools", null, "/skills/pdf"));
        var vm = new PromptLibraryViewModel(() => host, ImmediateDispatcher.Instance);
        await vm.RefreshAsync();

        var row = Assert.Single(vm.RegistrySkills);
        Assert.True(row.CanInstall);
        Assert.Equal("Install", row.ActionLabel);

        await vm.InstallSkillCommand.ExecuteAsync(row);

        Assert.Equal("PDF tools", Assert.Single(vm.Skills).Title);
        var refreshed = Assert.Single(vm.RegistrySkills);
        Assert.False(refreshed.CanInstall);
        Assert.Equal("Installed", refreshed.ActionLabel);
    }

    [Fact]
    public async Task With_every_registry_turned_off_the_surface_says_so_rather_than_showing_nothing()
    {
        var vm = new PromptLibraryViewModel(() => new FakeLibraryHost(), ImmediateDispatcher.Instance);

        await vm.RefreshAsync();

        Assert.False(vm.HasSkillRegistries);
        Assert.Contains("no skill registry is available", vm.SkillStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Editing_a_saved_prompt_is_visible_in_the_editor()
    {
        var host = new FakeLibraryHost();
        host.Prompt(new LibraryPrompt("p1", "Security review", "Look for injection."));
        var vm = new PromptLibraryViewModel(() => host, ImmediateDispatcher.Instance);
        await vm.RefreshAsync();

        Assert.False(vm.IsEditingExistingPrompt);
        Assert.Equal("New prompt", vm.EditorHeading);
        Assert.Equal("Save prompt", vm.SaveLabel);

        vm.EditPromptCommand.Execute(vm.Prompts[0]);

        Assert.True(vm.IsEditingExistingPrompt);
        Assert.Contains("Security review", vm.EditorHeading, StringComparison.Ordinal);
        Assert.Equal("Save changes", vm.SaveLabel);
    }

    [Fact]
    public async Task Cancelling_an_edit_returns_the_editor_to_composing_a_new_prompt()
    {
        var host = new FakeLibraryHost();
        host.Prompt(new LibraryPrompt("p1", "Security review", "Look for injection."));
        var vm = new PromptLibraryViewModel(() => host, ImmediateDispatcher.Instance);
        await vm.RefreshAsync();
        vm.EditPromptCommand.Execute(vm.Prompts[0]);

        vm.CancelEditCommand.Execute(null);

        Assert.False(vm.IsEditingExistingPrompt);
        Assert.Equal(string.Empty, vm.PromptTitle);
        Assert.Equal(string.Empty, vm.PromptBody);
    }

    [Fact]
    public async Task Saving_an_edit_replaces_that_prompt_instead_of_adding_another()
    {
        var host = new FakeLibraryHost();
        host.Prompt(new LibraryPrompt("p1", "Security review", "Look for injection."));
        var vm = new PromptLibraryViewModel(() => host, ImmediateDispatcher.Instance);
        await vm.RefreshAsync();
        vm.EditPromptCommand.Execute(vm.Prompts[0]);

        vm.PromptBody = "Look for injection and secrets.";
        await vm.SavePromptCommand.ExecuteAsync(null);

        var stored = Assert.Single(vm.Prompts);
        Assert.Equal("Look for injection and secrets.", stored.MarkdownBody);
        // Back to a blank editor, so the next Save can't quietly overwrite what was just edited.
        Assert.False(vm.IsEditingExistingPrompt);
    }

    /// <summary>An in-memory library: prompts, skills, and any number of registries of offered skills — one of
    /// which can be made to fail, the way a rate-limited public index does.</summary>
    private sealed class FakeLibraryHost : StubAgnesHost
    {
        private readonly List<LibraryPrompt> _prompts = [];
        private readonly List<LibrarySkill> _skills = [];
        private readonly Dictionary<string, List<RegistrySkillEntry>> _registries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _failing = new(StringComparer.Ordinal);

        /// <summary>The query the last search was run with, so a test can prove it reached the registries.</summary>
        public string? LastQuery { get; private set; }

        public void Prompt(LibraryPrompt prompt) => _prompts.Add(prompt);

        public void Registry(string id, params RegistrySkillEntry[] entries)
            => _registries[id] = [.. entries];

        /// <summary>A registry that's registered but can't answer right now.</summary>
        public void FailingRegistry(string id, string reason)
        {
            _registries[id] = [];
            _failing[id] = reason;
        }

        public override Task<CatalogResults<RegistrySkillEntry>> SearchSkillsAsync(string query)
        {
            LastQuery = query;
            var hits = _registries
                .Where(r => !_failing.ContainsKey(r.Key))
                .SelectMany(r => r.Value
                    .Where(e => query.Length == 0 || e.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(e => new CatalogHit<RegistrySkillEntry>(r.Key, r.Key, e)))
                .ToArray();
            var failures = _failing.Select(f => $"{f.Key}: {f.Value}").ToArray();
            return Task.FromResult(new CatalogResults<RegistrySkillEntry>(hits, failures));
        }

        public override Task<IReadOnlyList<LibraryPrompt>> GetPromptsAsync()
            => Task.FromResult<IReadOnlyList<LibraryPrompt>>(_prompts.ToArray());

        public override Task<LibraryPrompt> SavePromptAsync(LibraryPrompt prompt)
        {
            var index = _prompts.FindIndex(p => p.Id == prompt.Id);
            if (index >= 0)
            {
                _prompts[index] = prompt;
                return Task.FromResult(prompt);
            }

            var added = prompt with { Id = Guid.NewGuid().ToString("n") };
            _prompts.Add(added);
            return Task.FromResult(added);
        }

        public override Task<IReadOnlyList<LibrarySkill>> GetSkillsAsync()
            => Task.FromResult<IReadOnlyList<LibrarySkill>>(_skills.ToArray());

        public override Task<IReadOnlyList<CatalogSource>> GetSkillRegistriesAsync()
            => Task.FromResult<IReadOnlyList<CatalogSource>>(
                _registries.Keys.Select(k => new CatalogSource(k, k, SupportsSearch: true)).ToArray());

        public override Task<IReadOnlyList<RegistrySkillEntry>> GetRegistrySkillsAsync(string registryId)
            => Task.FromResult<IReadOnlyList<RegistrySkillEntry>>(
                _registries.TryGetValue(registryId, out var entries) ? entries.ToArray() : []);

        public override Task<LibrarySkill> InstallSkillFromRegistryAsync(string registryId, string entryId)
        {
            var entry = _registries[registryId].Single(e => e.Id == entryId);
            var skill = new LibrarySkill(Guid.NewGuid().ToString("n"), entry.Title, $"/library/{entry.Id}/SKILL.md", []);
            _skills.Add(skill);
            return Task.FromResult(skill);
        }
    }
}
