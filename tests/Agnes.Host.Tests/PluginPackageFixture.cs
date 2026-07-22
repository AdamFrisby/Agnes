using NuGet.Packaging;
using NuGet.Versioning;

namespace Agnes.Host.Tests;

/// <summary>Builds a real, well-formed (but unsigned) .nupkg around the compiled
/// <c>Agnes.TestPluginFixture.dll</c>, so <c>PluginInstallerTests</c> exercises the actual
/// extraction/manifest-parsing/ALC-loading path against real IL instead of a hand-rolled zip.</summary>
public static class PluginPackageFixture
{
    public const string PackageId = "test-plugin";

    private static readonly Lazy<string> FixtureDllPath = new(LocateFixtureDll);

    /// <summary>Builds a .nupkg for <paramref name="version"/> declaring <paramref name="capabilities"/>
    /// in its agnes-plugin.json manifest.</summary>
    public static byte[] Build(string version = "1.0.0", IReadOnlyList<string>? capabilities = null, string agnesApiVersion = "[0.0.0,)")
    {
        capabilities ??= [];
        var manifestJson = $$"""
            {
              "id": "{{PackageId}}",
              "displayName": "Test Plugin",
              "version": "{{version}}",
              "pluginPoints": ["IAgentAdapter"],
              "agnesApiVersion": "{{agnesApiVersion}}",
              "capabilities": [{{string.Join(", ", capabilities.Select(c => $"\"{c}\""))}}],
              "publisher": "Agnes Test Fixtures"
            }
            """;

        var builder = new PackageBuilder
        {
            Id = PackageId,
            Version = NuGetVersion.Parse(version),
            Description = "Agnes test plugin fixture",
        };
        builder.Authors.Add("Agnes Test Fixtures");

        builder.Files.Add(new PhysicalPackageFile
        {
            SourcePath = FixtureDllPath.Value,
            TargetPath = "lib/net10.0/Agnes.TestPluginFixture.dll",
        });

        var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifestJson);
        builder.Files.Add(new InMemoryPackageFile(manifestBytes, "agnes-plugin.json"));

        using var stream = new MemoryStream();
        builder.Save(stream);
        return stream.ToArray();
    }

    private static string LocateFixtureDll()
    {
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name; // .../bin/<Config>/net10.0/
        var testsDir = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent!.Parent!.Parent!.Parent!; // net10.0 -> <Config> -> bin -> Agnes.Host.Tests -> tests
        var dll = Path.Combine(testsDir.FullName, "Agnes.TestPluginFixture", "bin", config, "net10.0", "Agnes.TestPluginFixture.dll");
        return File.Exists(dll)
            ? dll
            : throw new FileNotFoundException($"Test plugin fixture not built at expected path: {dll}");
    }

    private sealed class InMemoryPackageFile : IPackageFile
    {
        private readonly byte[] _content;

        public InMemoryPackageFile(byte[] content, string targetPath)
        {
            _content = content;
            Path = targetPath;
            EffectivePath = targetPath;
        }

        public string Path { get; }
        public string EffectivePath { get; }
        public System.Runtime.Versioning.FrameworkName? TargetFramework => null;
        public NuGet.Frameworks.NuGetFramework NuGetFramework => NuGet.Frameworks.NuGetFramework.AnyFramework;
        public DateTimeOffset LastWriteTime => DateTimeOffset.UtcNow;

        public Stream GetStream() => new MemoryStream(_content);

        public string[]? Exclude => null;
        public string[]? Include => null;
    }
}
