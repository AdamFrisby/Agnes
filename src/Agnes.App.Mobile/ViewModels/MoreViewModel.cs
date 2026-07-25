using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// The fourth tab: hosts, appearance, notifications, and the reference material.
///
/// Deliberately shallow — the desktop client's settings tab has twelve categories because it manages
/// the host. This one manages the phone, and hands off to the host's own surfaces where it must.
/// </summary>
public sealed partial class MoreViewModel : ObservableObject
{
    private readonly IAppShell _shell;

    public MoreViewModel(IAppShell shell)
    {
        _shell = shell;

        HostsCommand = new RelayCommand(() => _shell.ShowSheet(
            new HostsSheetViewModel(_shell, _shell.Hosts, ((ShellViewModel)_shell).Sessions)));
        AddHostCommand = new RelayCommand(() => _shell.Push(
            new ConnectPageViewModel(_shell, _shell.Hosts, ((ShellViewModel)_shell).Sessions)));
        AppearanceCommand = new RelayCommand(() => _shell.Push(new AppearancePageViewModel(_shell)));
        NotificationsCommand = new RelayCommand(() => _shell.Push(new NotificationsPageViewModel(_shell)));
        PromptsCommand = new RelayCommand(() => _shell.Push(new PromptsPageViewModel(_shell)));
        DevicesCommand = new RelayCommand(() => _shell.Push(new DevicesPageViewModel(_shell)));
        AboutCommand = new RelayCommand(() => _shell.Push(new AboutPageViewModel(_shell)));
    }

    public IRelayCommand HostsCommand { get; }
    public IRelayCommand AddHostCommand { get; }
    public IRelayCommand AppearanceCommand { get; }
    public IRelayCommand NotificationsCommand { get; }
    public IRelayCommand PromptsCommand { get; }
    public IRelayCommand DevicesCommand { get; }
    public IRelayCommand AboutCommand { get; }

    public string HostsDetail
    {
        get
        {
            var real = _shell.Hosts.Real.ToList();
            if (real.Count == 0)
            {
                return "None paired yet";
            }

            var online = real.Count(h => h.IsOnline);
            return $"{online} of {real.Count} online";
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(HostsDetail));
}
