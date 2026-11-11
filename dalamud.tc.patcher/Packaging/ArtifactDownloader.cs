using System.Security.Cryptography;

namespace Dalamud.Tc.Patcher.Packaging;

/// <summary>下載官方封裝並核對 SHA-256；快取也必須通過驗證才能使用。</summary>
internal sealed class ArtifactDownloader
{
    private const long MaximumDownloadBytes = 1024L * 1024 * 1024;
    private static readonly HttpClient HttpClient = new();
    public async Task<string> DownloadAsync(ArtifactSpec artifact, string cacheDirectory, CancellationToken cancellationToken)
    {
        artifact.Validate("artifact"); ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory); Directory.CreateDirectory(cacheDirectory);
        var expectedHash = artifact.Sha256.ToLowerInvariant(); var cachedPath = Path.Combine(cacheDirectory, $"{expectedHash}.zip");
        if (File.Exists(cachedPath)) { if (await HasExpectedSha256Async(cachedPath, expectedHash, cancellationToken)) return cachedPath; File.Delete(cachedPath); }
        var temporaryPath = Path.Combine(cacheDirectory, $".{expectedHash}.{Guid.NewGuid():N}.downloading");
        try
        {
            using var response = await HttpClient.GetAsync(artifact.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
                throw new InvalidDataException("下載檔案大小超過安全上限。");
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
                await CopyWithLimitAsync(source, destination, cancellationToken);
            if (!await HasExpectedSha256Async(temporaryPath, expectedHash, cancellationToken)) throw new InvalidDataException("下載檔案的 SHA-256 與 manifest 不符。");
            try { File.Move(temporaryPath, cachedPath, overwrite: false); }
            catch (IOException)
            {
                if (!File.Exists(cachedPath) || !await HasExpectedSha256Async(cachedPath, expectedHash, cancellationToken))
                    throw;
            }
            return cachedPath;
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
    internal static async Task<bool> HasExpectedSha256Async(string path, string expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)), expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 128];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (checked(written + read) > MaximumDownloadBytes)
                throw new InvalidDataException("下載檔案大小超過安全上限。");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
        }
    }
}
