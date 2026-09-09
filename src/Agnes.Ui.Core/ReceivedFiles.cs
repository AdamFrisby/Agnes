namespace Agnes.Ui.Core;

/// <summary>A file the user received from an agent, with its bytes in hand.</summary>
public sealed record ReceivedFile(string FileName, string? MimeType, byte[] Bytes);

/// <summary>
/// What a head can do with a received file. Implemented per frontend: the desktop saves through a file
/// picker and opens with the OS default app; Android saves to Downloads and shares through the system
/// share sheet. Each verb is optional — a head reports what it can do so a view shows only real buttons.
/// </summary>
public interface IReceivedFileHandler
{
    bool CanSave { get; }
    bool CanOpen { get; }
    bool CanShare { get; }

    /// <summary>Puts the file somewhere the person chooses (or the platform's downloads folder).</summary>
    Task SaveAsync(ReceivedFile file, CancellationToken cancellationToken = default);

    /// <summary>Opens the file in whatever the platform uses for its type.</summary>
    Task OpenAsync(ReceivedFile file, CancellationToken cancellationToken = default);

    /// <summary>Hands the file to the platform's share surface.</summary>
    Task ShareAsync(ReceivedFile file, CancellationToken cancellationToken = default);
}

/// <summary>Does nothing; the default for tests, headless renders and heads that have not wired one.</summary>
public sealed class NullReceivedFileHandler : IReceivedFileHandler
{
    public static readonly NullReceivedFileHandler Instance = new();

    public bool CanSave => false;
    public bool CanOpen => false;
    public bool CanShare => false;

    public Task SaveAsync(ReceivedFile file, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task OpenAsync(ReceivedFile file, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ShareAsync(ReceivedFile file, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
