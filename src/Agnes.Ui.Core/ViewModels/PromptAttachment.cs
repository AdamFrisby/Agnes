using Agnes.Abstractions;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>A piece of context attached to the next prompt (a file/URL reference or an image).</summary>
public sealed class PromptAttachment
{
    public PromptAttachment(string label, string kindText, ContentBlock content)
    {
        Label = label;
        KindText = kindText;
        Content = content;
    }

    /// <summary>Short display label for the chip.</summary>
    public string Label { get; }

    /// <summary>Kind badge, e.g. "@" for a reference or "img" for an image.</summary>
    public string KindText { get; }

    /// <summary>The block sent to the agent alongside the prompt text.</summary>
    public ContentBlock Content { get; }

    public static PromptAttachment Reference(string uriOrPath)
        => new(uriOrPath, "@", new ResourceLinkContent(uriOrPath, uriOrPath));
}
