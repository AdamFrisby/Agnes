using Agnes.Abstractions;
using FluentIcons.Common;

namespace Agnes.Ui.Core.Transcript;

/// <summary>
/// A file the agent sent the user, as a card in the transcript. The bytes are not here: a head fetches
/// them on demand through the session's host connection (<c>SessionViewModel.DownloadSharedFileAsync</c>)
/// and hands them to its <see cref="IReceivedFileHandler"/> to save, open or share — the three verbs a
/// received file has, each meaning something different on a desktop and on a phone.
/// </summary>
public sealed class SharedFileItem : TranscriptItem
{
    public SharedFileItem(FileSharedEvent @event)
    {
        FileId = @event.FileId;
        FileName = @event.FileName;
        RelativePath = @event.RelativePath;
        Size = @event.Size;
        MimeType = @event.MimeType;
        Caption = @event.Caption;
    }

    public string FileId { get; }
    public string FileName { get; }
    public string RelativePath { get; }
    public long Size { get; }
    public string? MimeType { get; }
    public string? Caption { get; }

    public bool HasCaption => !string.IsNullOrWhiteSpace(Caption);

    /// <summary>Whether a head should try to show it inline rather than as an icon.</summary>
    public bool IsImage => MimeType is { } m && m.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public bool IsText => MimeType is { } m && (m.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || m is "application/json" or "application/xml" or "application/x-yaml");

    public bool IsPdf => string.Equals(MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    public string Extension => System.IO.Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();

    /// <summary>
    /// The icon for this file's kind. View-model state rather than a constant in each head's view, because
    /// it varies with the file — the one case the house rule says an icon belongs to the model.
    /// </summary>
    public Symbol Symbol => IsImage ? Symbol.Image
        : IsPdf ? Symbol.DocumentPdf
        : IsText ? Symbol.DocumentText
        : Symbol.Document;

    /// <summary>"1.2 MB", "840 KB", "312 B".</summary>
    public string SizeText => Size switch
    {
        >= 1024 * 1024 => $"{Size / (1024.0 * 1024.0):0.#} MB",
        >= 1024 => $"{Size / 1024.0:0} KB",
        _ => $"{Size} B",
    };
}
