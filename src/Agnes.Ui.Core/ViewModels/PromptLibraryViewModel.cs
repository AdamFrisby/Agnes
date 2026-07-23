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
    }

    /// <summary>The host's saved prompts.</summary>
    public ObservableCollection<LibraryPrompt> Prompts { get; } = [];

    /// <summary>The host's templates, each flagged if its referenced prompt is missing.</summary>
    public ObservableCollection<PromptTemplateRow> Templates { get; } = [];

    /// <summary>The host's saved skill bundles (SKILL.md + supporting files, managed as a unit).</summary>
    public ObservableCollection<LibrarySkill> Skills { get; } = [];

    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    // ---- prompt editor (id null = a new prompt) ----
    private string? _editingPromptId;
    public string? EditingPromptId { get => _editingPromptId; private set => SetProperty(ref _editingPromptId, value); }

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

    /// <summary>Loads prompts and templates from the host and rebuilds both lists (recomputing broken flags).</summary>
    public async Task RefreshAsync()
    {
        var host = _host();
        if (host is null)
        {
            _dispatcher.Post(() => { Prompts.Clear(); Templates.Clear(); Status = "Connect to a host to manage prompts."; });
            return;
        }

        try
        {
            var prompts = await host.GetPromptsAsync().ConfigureAwait(false);
            var templates = await host.GetPromptTemplatesAsync().ConfigureAwait(false);
            var skills = await host.GetSkillsAsync().ConfigureAwait(false);
            _dispatcher.Post(() => Rebuild(prompts, templates, skills));
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't load the prompt library: " + ex.Message);
        }
    }

    private void Rebuild(IReadOnlyList<LibraryPrompt> prompts, IReadOnlyList<PromptTemplate> templates, IReadOnlyList<LibrarySkill> skills)
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
