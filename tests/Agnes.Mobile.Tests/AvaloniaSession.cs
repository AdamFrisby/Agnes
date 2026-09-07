using Avalonia.Headless;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The one Avalonia application these tests get.
///
/// A <see cref="HeadlessUnitTestSession"/> registers the <c>avares:</c> URI parser and the platform
/// locator process-wide, so a second one — even of the same app type — fails with "a URI scheme name
/// 'avares' already has a registered custom parser" and takes an unrelated test down with it. So the
/// session is a collection fixture: built once, shared by every class that renders, torn down at the
/// end of the run.
///
/// It draws with Skia rather than the null backend, because the display surface's whole job is to decode
/// a JPEG and composite it, and that needs a real render interface.
/// </summary>
public sealed class AvaloniaSession : IDisposable
{
    public AvaloniaSession()
        => Session = HeadlessUnitTestSession.StartNew(typeof(Agnes.App.Mobile.Preview.PreviewAppBuilder));

    public HeadlessUnitTestSession Session { get; }

    public void Dispose() => Session.Dispose();

    /// <summary>Runs <paramref name="action"/> on the UI thread of the shared session.</summary>
    public Task Run(Action action) => Session.Dispatch(action, CancellationToken.None);
}

/// <summary>The xunit collection every rendering test class joins, so they share one application.</summary>
[CollectionDefinition(Name)]
public sealed class AvaloniaCollection : ICollectionFixture<AvaloniaSession>
{
    public const string Name = "avalonia";
}
