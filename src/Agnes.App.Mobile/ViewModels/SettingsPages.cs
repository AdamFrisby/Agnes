using System.Collections.ObjectModel;
using Agnes.Abstractions;
using Agnes.App.Mobile.Services;
using Agnes.Protocol;
using Agnes.Client;
using Agnes.Ui.Core;
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
        ToggleScreenDataCommand = new RelayCommand(() =>
        {
            Shell.UpdateSettings(s => s with
            {
                LowerScreenQualityOnMobileData = !s.LowerScreenQualityOnMobileData,
            });
            OnPropertyChanged(nameof(LowerScreenQualityOnMobileData));
        });
    }

    private ShellViewModel Shell => (ShellViewModel)_shell;

    public override string Title => "Appearance";


    public IRelayCommand<string> SetThemeCommand { get; }
    public IRelayCommand<string> SetScaleCommand { get; }
    public IRelayCommand ToggleThinkingCommand { get; }
    public IRelayCommand ToggleMotionCommand { get; }
    public IRelayCommand ToggleScreenDataCommand { get; }

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

    /// <summary>Whether a graphical session drops to the cheap tier on a metered network. On by default:
    /// a screen stream is the only thing in this app that can quietly spend a data plan, and the cheap
    /// tier still answers "what is it doing".</summary>
    public bool LowerScreenQualityOnMobileData => _shell.Settings.LowerScreenQualityOnMobileData;

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
        ToggleFileCommand = new RelayCommand(() =>
        {
            Shell.UpdateSettings(s => s with { NotifyOnFile = !s.NotifyOnFile });
            OnPropertyChanged(nameof(NotifyOnFile));
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

    /// <summary>Whether a file an agent sends should reach the shade. Its own switch rather than riding
    /// on "a turn finished": a file is the one thing you might want told about even when you've turned the
    /// chatter off, because it's the only kind of notification with something to take away from it.</summary>
    public IRelayCommand ToggleFileCommand { get; }

    public IRelayCommand ToggleHapticsCommand { get; }

    public bool NotifyOnBlocked => _shell.Settings.NotifyOnBlocked;
    public bool NotifyOnComplete => _shell.Settings.NotifyOnComplete;
    public bool NotifyOnFile => _shell.Settings.NotifyOnFile;
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

/// <summary>
/// One paired device on the phone's Devices page. The protocol record alone can't say when a device was
/// last used in words, what its role is called, or how it got in — and those three are what turn a list of
/// names into a list you can act on. It also holds the row's own role button, since promoting is per-row.
/// </summary>
public sealed class MobileDeviceRow
{
    public MobileDeviceRow(DeviceInfo info, DateTimeOffset now, bool isLastOwner)
    {
        Info = info;
        IsLastOwner = isLastOwner;
        LastSeen = Describe(info, now);
    }

    public DeviceInfo Info { get; }

    public string Id => Info.Id;
    public string Name => Info.Name;
    public bool IsCurrentDevice => Info.IsCurrentDevice;
    public bool IsOwner => Info.Role == DeviceRole.Owner;

    /// <summary>"Owner" / "Member" — a neutral label, never a status hue.</summary>
    public string RoleChip => DeviceRoleText.Chip(Info.Role);

    /// <summary>How it was admitted, in words; empty for a kind this build doesn't know.</summary>
    public string Admission => DeviceRoleText.Admission(Info.Kind) ?? string.Empty;

    /// <summary>Pairing date and last use, the two facts that identify a device you meant to remove.</summary>
    public string LastSeen { get; }

    public DeviceRole TargetRole => IsOwner ? DeviceRole.Member : DeviceRole.Owner;

    public string RoleActionLabel => IsOwner ? "Make member" : "Make owner";

    /// <summary>The only owner left can't be demoted — the host refuses, and offering it anyway would be
    /// a button whose whole purpose is to fail.</summary>
    public bool IsLastOwner { get; }

    public bool CanChangeRole => !(IsOwner && IsLastOwner);

    private static string Describe(DeviceInfo info, DateTimeOffset now)
    {
        var paired = $"Paired {info.PairedAt.ToLocalTime():d MMM yyyy}";
        if (info.LastSeenAt is not { } seen)
        {
            return paired + " · never connected";
        }

        var ago = now - seen;
        var last = ago switch
        {
            { TotalMinutes: < 5 } => "active now",
            { TotalMinutes: < 60 } => $"last seen {(int)ago.TotalMinutes} min ago",
            { TotalHours: < 24 } => $"last seen {(int)ago.TotalHours}h ago",
            { TotalDays: < 30 } => $"last seen {(int)ago.TotalDays}d ago",
            _ => $"last seen {seen.ToLocalTime():d MMM yyyy}",
        };

        return $"{paired} · {last}";
    }
}

/// <summary>The devices paired with a host, and the ability to revoke one. Worth having on a phone: if
/// you lose a laptop, this is the fastest way to cut it off. An owner can also promote, demote and prune
/// from here; a member sees the same list read-only, and is told why.</summary>
public sealed partial class DevicesPageViewModel : PageViewModel
{
    private readonly IAppShell _shell;

    /// <summary>How long a device may go unused before the prune offer will remove it.</summary>
    public const int PruneUnusedForDays = 30;

    public DevicesPageViewModel(IAppShell shell)
    {
        _shell = shell;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        RevokeCommand = new AsyncRelayCommand<MobileDeviceRow>(RevokeAsync);
        SetRoleCommand = new AsyncRelayCommand<MobileDeviceRow>(SetRoleAsync);
        PruneCommand = new AsyncRelayCommand(PruneAsync);
        _ = LoadAsync();
    }

    public override string Title => "Paired devices";


    public ObservableCollection<MobileDeviceRow> Devices { get; } = [];

    [ObservableProperty]
    private string _status = "Loading…";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Whether this device may manage the others. False until the host has said so, so the
    /// buttons never appear on a guess.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnlyOwnersNote))]
    private bool _canManage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnlyOwnersNote))]
    private bool _isRoleKnown;

    public bool ShowOnlyOwnersNote => IsRoleKnown && !CanManage;

    public static string OnlyOwnersNote => DeviceRoleText.OnlyOwnersManage;

    public static string PruneLabel => DeviceRoleText.PruneAction(PruneUnusedForDays);

    /// <summary>Armed by the first tap, which names the number; the second tap removes them. A phone has
    /// no hover and no undo, so the count has to be in the button itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PruneButtonLabel))]
    private bool _isConfirmingPrune;

    public string PruneButtonLabel => IsConfirmingPrune
        ? DeviceRoleText.ConfirmPrune(StaleCount, PruneUnusedForDays)
        : PruneLabel;

    private int StaleCount => Devices.Count(d =>
        !d.IsCurrentDevice
        && !d.IsOwner
        && (d.Info.LastSeenAt is not { } seen
            || DateTimeOffset.UtcNow - seen > TimeSpan.FromDays(PruneUnusedForDays)));

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<MobileDeviceRow> RevokeCommand { get; }
    public IAsyncRelayCommand<MobileDeviceRow> SetRoleCommand { get; }
    public IAsyncRelayCommand PruneCommand { get; }

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
            var list = await DeviceManagement.ListAsync(link.Url, link.Saved.Token, link.Http).ConfigureAwait(false);

            // What this device is, on the same trip. Null means the host predates roles: leave the page
            // exactly as it was before they existed rather than guessing.
            var me = await PairingManagement.MeAsync(link.Url, link.Saved.Token, link.Http).ConfigureAwait(false);
            if (me is not null)
            {
                link.SetRole(me.Role);
            }

            var owners = list.Count(d => d.Role == DeviceRole.Owner);
            var now = DateTimeOffset.UtcNow;
            _shell.Dispatcher.Post(() =>
            {
                IsRoleKnown = me is not null;
                CanManage = me?.Role == DeviceRole.Owner;
                IsConfirmingPrune = false;

                Devices.Clear();
                foreach (var device in list)
                {
                    Devices.Add(new MobileDeviceRow(device, now, owners <= 1));
                }

                OnPropertyChanged(nameof(PruneButtonLabel));
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

    private async Task RevokeAsync(MobileDeviceRow? device)
    {
        if (device is null || Target is not { } link)
        {
            return;
        }

        try
        {
            await DeviceManagement.RevokeAsync(link.Url, link.Saved.Token, device.Id, link.Http).ConfigureAwait(false);
            _shell.Toast("Revoked", ToastKind.Success);
            await LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _shell.Toast("Couldn't revoke: " + ex.Message, ToastKind.Danger);
        }
    }

    /// <summary>Promotes or demotes one device. The host is the authority — it refuses a demotion that
    /// would leave no owner — so a refusal is reported rather than pre-empted.</summary>
    private async Task SetRoleAsync(MobileDeviceRow? device)
    {
        if (device is null || Target is not { } link)
        {
            return;
        }

        var wanted = device.TargetRole;
        var ok = await PairingManagement
            .SetRoleAsync(link.Url, link.Saved.Token, device.Id, wanted, link.Http).ConfigureAwait(false);

        _shell.Toast(
            ok ? $"{device.Name} is now {(wanted == DeviceRole.Owner ? "an owner" : "a member")}"
               : "The host refused — it keeps at least one owner",
            ok ? ToastKind.Success : ToastKind.Warning);

        await LoadAsync().ConfigureAwait(false);
    }

    private async Task PruneAsync()
    {
        if (Target is not { } link)
        {
            return;
        }

        if (!IsConfirmingPrune)
        {
            _shell.Dispatcher.Post(() =>
            {
                IsConfirmingPrune = true;
                Status = StaleCount == 0
                    ? $"Nothing has been idle for {PruneUnusedForDays} days."
                    : "Tap again to remove them.";
            });
            return;
        }

        var ok = await PairingManagement
            .PruneAsync(link.Url, link.Saved.Token, PruneUnusedForDays, link.Http).ConfigureAwait(false);

        _shell.Toast(
            ok ? $"Removed the devices unused for {PruneUnusedForDays} days" : "Only an owner can prune devices",
            ok ? ToastKind.Success : ToastKind.Warning);

        await LoadAsync().ConfigureAwait(false);
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
        SiteCommand = new RelayCommand(() => _shell.OpenUrl("https://multitudal.com"));
    }

    public override string Title => "About";


    public string Version => typeof(AboutPageViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public IRelayCommand RepoCommand { get; }
    public IRelayCommand DocsCommand { get; }
    public IRelayCommand SiteCommand { get; }
}
