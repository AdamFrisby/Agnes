using System.Text.Json.Serialization;

namespace Agnes.Abstractions;

/// <summary>Role attributed to a message in a session.</summary>
public enum MessageRole
{
    User,
    Assistant,
}

/// <summary>A single block of content within a message or tool call.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(ResourceLinkContent), "resource_link")]
public abstract record ContentBlock;

/// <summary>Plain UTF-8 text.</summary>
public sealed record TextContent(string Text) : ContentBlock;

/// <summary>An inline image, base64-encoded.</summary>
public sealed record ImageContent(string MimeType, string Data) : ContentBlock;

/// <summary>A link to a resource (file, URL) the agent referenced.</summary>
public sealed record ResourceLinkContent(string Uri, string? Name = null) : ContentBlock;
