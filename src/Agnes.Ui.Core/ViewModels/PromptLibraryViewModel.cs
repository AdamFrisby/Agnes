using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.Client;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Drives the prompt-library surface: the host's saved prompts and the slash-token templates that expand
/// them. Host-agnostic — it talks to whatever <see cref="IAgnesHost"/> the accessor returns, so it drives a
/// real SignalR host and the offline simulation identically, and every change goes over the wire. A template
/// whose referenced prompt no longer resolves is surfaced as <see cref="PromptTemplateRow.IsBroken"/> rather
/// than silently dropped (the "clearly-broken, visibly-flagged" acceptance criterion).
/// </summary>
public sealed class PromptLibraryViewModel : ObservableObject
{
    private readonly Func<IAgnesHost?> _host;
    private readonly IUiDispatcher _dispatcher;

    public PromptLibraryViewModel(Func<IAgnesHost?> host, IUiDispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        NewPromptCommand = new RelayCommand(BeginNewPrompt);
        EditPromptCommand = new RelayCommand<LibraryPrompt>(BeginEditPrompt);
        SavePromptCommand = new AsyncRelayCommand(SavePromptAsync, () => CanSavePrompt);
        DeletePromptCommand = new AsyncRelayCommand<LibraryPrompt>(DeletePromptAsync);
        SaveTemplateCommand = new AsyncRelayCommand(SaveTemplateAsync, () => CanSaveTemplate);
        DeleteTemplateCommand = new AsyncRelayCommand<PromptTemplateRow>(DeleteTemplateAsync);
        DeleteSkillCommand = new AsyncRelayCommand<LibrarySkill>(DeleteSkillAsync);
        CancelEditCommand = new RelayCommand(BeginNewPrompt);
        BrowseRegistryCommand = new AsyncRelayCommand(BrowseRegistriesAsync);
        InstallSkillCommand = new AsyncRelayCommand<RegistrySkillRow>(InstallSkillAsync);
    }

    /// <summary>The host's saved prompts.</summary>
    public ObservableCollection<LibraryPrompt> Prompts { get; } = [];

    /// <summary>The host's templates, each flagged if its referenced prompt is missing.</summary>
    public ObservableCollection<PromptTemplateRow> Templates { get; } = [];

    /// <summary>The host's saved skill bundles (SKILL.md + supporting files, managed as a unit).</summary>
    public ObservableCollection<LibrarySkill> Skills { get; } = [];

    /// <summary>The registry sources this host offers skills from (a host with none configured reports an
    /// empty list — which the surface has to explain rather than just showing nothing).</summary>
    public ObservableCollection<CatalogSource> SkillRegistries { get; } = [];

    /// <summary>
    /// What the registries are currently offering: their front pages when nothing has been typed, and the
    /// results of <see cref="SkillQuery"/> once something has. Every row names the registry it came from and
    /// is flagged if the library already holds it.
    /// </summary>
    public ObservableCollection<RegistrySkillRow> RegistrySkills { get; } = [];

    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    /// <summary>
    /// What to look for, across every registry at once. Searching all of them beats picking one first: a
    /// person wants "a skill that does X", and which index happens to hold it is the registry's problem, not
    /// theirs. The source is still shown on each result.
    /// </summary>
    private string _skillQuery = string.Empty;
    public string SkillQuery { get => _skillQuery; set => SetProperty(ref _skillQuery, value); }

    private string _skillStatus = string.Empty;

    /// <summary>What the skills area has to say for itself — including <em>why</em> it's empty, which may be
    /// "no registry is configured", "nothing matched", or "one of them is rate-limited right now".</summary>
    public string SkillStatus { get => _skillStatus; set => SetProperty(ref _skillStatus, value); }

    public bool HasSkillRegistries => SkillRegistries.Count > 0;

    /// <summary>True while a registry query is in flight, so the surface can say it's working — these are
    /// network calls to third-party indexes and can take a moment.</summary>
    private bool _isSearchingSkills;
    public bool IsSearchingSkills { get => _isSearchingSkills; set => SetProperty(ref _isSearchingSkills, value); }

    // ---- prompt editor (id null = a new prompt) ----
    private string? _editingPromptId;
    public string? EditingPromptId
    {
        get => _editingPromptId;
        private set
        {
            if (SetProperty(ref _editingPromptId, value))
            {
                OnPropertyChanged(nameof(IsEditingExistingPrompt));
                OnPropertyChanged(nameof(EditorHeading));
                OnPropertyChanged(nameof(SaveLabel));
            }
        }
    }

    /// <summary>True while the editor is overwriting a saved prompt rather than composing a new one. The
    /// editor is a single set of fields for both jobs, so which job it's doing has to be visible — otherwise
    /// Save silently overwrites the prompt you last clicked Edit on.</summary>
    public bool IsEditingExistingPrompt => _editingPromptId is not null;

    /// <summary>Names what Save will do, with the title of the prompt being overwritten when there is one.</summary>
    public string EditorHeading => _editingPromptId is null
        ? "New prompt"
        : $"Editing “{Prompts.FirstOrDefault(p => p.Id == _editingPromptId)?.Title ?? "prompt"}”";

    public string SaveLabel => _editingPromptId is null ? "Save prompt" : "Save changes";

    private string _promptTitle = string.Empty;
    public string PromptTitle { get => _promptTitle; set { if (SetProperty(ref _promptTitle, value)) { RaiseCanSavePrompt(); } } }

    private string _promptBody = string.Empty;
    public string PromptBody { get => _promptBody; set { if (SetProperty(ref _promptBody, value)) { RaiseCanSavePrompt(); } } }

    /// <summary>When true the edited prompt is saved as a system-prompt addition (prepended to a session's
    /// system prompt at open) rather than a per-message snippet.</summary>
    private bool _promptIsSystemAddition;
    public bool PromptIsSystemAddition { get => _promptIsSystemAddition; set => SetProperty(ref _promptIsSystemAddition, value); }

    public bool CanSavePrompt => !string.IsNullOrWhiteSpace(_promptTitle) && !string.IsNullOrWhiteSpace(_promptBody);

    // ---- template editor ----
    private string _templateToken = string.Empty;
    public string TemplateToken { get => _templateToken; set { if (SetProperty(ref _templateToken, value)) { RaiseCanSaveTemplate(); } } }

    private string _templatePromptId = string.Empty;
    public string TemplatePromptId { get => _templatePromptId; set { if (SetProperty(ref _templatePromptId, value)) { RaiseCanSaveTemplate(); } } }

    /// <summary>Maps to <see cref="TemplateBehavior.InsertAndSend"/> when true, else <see cref="TemplateBehavior.Insert"/>.</summary>
    private bool _templateSendImmediately;
    public bool TemplateSendImmediately { get => _templateSendImmediately; set => SetProperty(ref _templateSendImmediately, value); }

    public bool CanSaveTemplate => !string.IsNullOrWhiteSpace(_templateToken) && !string.IsNullOrWhiteSpace(_templatePromptId);

    public ICommand RefreshCommand { get; }
    public ICommand NewPromptCommand { get; }
    public ICommand EditPromptCommand { get; }
    public IAsyncRelayCommand SavePromptCommand { get; }
    public ICommand DeletePromptCommand { get; }
    public IAsyncRelayCommand SaveTemplateCommand { get; }
    public ICommand DeleteTemplateCommand { get; }
    public ICommand DeleteSkillCommand { get; }

    /// <summary>Abandons an in-progress edit and returns the editor to composing a new prompt.</summary>
    public ICommand CancelEditCommand { get; }

    /// <summary>Runs <see cref="SkillQuery"/> against every registry (or re-lists them when it's blank).</summary>
    public IAsyncRelayCommand BrowseRegistryCommand { get; }

    public IAsyncRelayCommand<RegistrySkillRow> InstallSkillCommand { get; }

    /// <summary>Loads prompts and templates from the host and rebuilds both lists (recomputing broken flags).</summary>
    public async Task RefreshAsync()
    {
        var host = _host();
        if (host is null)
        {
            _dispatcher.Post(() =>
            {
                Prompts.Clear();
                Templates.Clear();
                Status = "Connect to a host to manage prompts.";
                SkillStatus = string.Empty;
            });
            return;
        }

        try
        {
            var prompts = await host.GetPromptsAsync().ConfigureAwait(false);
            var templates = await host.GetPromptTemplatesAsync().ConfigureAwait(false);
            var skills = await host.GetSkillsAsync().ConfigureAwait(false);
            var registries = await host.GetSkillRegistriesAsync().ConfigureAwait(false);
            _dispatcher.Post(() => Rebuild(prompts, templates, skills, registries));

            if (registries.Count > 0)
            {
                await BrowseRegistriesAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't load the prompt library: " + ex.Message);
        }
    }

    /// <summary>
    /// Asks every registry for <see cref="SkillQuery"/> at once — or, with nothing typed, for what each one
    /// leads with. Registries that couldn't answer are named in the status rather than passed over: a
    /// rate-limited index looks exactly like an empty one otherwise.
    /// </summary>
    private async Task BrowseRegistriesAsync()
    {
        var host = _host();
        if (host is null)
        {
            _dispatcher.Post(RegistrySkills.Clear);
            return;
        }

        var query = SkillQuery?.Trim() ?? string.Empty;
        try
        {
            _dispatcher.Post(() => IsSearchingSkills = true);
            var results = await host.SearchSkillsAsync(query).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                RegistrySkills.Clear();
                foreach (var hit in results.Hits)
                {
                    RegistrySkills.Add(new RegistrySkillRow(hit.CatalogId, hit.CatalogName, hit.Entry, IsInstalled(hit.Entry.Title)));
                }

                SkillStatus = Describe(results, query);
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SkillStatus = "Couldn't reach the skill registries: " + ex.Message);
        }
        finally
        {
            _dispatcher.Post(() => IsSearchingSkills = false);
        }
    }

    private static string Describe(CatalogResults<RegistrySkillEntry> results, string query)
    {
        var found = results.Hits.Count switch
        {
            0 when query.Length > 0 => $"Nothing matched '{query}'.",
            0 => "The registries are offering nothing right now.",
            var n when query.Length > 0 => $"{n} match(es) for '{query}'.",
            var n => $"{n} skill(s) offered.",
        };

        return results.Failures.Count == 0 ? found : $"{found} Couldn't reach: {string.Join("; ", results.Failures)}";
    }

    /// <summary>Fetches a registry skill into the host's library, then refreshes so it appears as installed.</summary>
    private async Task InstallSkillAsync(RegistrySkillRow? row)
    {
        var host = _host();
        if (host is null || row is null)
        {
            return;
        }

        try
        {
            _dispatcher.Post(() => SkillStatus = $"Installing '{row.Title}'…");
            var installed = await host.InstallSkillFromRegistryAsync(row.RegistryId, row.EntryId).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            _dispatcher.Post(() => SkillStatus = $"Installed '{installed.Title}'.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => SkillStatus = $"Couldn't install '{row.Title}': " + ex.Message);
        }
    }

    /// <summary>A registry entry is "installed" when the library already holds a skill of that title — the
    /// library's ids are its own, so the title is the only thing the two sides share.</summary>
    private bool IsInstalled(string title)
        => Skills.Any(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase));

    private void Rebuild(
        IReadOnlyList<LibraryPrompt> prompts,
        IReadOnlyList<PromptTemplate> templates,
        IReadOnlyList<LibrarySkill> skills,
        IReadOnlyList<CatalogSource> registries)
    {
        Prompts.Clear();
        foreach (var p in prompts)
        {
            Prompts.Add(p);
        }

        RebuildTemplates(templates);

        Skills.Clear();
        foreach (var s in skills)
        {
            Skills.Add(s);
        }

        SkillRegistries.Clear();
        foreach (var r in registries)
        {
            SkillRegistries.Add(r);
        }

        OnPropertyChanged(nameof(HasSkillRegistries));
        OnPropertyChanged(nameof(EditorHeading));

        // An empty skills area has two very different causes; say which one it is.
        if (registries.Count == 0)
        {
            RegistrySkills.Clear();
            SkillStatus = "No skill registry is available on this host, so nothing can be installed from here — they may all be turned off (Agnes:Registries:…:Enabled).";
        }

        Status = $"{Prompts.Count} prompt(s), {Templates.Count} template(s), {Skills.Count} skill(s).";
    }

    private void RebuildTemplates(IReadOnlyList<PromptTemplate> templates)
    {
        Templates.Clear();
        foreach (var t in templates)
        {
            var prompt = Prompts.FirstOrDefault(p => p.Id == t.PromptId);
            Templates.Add(new PromptTemplateRow(t, prompt?.Title));
        }
    }

    private void BeginNewPrompt()
    {
        EditingPromptId = null;
        PromptTitle = string.Empty;
        PromptBody = string.Empty;
        PromptIsSystemAddition = false;
    }

    private void BeginEditPrompt(LibraryPrompt? prompt)
    {
        if (prompt is null)
        {
            return;
        }

        EditingPromptId = prompt.Id;
        PromptTitle = prompt.Title;
        PromptBody = prompt.MarkdownBody;
        PromptIsSystemAddition = prompt.IsSystemPromptAddition;
    }

    private async Task SavePromptAsync()
    {
        var host = _host();
        if (host is null || !CanSavePrompt)
        {
            return;
        }

        var prompt = new LibraryPrompt(EditingPromptId ?? string.Empty, PromptTitle.Trim(), PromptBody)
        {
            IsSystemPromptAddition = PromptIsSystemAddition,
        };
        try
        {
            await host.SavePromptAsync(prompt).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            _dispatcher.Post(BeginNewPrompt);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't save the prompt: " + ex.Message);
        }
    }

    private async Task DeletePromptAsync(LibraryPrompt? prompt)
    {
        var host = _host();
        if (host is null || prompt is null)
        {
            return;
        }

        try
        {
            await host.DeletePromptAsync(prompt.Id).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't delete the prompt: " + ex.Message);
        }
    }

    private async Task SaveTemplateAsync()
    {
        var host = _host();
        if (host is null || !CanSaveTemplate)
        {
            return;
        }

        var behavior = TemplateSendImmediately ? TemplateBehavior.InsertAndSend : TemplateBehavior.Insert;
        var template = new PromptTemplate(TemplateToken.Trim(), TemplatePromptId, behavior);
        try
        {
            await host.SavePromptTemplateAsync(template).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            _dispatcher.Post(() => { TemplateToken = string.Empty; TemplatePromptId = string.Empty; TemplateSendImmediately = false; });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't save the template: " + ex.Message);
        }
    }

    private async Task DeleteTemplateAsync(PromptTemplateRow? row)
    {
        var host = _host();
        if (host is null || row is null)
        {
            return;
        }

        try
        {
            await host.DeletePromptTemplateAsync(row.Template.SlashToken).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't delete the template: " + ex.Message);
        }
    }

    private async Task DeleteSkillAsync(LibrarySkill? skill)
    {
        var host = _host();
        if (host is null || skill is null)
        {
            return;
        }

        try
        {
            await host.DeleteSkillAsync(skill.Id).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't delete the skill: " + ex.Message);
        }
    }

    private void RaiseCanSavePrompt()
    {
        OnPropertyChanged(nameof(CanSavePrompt));
        SavePromptCommand.NotifyCanExecuteChanged();
    }

    private void RaiseCanSaveTemplate()
    {
        OnPropertyChanged(nameof(CanSaveTemplate));
        SaveTemplateCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// A registry-offered skill as a bindable row, carrying the registry it came from (so installing needs no
/// ambient selection) and whether the library already holds it (so the surface offers Install once, not
/// every time you look at it).
/// </summary>
public sealed class RegistrySkillRow
{
    public RegistrySkillRow(string registryId, string registryName, RegistrySkillEntry entry, bool isInstalled)
    {
        RegistryId = registryId;
        RegistryName = registryName;
        Entry = entry;
        IsInstalled = isInstalled;
    }

    public string RegistryId { get; }

    /// <summary>Which registry offered it. Results from several are shown together, and a public index can
    /// hold a dozen skills of the same name, so the row has to say where this one came from.</summary>
    public string RegistryName { get; }

    public RegistrySkillEntry Entry { get; }

    public bool IsInstalled { get; }

    public string EntryId => Entry.Id;

    public string Title => Entry.Title;

    public string Description => Entry.Description ?? "No description.";

    public string Source => Entry.Source;

    /// <summary>
    /// Who published it and how popular it is, in one line — the facts that separate three identically-named
    /// "pdf" skills. Blank when the registry reports neither, so a local directory shows nothing extra.
    /// </summary>
    public string Provenance
    {
        get
        {
            var parts = new List<string>();
            if (Entry.Publisher is { Length: > 0 } publisher)
            {
                parts.Add(publisher);
            }

            if (Entry.Stars is > 0 and var stars)
            {
                parts.Add($"★ {stars:N0}");
            }

            if (Entry.Downloads is > 0 and var downloads)
            {
                parts.Add($"{downloads:N0} installs");
            }

            parts.Add(RegistryName);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>What the row's action reads as: nothing to do when it's already in the library.</summary>
    public string ActionLabel => IsInstalled ? "Installed" : "Install";

    public bool CanInstall => !IsInstalled;
}

/// <summary>A template as a bindable row; <see cref="IsBroken"/> is true when its referenced prompt is gone.</summary>
public sealed class PromptTemplateRow
{
    public PromptTemplateRow(PromptTemplate template, string? promptTitle)
    {
        Template = template;
        PromptTitle = promptTitle;
    }

    public PromptTemplate Template { get; }

    /// <summary>The referenced prompt's title, or null if it no longer exists.</summary>
    public string? PromptTitle { get; }

    public string SlashToken => Template.SlashToken;

    public bool SendImmediately => Template.Behavior == TemplateBehavior.InsertAndSend;

    /// <summary>True when the referenced prompt is missing — the template is broken and must show as such.</summary>
    public bool IsBroken => PromptTitle is null;

    /// <summary>What the row shows for the target: the prompt title, or a broken marker.</summary>
    public string TargetLabel => PromptTitle ?? "⚠ missing prompt";
}
