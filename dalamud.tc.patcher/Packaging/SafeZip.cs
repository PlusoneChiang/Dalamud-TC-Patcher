using System.IO.Compression;
using System.Buffers;

namespace Dalamud.Tc.Patcher.Packaging;

/// <summary>安全解壓封裝，限制路徑、檔案種類與解壓量；失敗時清理目標目錄。</summary>
internal static class SafeZip
{
    private const long MaximumEntryBytes = 512L * 1024 * 1024;
    private const long MaximumTotalBytes = 1024L * 1024 * 1024;
    private const int MaximumEntries = 10_000;
    public static void Extract(string zipPath, string destinationDirectory)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("找不到 ZIP 檔案。", zipPath);
        if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory)) throw new IOException("安全解壓目的地必須是尚不存在的新目錄。");
        Directory.CreateDirectory(destinationDirectory);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException("ZIP 項目數超過安全上限。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long totalLength = 0;
            long actualTotal = 0;
            foreach (var entry in archive.Entries)
            {
                var relativePath = ValidateEntry(entry); if (!names.Add(relativePath)) throw new InvalidDataException($"ZIP 含有大小寫重複項目：{relativePath}");
                if (IsDirectory(entry)) continue;
                if (entry.Length > MaximumEntryBytes || checked(totalLength + entry.Length) > MaximumTotalBytes) throw new InvalidDataException("ZIP 解壓後資料量超過安全上限。");
                if (entry.Length > 0 && (entry.CompressedLength == 0 || entry.Length / Math.Max(entry.CompressedLength, 1) > 200)) throw new InvalidDataException("ZIP 的壓縮比異常。");
                totalLength += entry.Length;
            }
            foreach (var entry in archive.Entries)
            {
                var relativePath = ValidateEntry(entry); var outputPath = Path.Combine(destinationDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (IsDirectory(entry)) { Directory.CreateDirectory(outputPath); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var input = entry.Open();
                using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var actualLength = CopyWithLimit(input, output, MaximumEntryBytes, MaximumTotalBytes - actualTotal);
                if (actualLength != entry.Length)
                    throw new InvalidDataException("ZIP 項目的實際解壓資料量與宣告值不符。");
                actualTotal += actualLength;
            }
        }
        catch { if (Directory.Exists(destinationDirectory)) Directory.Delete(destinationDirectory, recursive: true); throw; }
    }
    private static string ValidateEntry(ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0 || name.IndexOf('\\') >= 0 || name.StartsWith("/", StringComparison.Ordinal) || name.StartsWith("\\", StringComparison.Ordinal) || (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':')) throw new InvalidDataException("ZIP 含有不安全的路徑。");
        if (IsSymbolicLink(entry)) throw new InvalidDataException("ZIP 不允許符號連結項目。");
        var trimmed = name.TrimEnd('/'); if (trimmed.Length == 0) throw new InvalidDataException("ZIP 含有無效的根目錄項目。");
        if (trimmed.Split('/', StringSplitOptions.None).Any(x => x.Length == 0 || x is "." or ".." || x.Contains(":", StringComparison.Ordinal))) throw new InvalidDataException("ZIP 含有路徑穿越項目。");
        return trimmed;
    }
    private static bool IsDirectory(ZipArchiveEntry entry) => entry.FullName.EndsWith("/", StringComparison.Ordinal);
    private static bool IsSymbolicLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static long CopyWithLimit(Stream input, Stream output, long entryLimit, long totalRemaining)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 128);
        try
        {
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (checked(written + read) > entryLimit || checked(written + read) > totalRemaining)
                    throw new InvalidDataException("ZIP 的實際解壓資料量超過安全上限。");
                output.Write(buffer, 0, read);
                written += read;
            }
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
