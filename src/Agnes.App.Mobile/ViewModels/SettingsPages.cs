using System.Collections.ObjectModel;
using Agnes.Abstractions;
using Agnes.App.Mobile.Services;
using Agnes.Protocol;
using Agnes.Client;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>Theme, text size, and what the transcript shows.</summary>
public sealed partial class AppearancePageViewModel : PageViewModel
{
    private readonly IAppShell _shell;

    public AppearancePageViewModel(IAppShell shell)
    {
        _shell = shell;
        SetThemeCommand = new RelayCommand<string>(theme =>
        {
            if (theme is null)
            {
                return;
            }

            Shell.UpdateSettings(s => s with { Theme = theme });
            ThemeApplier.Apply(theme);
            RaiseTheme();
            _shell.Haptics.Tick();
        });
        SetScaleCommand = new RelayCommand<string>(scale =>
        {
            var value = scale switch { "small" => 0.9, "large" => 1.15, "xlarge" => 1.3, _ => 1.0 };
            Shell.UpdateSettings(s => s with { TextScale = value });
            RaiseScale();
            _shell.Haptics.Tick();
        });
        ToggleThinkingCommand = new RelayCommand(() =>
        {
            Shell.UpdateSettings(s => s with { ShowThinking = !s.ShowThinking });
            OnPropertyChanged(nameof(ShowThinking));
        });
        ToggleMotionCommand = new RelayCommand(() =>
        {
            Shell.UpdateSettings(s => s with { ReducedMotion = !s.ReducedMotion });
            OnPropertyChanged(nameof(ReducedMotion));
        });
    }

    private ShellViewModel Shell => (ShellViewModel)_shell;

    public override string Title => "Appearance";


    public IRelayCommand<string> SetThemeCommand { get; }
    public IRelayCommand<string> SetScaleCommand { get; }
    public IRelayCommand ToggleThinkingCommand { get; }
    public IRelayCommand ToggleMotionCommand { get; }

    public bool ThemeSystem => _shell.Settings.Theme is not "Light" and not "Dark";
    public bool ThemeLight => _shell.Settings.Theme == "Light";
    public bool ThemeDark => _shell.Settings.Theme == "Dark";

    public bool ScaleSmall => _shell.Settings.TextScale < 0.95;
    public bool ScaleNormal => _shell.Settings.TextScale is >= 0.95 and < 1.1;
    public bool ScaleLarge => _shell.Settings.TextScale is >= 1.1 and < 1.25;
    public bool ScaleXLarge => _shell.Settings.TextScale >= 1.25;

    /// <summary>Whether the agent's reasoning is shown inline. Off by default: on a phone the thinking is
    /// usually noise between you and the answer.</summary>
    public bool ShowThinking => _shell.Settings.ShowThinking;

    public bool ReducedMotion => _shell.Settings.ReducedMotion;

    private void RaiseTheme()
    {
        OnPropertyChanged(nameof(ThemeSystem));
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(ThemeDark));
    }

    private void RaiseScale()
    {
        OnPropertyChanged(nameof(ScaleSmall));
        OnPropertyChanged(nameof(ScaleNormal));
        OnPropertyChanged(nameof(ScaleLarge));
        OnPropertyChanged(nameof(ScaleXLarge));
    }
}

/// <summary>When the phone should interrupt you, and whether it buzzes when it does.</summary>
public sealed partial class NotificationsPageViewModel : PageViewModel
{
    private readonly IAppShell _shell;

    public NotificationsPageViewModel(IAppShell shell)
    {
        _shell = shell;
        ToggleBlockedCommand = new RelayCommand(() =>
        {
            Shell.UpdateSettings(s => s with { NotifyOnBlocked = !s.NotifyOnBlocked });
            OnPropertyChanged(nameof(NotifyOnBlocked));
        });
        ToggleCompleteCommand = new RelayCommand(() =>
        {
            Shell.UpdateSettings(s => s with { NotifyOnComplete = !s.NotifyOnComplete });
            OnPropertyChanged(nameof(NotifyOnComplete));
        });
        ToggleHapticsCommand = new RelayCommand(() =>
        {
            Shell.UpdateSettings(s => s with { Haptics = !s.Haptics });
            OnPropertyChanged(nameof(Haptics));
            if (_shell.Settings.Haptics)
            {
                _shell.Haptics.Success();
            }
        });
    }

    private ShellViewModel Shell => (ShellViewModel)_shell;

    public override string Title => "Notifications";


    public IRelayCommand ToggleBlockedCommand { get; }
    public IRelayCommand ToggleCompleteCommand { get; }
    public IRelayCommand ToggleHapticsCommand { get; }

    public bool NotifyOnBlocked => _shell.Settings.NotifyOnBlocked;
    public bool NotifyOnComplete => _shell.Settings.NotifyOnComplete;
    public bool Haptics => _shell.Settings.Haptics;
}

/// <summary>The host's saved prompts, so a long instruction you use often isn't retyped on a phone
/// keyboard. Tapping one copies it; the composer's slash tokens expand the same library inline.</summary>
public sealed partial class PromptsPageViewModel : PageViewModel
{
    private readonly IAppShell _shell;

    public PromptsPageViewModel(IAppShell shell)
    {
        _shell = shell;
        Library = new PromptLibraryViewModel(
            () => _shell.Hosts.Links.FirstOrDefault(l => l.IsOnline && !l.IsBuiltIn)?.Host
                  ?? _shell.Hosts.Links.FirstOrDefault(l => l.IsOnline)?.Host,
            shell.Dispatcher);
        CopyCommand = new RelayCommand<Agnes.Abstractions.LibraryPrompt>(p =>
        {
            if (p is not null)
            {
                _shell.CopyToClipboard(p.MarkdownBody, p.Title);
            }
        });
        Library.RefreshCommand.Execute(null);
    }

    public override string Title => "Prompts";


    public PromptLibraryViewModel Library { get; }

    public IRelayCommand<Agnes.Abstractions.LibraryPrompt> CopyCommand { get; }
}

/// <summary>The devices paired with a host, and the ability to revoke one. Worth having on a phone: if
/// you lose a laptop, this is the fastest way to cut it off.</summary>
public sealed partial class DevicesPageViewModel : PageViewModel
{
    private readonly IAppShell _shell;

    public DevicesPageViewModel(IAppShell shell)
    {
        _shell = shell;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        RevokeCommand = new AsyncRelayCommand<DeviceInfo>(RevokeAsync);
        _ = LoadAsync();
    }

    public override string Title => "Paired devices";


    public ObservableCollection<DeviceInfo> Devices { get; } = [];

    [ObservableProperty]
    private string _status = "Loading…";

    [ObservableProperty]
    private bool _isBusy;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<DeviceInfo> RevokeCommand { get; }

    private HostLink? Target => _shell.Hosts.Real.FirstOrDefault(l => l.IsOnline);

    private async Task LoadAsync()
    {
        if (Target is not { } link)
        {
            _shell.Dispatcher.Post(() => { Devices.Clear(); Status = "Connect a host to manage its devices."; });
            return;
        }

        _shell.Dispatcher.Post(() => { IsBusy = true; Status = "Loading…"; });
        try
        {
            var list = await DeviceManagement.ListAsync(link.Url, link.Saved.Token).ConfigureAwait(false);
            _shell.Dispatcher.Post(() =>
            {
                Devices.Clear();
                foreach (var device in list)
                {
                    Devices.Add(device);
                }

                Status = list.Count == 0 ? "No paired devices." : $"{list.Count} paired with {link.Name}.";
            });
        }
        catch (Exception ex)
        {
            _shell.Dispatcher.Post(() => Status = "Couldn't load devices: " + ex.Message);
        }
        finally
        {
            _shell.Dispatcher.Post(() => IsBusy = false);
        }
    }

    private async Task RevokeAsync(DeviceInfo? device)
    {
        if (device is null || Target is not { } link)
        {
            return;
        }

        try
        {
            await DeviceManagement.RevokeAsync(link.Url, link.Saved.Token, device.Id).ConfigureAwait(false);
            _shell.Toast("Revoked", ToastKind.Success);
            await LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _shell.Toast("Couldn't revoke: " + ex.Message, ToastKind.Danger);
        }
    }
}

/// <summary>What this is, and where to read more.</summary>
public sealed partial class AboutPageViewModel : PageViewModel
{
    private readonly IAppShell _shell;

    public AboutPageViewModel(IAppShell shell)
    {
        _shell = shell;
        RepoCommand = new RelayCommand(() => _shell.OpenUrl("https://github.com/AdamFrisby/Agnes"));
        DocsCommand = new RelayCommand(() => _shell.OpenUrl("https://github.com/AdamFrisby/Agnes/blob/main/docs/architecture.md"));
        SiteCommand = new RelayCommand(() => _shell.OpenUrl("https://multitudal.dev"));
    }

    public override string Title => "About";


    public string Version => typeof(AboutPageViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public IRelayCommand RepoCommand { get; }
    public IRelayCommand DocsCommand { get; }
    public IRelayCommand SiteCommand { get; }
}
