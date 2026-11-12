using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Tc.Patcher.Packaging;
using Dalamud.Tc.Patcher.Patching;
using Xunit;

namespace Dalamud.Tc.Patcher.Tests;

public sealed class PackageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dalamud-tc-patcher-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SafeZip_RejectsTraversalEntry()
    {
        Directory.CreateDirectory(this.root);
        var zip = Path.Combine(this.root, "traversal.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open()))
            writer.Write("not allowed");

        Assert.Throws<InvalidDataException>(() => SafeZip.Extract(zip, Path.Combine(this.root, "result")));
        Assert.False(File.Exists(Path.Combine(this.root, "outside.txt")));
    }

    [Fact]
    public async Task ArtifactHashCheck_RejectsMismatch()
    {
        Directory.CreateDirectory(this.root);
        var path = Path.Combine(this.root, "input.bin");
        File.WriteAllText(path, "verified content");
        var wrongHash = Convert.ToHexString(SHA256.HashData("different content"u8));
        Assert.False(await ArtifactDownloader.HasExpectedSha256Async(path, wrongHash, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SafeZip_RejectsCaseInsensitiveDuplicateEntries()
    {
        Directory.CreateDirectory(this.root);
        var zip = Path.Combine(this.root, "duplicate.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("Data/file.txt");
            archive.CreateEntry("data/FILE.txt");
        }

        Assert.Throws<InvalidDataException>(() => SafeZip.Extract(zip, Path.Combine(this.root, "result")));
    }

    [Fact]
    public async Task PackageBuilder_CleansStagingWhenInputZipIsInvalid()
    {
        Directory.CreateDirectory(this.root);
        var cache = Path.Combine(this.root, "cache");
        Directory.CreateDirectory(cache);
        var bytes = "not a ZIP"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(cache, $"{hash}.zip"), bytes);
        var output = Path.Combine(this.root, "result");
        var manifest = new PackageManifest("test", "13.0.0.16", "commit", "game", new ArtifactSpec("https://example.invalid/input.zip", hash), Array.Empty<AssemblyPatchSpec>());

        await Assert.ThrowsAsync<InvalidDataException>(() => PackageBuilder.BuildAsync(manifest, output, cache, null, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateDirectories(this.root, ".result.staging.*"));
    }

    [Theory]
    [InlineData("9.0.0", "9.0.3")]
    [InlineData("9.0.42", "9.0.42")]
    public async Task PackageBuilder_WritesStableMetadataWithoutHashReportCycle(string inputRuntime, string expectedRuntime)
    {
        Directory.CreateDirectory(this.root);
        var sourceZip = Path.Combine(this.root, "source.zip");
        using (var archive = ZipFile.Open(sourceZip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("value.txt").Open()))
                await writer.WriteAsync("payload");
            using (var writer = new StreamWriter(archive.CreateEntry("Dalamud.runtimeconfig.json").Open()))
                await writer.WriteAsync("""
                    {"runtimeOptions":{"tfm":"net9.0","rollForward":"LatestMinor","frameworks":[
                        {"name":"Microsoft.NETCore.App","version":"RUNTIME_VERSION"},
                        {"name":"Microsoft.WindowsDesktop.App","version":"RUNTIME_VERSION"}]}}
                    """.Replace("RUNTIME_VERSION", inputRuntime, StringComparison.Ordinal));
            using (var writer = new StreamWriter(archive.CreateEntry("Dalamud.Injector.runtimeconfig.json").Open()))
                await writer.WriteAsync("""
                    {"runtimeOptions":{"tfm":"net9.0","framework":{"name":"Microsoft.NETCore.App","version":"RUNTIME_VERSION"}}}
                    """.Replace("RUNTIME_VERSION", inputRuntime, StringComparison.Ordinal));
        }
        var bytes = File.ReadAllBytes(sourceZip);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var cache = Path.Combine(this.root, "cache");
        Directory.CreateDirectory(cache);
        File.Move(sourceZip, Path.Combine(cache, $"{hash}.zip"));
        var output = Path.Combine(this.root, "result");
        var manifest = new PackageManifest("test", "13", "source-commit", "game", new ArtifactSpec("https://example.invalid/input.zip", hash), Array.Empty<AssemblyPatchSpec>());

        await PackageBuilder.BuildAsync(manifest, output, cache, null, TestContext.Current.CancellationToken);
        using var hashes = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "hashes.json")));
        Assert.True(hashes.RootElement.TryGetProperty("value.txt", out _));
        Assert.False(hashes.RootElement.TryGetProperty("hashes.json", out _));
        Assert.False(hashes.RootElement.TryGetProperty("versionInfo.json", out _));
        using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "versionInfo.json")));
        var requirements = report.RootElement.GetProperty("runtimeRequirements");
        Assert.Equal(2, requirements.EnumerateObject().Count());
        var core = requirements.GetProperty("Dalamud.runtimeconfig.json");
        Assert.Equal("LatestMinor", core.GetProperty("rollForward").GetString());
        Assert.All(core.GetProperty("frameworks").EnumerateArray(), f => Assert.Equal(expectedRuntime, f.GetProperty("version").GetString()));
        Assert.Equal(expectedRuntime, requirements.GetProperty("Dalamud.Injector.runtimeconfig.json").GetProperty("framework").GetProperty("version").GetString());
        Assert.False(File.Exists(Path.Combine(output, "build-report.json")));
        Assert.False(report.RootElement.GetProperty("outputFiles").TryGetProperty("versionInfo.json", out _));
        foreach (var file in new[] { "Dalamud.runtimeconfig.json", "Dalamud.Injector.runtimeconfig.json" })
        {
            using var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(output, file)));
            Assert.True(System.Text.Json.JsonElement.DeepEquals(config.RootElement.GetProperty("runtimeOptions"), requirements.GetProperty(file)));
            var bytesHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, file)))).ToLowerInvariant();
            Assert.Equal(bytesHash, report.RootElement.GetProperty("outputFiles").GetProperty(file).GetString());
            Assert.Equal(Convert.ToHexString(MD5.HashData(File.ReadAllBytes(Path.Combine(output, file)))), hashes.RootElement.GetProperty(file).GetString());
        }
        var patcherRuntime = report.RootElement.GetProperty("patcherRuntime");
        Assert.Equal(RuntimeInformation.FrameworkDescription, patcherRuntime.GetProperty("frameworkDescription").GetString());
        Assert.Equal(Environment.Version.ToString(), patcherRuntime.GetProperty("version").GetString());
        Assert.Equal(RuntimeInformation.OSDescription, patcherRuntime.GetProperty("osDescription").GetString());
        Assert.Equal(RuntimeInformation.OSArchitecture.ToString(), patcherRuntime.GetProperty("osArchitecture").GetString());
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), patcherRuntime.GetProperty("processArchitecture").GetString());
        var sdk = typeof(PackageManifest).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "BuildSdkVersion").Value;
        Assert.False(string.IsNullOrWhiteSpace(sdk));
        Assert.Equal(sdk, report.RootElement.GetProperty("patcherBuild").GetProperty("sdkVersion").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"runtimeOptions\":null}")]
    [InlineData("{\"runtimeOptions\":[]}")]
    public async Task PackageBuilder_RejectsInvalidRuntimeOptionsAndCleansStaging(string runtimeConfig)
    {
        Directory.CreateDirectory(this.root);
        var sourceZip = Path.Combine(this.root, "source.zip");
        using (var archive = ZipFile.Open(sourceZip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("Dalamud.runtimeconfig.json").Open()))
            await writer.WriteAsync(runtimeConfig);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceZip))).ToLowerInvariant();
        var cache = Path.Combine(this.root, "cache");
        Directory.CreateDirectory(cache);
        File.Move(sourceZip, Path.Combine(cache, $"{hash}.zip"));
        var output = Path.Combine(this.root, "result");
        var manifest = new PackageManifest("test", "13", "source-commit", "game", new ArtifactSpec("https://example.invalid/input.zip", hash), Array.Empty<AssemblyPatchSpec>());

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => PackageBuilder.BuildAsync(manifest, output, cache, null, TestContext.Current.CancellationToken));
        Assert.Contains("runtimeOptions", error.Message);
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateDirectories(this.root, ".result.staging.*"));
    }

    [Fact]
    public void Manifest_RejectsUnknownFieldAndLegacySnapshotConfiguration()
    {
        Directory.CreateDirectory(this.root);
        var unknown = Path.Combine(this.root, "unknown.json");
        File.WriteAllText(unknown, "{\"id\":\"a\",\"unexpected\":true}");
        Assert.Throws<System.Text.Json.JsonException>(() => PackageManifest.Load(unknown));

        var legacy = Path.Combine(this.root, "legacy.json");
        File.WriteAllText(legacy, "{\"id\":\"test\",\"support\":{}}");
        Assert.Throws<System.Text.Json.JsonException>(() => PackageManifest.Load(legacy));
    }

    [Fact]
    public async Task PackageBuilder_RejectsExistingOutputBeforeDownload()
    {
        var output = Path.Combine(this.root, "already-exists");
        Directory.CreateDirectory(output);
        var manifest = new PackageManifest(
            "test", "13.0.0.16", "e100ec2abdc490838f3422bf5fd5cda695a886fd", "test",
            new ArtifactSpec("https://example.invalid/input.zip", new string('0', 64)), Array.Empty<AssemblyPatchSpec>());

        await Assert.ThrowsAsync<IOException>(() => PackageBuilder.BuildAsync(manifest, output, Path.Combine(this.root, "cache"), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PackageBuilder_RejectsMissingDependenciesBeforeDownload()
    {
        var output = Path.Combine(this.root, "result");
        var manifest = new PackageManifest("test", "13", "commit", "game",
            new ArtifactSpec("https://example.invalid/input.zip", new string('0', 64)),
            [new AssemblyPatchSpec("Dalamud.dll", new string('1', 64), "core")]);

        await Assert.ThrowsAsync<FileNotFoundException>(() => PackageBuilder.BuildAsync(manifest, output,
            Path.Combine(this.root, "cache"), Path.Combine(this.root, "missing-dependencies"), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(output));
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
            Directory.Delete(this.root, recursive: true);
    }
}
