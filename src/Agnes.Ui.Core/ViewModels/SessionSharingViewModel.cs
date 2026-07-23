using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.Client;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Drives the desktop "Share" affordance on a single session (collaboration/02) against whatever
/// <see cref="IAgnesHost"/> the accessor returns, so it works identically over a real SignalR host and the
/// offline simulation. Two deliberately-separate mechanisms, matching the domain:
/// <list type="bullet">
///   <item><b>Direct sharing</b> with an identified recipient (a GitHub login or a paired device id) at one of
///     three access levels, plus the orthogonal permission-approval toggle. The toggle is only offered for a
///     CanEdit/CanManage share — the host refuses it for a view-only share regardless, and this surfaces that
///     refusal rather than hiding it.</item>
///   <item><b>A public link</b> — always view-only by construction — with optional expiry, max-uses and a
///     consent gate. The raw URL is returned once, to copy; the host only keeps a hash.</item>
/// </list>
/// Only the session owner or a CanManage collaborator may change sharing; a non-authorized client's calls are
/// refused host-side and surfaced here as a status message.
/// </summary>
public sealed class SessionSharingViewModel : ObservableObject
{
    private readonly Func<IAgnesHost?> _host;
    private readonly IUiDispatcher _dispatcher;

    public SessionSharingViewModel(Func<IAgnesHost?> host, IUiDispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ShareCommand = new AsyncRelayCommand(ShareAsync);
        RevokeShareCommand = new AsyncRelayCommand<SessionShare>(RevokeShareAsync);
        CreatePublicLinkCommand = new AsyncRelayCommand(CreatePublicLinkAsync);
        RevokePublicLinkCommand = new AsyncRelayCommand(RevokePublicLinkAsync);
    }

    /// <summary>The session this affordance shares. Set before refreshing.</summary>
    private string _sessionId = string.Empty;
    public string SessionId
    {
        get => _sessionId;
        set => SetProperty(ref _sessionId, value);
    }

    /// <summary>The active direct shares on the session.</summary>
    public ObservableCollection<SessionShare> Shares { get; } = [];

    /// <summary>The access levels a share can carry, for a picker.</summary>
    public IReadOnlyList<SessionAccessLevel> Levels { get; } = Enum.GetValues<SessionAccessLevel>();

    private string _newRecipientId = string.Empty;
    public string NewRecipientId
    {
        get => _newRecipientId;
        set => SetProperty(ref _newRecipientId, value);
    }

    private SessionAccessLevel _newLevel = SessionAccessLevel.ViewOnly;
    public SessionAccessLevel NewLevel
    {
        get => _newLevel;
        set
        {
            if (SetProperty(ref _newLevel, value))
            {
                // A view-only share can never carry permission approvals — reflect that in the UI so the toggle
                // isn't even offered (the host enforces it regardless).
                OnPropertyChanged(nameof(CanOfferPermissionApprovals));
                if (value == SessionAccessLevel.ViewOnly)
                {
                    NewAllowPermissionApprovals = false;
                }
            }
        }
    }

    private bool _newAllowPermissionApprovals;
    public bool NewAllowPermissionApprovals
    {
        get => _newAllowPermissionApprovals;
        set => SetProperty(ref _newAllowPermissionApprovals, value);
    }

    /// <summary>Whether the permission-approval toggle may be offered for the currently-selected level.</summary>
    public bool CanOfferPermissionApprovals => NewLevel != SessionAccessLevel.ViewOnly;

    // Public-link options.
    private int? _linkMaxUses;
    public int? LinkMaxUses
    {
        get => _linkMaxUses;
        set => SetProperty(ref _linkMaxUses, value);
    }

    private TimeSpan? _linkExpiry;
    public TimeSpan? LinkExpiry
    {
        get => _linkExpiry;
        set => SetProperty(ref _linkExpiry, value);
    }

    private bool _linkRequireConsent = true;
    public bool LinkRequireConsent
    {
        get => _linkRequireConsent;
        set => SetProperty(ref _linkRequireConsent, value);
    }

    private string _publicLinkUrl = string.Empty;
    /// <summary>The most-recently-created public link URL (shown once so it can be copied).</summary>
    public string PublicLinkUrl
    {
        get => _publicLinkUrl;
        set => SetProperty(ref _publicLinkUrl, value);
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool HasShares => Shares.Count > 0;

    public ICommand RefreshCommand { get; }
    public ICommand ShareCommand { get; }
    public ICommand RevokeShareCommand { get; }
    public ICommand CreatePublicLinkCommand { get; }
    public ICommand RevokePublicLinkCommand { get; }

    /// <summary>Loads the session's active shares and rebuilds the list.</summary>
    public async Task RefreshAsync()
    {
        var host = _host();
        if (host is null || string.IsNullOrWhiteSpace(SessionId))
        {
            _dispatcher.Post(() =>
            {
                Shares.Clear();
                OnPropertyChanged(nameof(HasShares));
                Status = "Open a session to manage sharing.";
            });
            return;
        }

        try
        {
            var shares = await host.ListSharesAsync(SessionId).ConfigureAwait(false);
            _dispatcher.Post(() => Rebuild(shares));
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't load shares: " + ex.Message);
        }
    }

    private void Rebuild(IReadOnlyList<SessionShare> shares)
    {
        Shares.Clear();
        foreach (var s in shares)
        {
            Shares.Add(s);
        }

        OnPropertyChanged(nameof(HasShares));
        Status = $"{Shares.Count} collaborator(s).";
    }

    private async Task ShareAsync()
    {
        var host = _host();
        var recipient = NewRecipientId?.Trim();
        if (host is null || string.IsNullOrWhiteSpace(recipient))
        {
            _dispatcher.Post(() => Status = "Enter a recipient (a GitHub login or a paired device id).");
            return;
        }

        try
        {
            await host.ShareSessionAsync(SessionId, recipient, NewLevel, NewAllowPermissionApprovals).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                NewRecipientId = string.Empty;
                NewAllowPermissionApprovals = false;
            });
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Surfaces the host's structural refusal (e.g. approvals on a view-only/inactive share) verbatim.
            _dispatcher.Post(() => Status = "Couldn't share: " + ex.Message);
        }
    }

    private async Task RevokeShareAsync(SessionShare? share)
    {
        var host = _host();
        if (host is null || share is null)
        {
            return;
        }

        try
        {
            await host.RevokeShareAsync(SessionId, share.RecipientId).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't revoke share: " + ex.Message);
        }
    }

    private async Task CreatePublicLinkAsync()
    {
        var host = _host();
        if (host is null || string.IsNullOrWhiteSpace(SessionId))
        {
            return;
        }

        try
        {
            var link = await host.CreatePublicLinkAsync(SessionId, new PublicLinkOptions(LinkExpiry, LinkMaxUses, LinkRequireConsent)).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                PublicLinkUrl = link.Url.ToString();
                Status = "Public link created — copy it now; it won't be shown again.";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't create link: " + ex.Message);
        }
    }

    private async Task RevokePublicLinkAsync()
    {
        var host = _host();
        if (host is null || string.IsNullOrWhiteSpace(SessionId))
        {
            return;
        }

        try
        {
            await host.RevokePublicLinkAsync(SessionId).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                PublicLinkUrl = string.Empty;
                Status = "Public link revoked.";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't revoke link: " + ex.Message);
        }
    }
}
