using Dalamud.Tc.Patcher.Patching;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dalamud.Tc.Patcher.Packaging;

/// <summary>統籌下載、已建置相依檔案、DLL 補丁與輸出報告；全部成功才發布暫存目錄。</summary>
internal static class PackageBuilder
{
    private static readonly string[] DependencyFiles = ["FFXIVClientStructs.dll", "Lumina.dll", "Lumina.Excel.dll"];

    public static async Task BuildAsync(PackageManifest manifest, string outputDirectory, string cacheDirectory, string? dependenciesDirectory, CancellationToken cancellationToken)
    {
        manifest.Validate();
        var output = Path.GetFullPath(outputDirectory);
        var cache = Path.GetFullPath(cacheDirectory);
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("輸出目錄已存在，為避免覆寫而拒絕繼續執行。");
        if (string.Equals(cache, output, StringComparison.OrdinalIgnoreCase) || cache.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("快取不可位於輸出目錄內。");
        if (manifest.Patches.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(dependenciesDirectory))
                throw new ArgumentException("請先編譯外部相依，並以 --dependencies 指定 DLL 目錄。");
            dependenciesDirectory = Path.GetFullPath(dependenciesDirectory);
            foreach (var name in DependencyFiles)
                if (!File.Exists(Path.Combine(dependenciesDirectory, name)))
                    throw new FileNotFoundException($"相依目錄缺少 {name}，請先建置外部專案。", Path.Combine(dependenciesDirectory, name));
        }
        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(output)}.staging.{Guid.NewGuid():N}");
        try
        {
            Console.WriteLine("正在取得國際服 Dalamud 封裝…");
            var zip = await new ArtifactDownloader().DownloadAsync(manifest.Upstream, cache, cancellationToken);
            SafeZip.Extract(zip, staging);
            foreach (var patch in manifest.Patches)
                if (!await ArtifactDownloader.HasExpectedSha256Async(Path.Combine(staging, patch.Path), patch.InputSha256, cancellationToken))
                    throw new InvalidDataException($"官方 DLL SHA-256 不符：{patch.Path}");

            IReadOnlyDictionary<string, string> sources = new Dictionary<string, string>();
            var dependencyHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (manifest.Patches.Count > 0)
            {
                Console.WriteLine("正在套用已建置的相依 DLL…");
                foreach (var name in DependencyFiles)
                {
                    var bytes = await File.ReadAllBytesAsync(Path.Combine(dependenciesDirectory!, name), cancellationToken);
                    dependencyHashes.Add(name, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                    await File.WriteAllBytesAsync(Path.Combine(staging, name), bytes, cancellationToken);
                    File.Delete(Path.Combine(staging, Path.ChangeExtension(name, ".pdb")));
                }
                UpdateDependencyManifest(staging);
                Console.WriteLine("正在編譯並套用明確來源補丁…");
                sources = AssemblyPatcher.Apply(staging, manifest.Patches);
            }
            File.Delete(Path.Combine(staging, "hashes.json"));
            var md5 = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                if (name is "build-report.json" or "dalamud.txt" || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ipdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                md5.Add(Relative(staging, path), Convert.ToHexString(MD5.HashData(await File.ReadAllBytesAsync(path, cancellationToken))));
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "hashes.json"), JsonSerializer.Serialize(md5, PackageManifest.JsonOptions), cancellationToken);
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
                files.Add(Relative(staging, path), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken))).ToLowerInvariant());
            var patcherSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(PackageBuilder).Assembly.Location, cancellationToken))).ToLowerInvariant();
            var report = new { patcherSha256, manifest.Id, manifest.UpstreamVersion, manifest.UpstreamSourceCommit, manifest.GameVersion, inputSha256 = manifest.Upstream.Sha256, patches = manifest.Patches, ruleSourceSha256 = sources, dependencySha256 = dependencyHashes, outputFiles = files };
            await File.WriteAllTextAsync(Path.Combine(staging, "build-report.json"), JsonSerializer.Serialize(report, PackageManifest.JsonOptions), cancellationToken);
            Directory.Move(staging, output);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '\\');

    private static void UpdateDependencyManifest(string directory)
    {
        var path = Path.Combine(directory, "Dalamud.deps.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var targets = root["targets"]!.AsObject();
        foreach (var target in targets.Select(t => t.Value!.AsObject()))
        foreach (var library in target)
        {
            var runtime = library.Value?["runtime"]?.AsObject();
            if (runtime is null) continue;
            foreach (var dependency in new[] { "FFXIVClientStructs", "Lumina", "Lumina.Excel" })
                if (runtime[dependency + ".dll"] is System.Text.Json.Nodes.JsonObject metadata)
                {
                    var file = Path.Combine(directory, dependency + ".dll");
                    metadata["assemblyVersion"] = System.Reflection.AssemblyName.GetAssemblyName(file).Version!.ToString();
                    metadata.Remove("fileVersion");
                }
        }
        File.WriteAllText(path, root.ToJsonString(PackageManifest.JsonOptions));
    }
}
