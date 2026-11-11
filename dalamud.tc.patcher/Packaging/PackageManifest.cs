using Dalamud.Tc.Patcher.Patches;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dalamud.Tc.Patcher.Packaging;

/// <summary>描述官方輸入、輸出識別與補丁集合，並驗證設定完整性及路徑安全。</summary>
public sealed record PackageManifest(string Id, string UpstreamVersion, string UpstreamSourceCommit, string GameVersion, ArtifactSpec Upstream, IReadOnlyList<AssemblyPatchSpec> Patches)
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static PackageManifest Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("找不到 manifest 檔案。", manifestPath);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(manifestPath), JsonOptions) ?? throw new InvalidDataException("manifest 不可為空白。");
        manifest.Validate();
        return manifest;
    }
    internal void Validate()
    {
        Require(Id, nameof(Id)); Require(UpstreamVersion, nameof(UpstreamVersion)); Require(UpstreamSourceCommit, nameof(UpstreamSourceCommit)); Require(GameVersion, nameof(GameVersion));
        (Upstream ?? throw new InvalidDataException("manifest 缺少 upstream。")).Validate(nameof(Upstream));
        if (Patches is null) throw new InvalidDataException("manifest 的 patches 不可為 null。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in Patches)
        {
            patch.Validate();
            if (!paths.Add(patch.Path)) throw new InvalidDataException($"patch 路徑重複：{patch.Path}");
        }
    }
    internal static void Require(string? value, string name) { if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"manifest 欄位 {name} 不可為空白。"); }
    internal static void ValidateSha256(string? value, string name) { if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)) throw new InvalidDataException($"{name} 必須是 64 個十六進位字元的 SHA-256 雜湊。"); }
    internal static void ValidateRelativePath(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\\') >= 0 || value.IndexOf('\0') >= 0 || value.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(value, "^[A-Za-z]:", RegexOptions.CultureInvariant)) throw new InvalidDataException($"{name} 必須是安全的相對 ZIP 路徑。");
        if (value.Split('/', StringSplitOptions.None).Any(x => x.Length == 0 || x is "." or ".." || x.Contains(":", StringComparison.Ordinal))) throw new InvalidDataException($"{name} 必須是安全的相對 ZIP 路徑。");
    }
}
public sealed record ArtifactSpec(string Url, string Sha256)
{
    internal void Validate(string name) { PackageManifest.Require(Url, $"{name}.url"); if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException($"{name}.url 必須使用 HTTPS。"); PackageManifest.ValidateSha256(Sha256, $"{name}.sha256"); }
}
public sealed record AssemblyPatchSpec(string Path, string InputSha256, string RuleSet)
{
    internal void Validate()
    {
        PackageManifest.ValidateRelativePath(Path, "patch.path");
        PackageManifest.ValidateSha256(InputSha256, "patch.inputSha256");
        var expected = PatchSet.TargetAssembly(RuleSet);
        if (Path != expected) throw new InvalidDataException($"補丁集合 {RuleSet} 必須套用於 {expected}。");
    }
}
