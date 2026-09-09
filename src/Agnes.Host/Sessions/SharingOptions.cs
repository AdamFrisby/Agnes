namespace Agnes.Host.Sessions;

/// <summary>
/// How generously an agent may send the user files (<c>Agnes:Sharing:*</c>).
/// <para>
/// The cap exists because sending copies: every shared file is duplicated into the session's workspace and
/// then pulled down by every connected client, phones included. A model that decides to "send you the build"
/// can name a multi-gigabyte artifact as easily as a screenshot, and the person on the other end pays for it
/// in disk and mobile data. 25 MB comfortably covers what the feature is for — screenshots, reports,
/// diagrams, a small binary — while making the pathological case a clear refusal the agent can act on rather
/// than a silent, very slow success.
/// </para>
/// </summary>
public sealed record SharingOptions
{
    /// <summary>The default cap: 25 MB.</summary>
    public const long DefaultMaxBytes = 25L * 1024 * 1024;

    /// <summary>Largest file an agent may send, in bytes. A non-positive value falls back to the default.</summary>
    public long MaxBytes { get; init; } = DefaultMaxBytes;

    /// <summary>The effective cap, so a misconfigured zero can't disable sharing by accident.</summary>
    public long EffectiveMaxBytes => MaxBytes > 0 ? MaxBytes : DefaultMaxBytes;
}
