using System.Collections.ObjectModel;
using System.Windows.Input;
using Agnes.Client;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// The framework-agnostic view model behind the file-browser panel (git-and-files/03). It's a thin,
/// testable command surface over <see cref="IAgnesHost"/>'s structured file ops on the session's working
/// directory: list the current directory, navigate in/up, open a file to read (text or image preview), save
/// a quick edit, rename/delete an entry, make a new folder, and download bytes. All path-safety lives on the
/// host (the shared <c>WorkspacePaths</c> guard); this VM only ever hands the host workspace-relative paths.
/// A single <see cref="IsBusy"/>/<see cref="Error"/> pair keeps every op's failure visible instead of silent.
/// </summary>
public sealed class FileBrowserViewModel : ObservableObject
{
    private readonly IAgnesHost _host;
    private readonly IUiDispatcher _dispatcher;

    private string _currentPath = string.Empty;
    private FileEntry? _selectedEntry;
    private FileContent? _openFile;
    private string _editText = string.Empty;
    private bool _isVisible;
    private bool _isBusy;
    private string? _error;

    public FileBrowserViewModel(IAgnesHost host, string sessionId, IUiDispatcher? dispatcher = null)
    {
        _host = host;
        SessionId = sessionId;
        _dispatcher = dispatcher ?? ImmediateDispatcher.Instance;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        NavigateUpCommand = new AsyncRelayCommand(NavigateUpAsync, () => !IsAtRoot);
        OpenCommand = new AsyncRelayCommand<FileEntry>(OpenEntryAsync);
        SaveEditCommand = new AsyncRelayCommand(SaveEditAsync, () => _openFile is { Kind: FileContentKind.Text });
        DeleteCommand = new AsyncRelayCommand<FileEntry>(DeleteAsync);
        CloseFileCommand = new RelayCommand(() => OpenFile = null);
    }

    /// <summary>The session whose working directory is being browsed.</summary>
    public string SessionId { get; }

    /// <summary>The entries of the current directory (directories first, then by name — as the host sorts).</summary>
    public ObservableCollection<FileEntry> Entries { get; } = [];

    /// <summary>The workspace-relative path of the directory being shown (empty = the workspace root).</summary>
    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(IsAtRoot));
                (NavigateUpCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether the current directory is the workspace root (so "up" is a no-op).</summary>
    public bool IsAtRoot => string.IsNullOrEmpty(_currentPath);

    /// <summary>The entry the user last selected in the list.</summary>
    public FileEntry? SelectedEntry
    {
        get => _selectedEntry;
        set => SetProperty(ref _selectedEntry, value);
    }

    /// <summary>The currently opened file's content (text or image), or null when nothing is open.</summary>
    public FileContent? OpenFile
    {
        get => _openFile;
        private set
        {
            if (SetProperty(ref _openFile, value))
            {
                OnPropertyChanged(nameof(HasOpenFile));
                OnPropertyChanged(nameof(IsTextOpen));
                OnPropertyChanged(nameof(IsImageOpen));
                EditText = value is { Kind: FileContentKind.Text, Text: { } t } ? t : string.Empty;
                (SaveEditCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether a file is open in the preview pane.</summary>
    public bool HasOpenFile => _openFile is not null;

    /// <summary>The open file is editable text.</summary>
    public bool IsTextOpen => _openFile is { Kind: FileContentKind.Text };

    /// <summary>The open file is a previewable image.</summary>
    public bool IsImageOpen => _openFile is { Kind: FileContentKind.Image };

    /// <summary>The editable text of the open file (bound to the editor; saved via <see cref="SaveEditAsync"/>).</summary>
    public string EditText
    {
        get => _editText;
        set => SetProperty(ref _editText, value);
    }

    /// <summary>Whether the file-browser panel is shown (show/hide; toggling it never loses navigation state).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>True while an op is in flight (drives a spinner / disables the list).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>The last op's error message, or null when the last op succeeded.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    public ICommand RefreshCommand { get; }
    public ICommand NavigateUpCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand SaveEditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CloseFileCommand { get; }

    /// <summary>(Re)loads the current directory's entries from the host.</summary>
    public Task RefreshAsync() => LoadDirectoryAsync(_currentPath);

    /// <summary>Navigates into a directory entry (a file entry opens instead of navigating).</summary>
    public Task NavigateIntoAsync(FileEntry entry)
    {
        if (entry is null)
        {
            return Task.CompletedTask;
        }

        return entry.IsDirectory ? LoadDirectoryAsync(entry.RelativePath) : OpenEntryAsync(entry);
    }

    /// <summary>Navigates to the parent of the current directory (no-op at the root).</summary>
    public Task NavigateUpAsync()
    {
        if (IsAtRoot)
        {
            return Task.CompletedTask;
        }

        var slash = _currentPath.LastIndexOf('/');
        var parent = slash < 0 ? string.Empty : _currentPath[..slash];
        return LoadDirectoryAsync(parent);
    }

    /// <summary>Opens a file entry into the preview pane (reads it from the host).</summary>
    public async Task OpenEntryAsync(FileEntry? entry)
    {
        if (entry is null || entry.IsDirectory)
        {
            return;
        }

        await RunAsync(async () => OpenFile = await _host.ReadFileAsync(SessionId, entry.RelativePath).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>Saves the edited text of the open file back to the workspace (quick edit, no agent turn).</summary>
    public async Task SaveEditAsync()
    {
        if (_openFile is not { Kind: FileContentKind.Text } file)
        {
            return;
        }

        await RunAsync(() => _host.WriteFileAsync(SessionId, file.RelativePath, _editText)).ConfigureAwait(false);
    }

    /// <summary>Deletes an entry and refreshes the list.</summary>
    public async Task DeleteAsync(FileEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _host.DeleteEntryAsync(SessionId, entry.RelativePath).ConfigureAwait(false);
            if (string.Equals(_openFile?.RelativePath, entry.RelativePath, StringComparison.Ordinal))
            {
                OpenFile = null;
            }

            await LoadEntriesAsync(_currentPath).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Renames an entry to <paramref name="newName"/> (kept in the same directory) and refreshes.</summary>
    public async Task RenameAsync(FileEntry entry, string newName)
    {
        if (entry is null || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var slash = entry.RelativePath.LastIndexOf('/');
        var toPath = slash < 0 ? newName : entry.RelativePath[..(slash + 1)] + newName;
        await RunAsync(async () =>
        {
            await _host.RenameEntryAsync(SessionId, entry.RelativePath, toPath).ConfigureAwait(false);
            await LoadEntriesAsync(_currentPath).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Creates a new folder under the current directory and refreshes.</summary>
    public async Task CreateFolderAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var path = string.IsNullOrEmpty(_currentPath) ? name : _currentPath + "/" + name;
        await RunAsync(async () =>
        {
            await _host.CreateDirectoryAsync(SessionId, path).ConfigureAwait(false);
            await LoadEntriesAsync(_currentPath).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Downloads an entry's raw bytes (the desktop view saves them to a local file).</summary>
    public Task<byte[]> DownloadAsync(FileEntry entry)
        => entry is null || entry.IsDirectory
            ? Task.FromResult<byte[]>([])
            : _host.DownloadFileAsync(SessionId, entry.RelativePath);

    private async Task LoadDirectoryAsync(string path)
    {
        await RunAsync(() => LoadEntriesAsync(path)).ConfigureAwait(false);
    }

    private async Task LoadEntriesAsync(string path)
    {
        var entries = await _host.ListDirectoryAsync(SessionId, path).ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            CurrentPath = path;
            Entries.Clear();
            foreach (var entry in entries)
            {
                Entries.Add(entry);
            }
        });
    }

    // Runs an op with a single in-flight guard and turns any failure into a visible Error rather than an
    // unobserved exception — a rejected traversal path surfaces here as a message, not a crash.
    private async Task RunAsync(Func<Task> op)
    {
        _dispatcher.Post(() => { IsBusy = true; Error = null; });
        try
        {
            await op().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Error = ex.Message);
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }
}
