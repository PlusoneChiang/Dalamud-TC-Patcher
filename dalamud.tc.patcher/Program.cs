using Dalamud.Tc.Patcher.Packaging;

namespace Dalamud.Tc.Patcher;

/// <summary>解析命令列，將設定與路徑交給套件建置流程。</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            WriteUsage();
            return 0;
        }

        try
        {
            if (string.Equals(args[0], "build", StringComparison.OrdinalIgnoreCase))
            {
                var options = BuildOptions.Parse(args[1..]);
                var manifest = PackageManifest.Load(options.ManifestPath);
                var cacheDirectory = options.CacheDirectory ?? Path.Combine(Path.GetTempPath(), "dalamud.tc.patcher-cache");
                await PackageBuilder.BuildAsync(manifest, options.OutputDirectory, cacheDirectory, options.DependenciesDirectory, CancellationToken.None);
                Console.WriteLine("已成功產出套用補丁後的 Dalamud。\n");
            }
            else
            {
                Console.Error.WriteLine("不支援的命令。\n");
                WriteUsage();
                return 2;
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("作業已取消。");
            return 1;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"建立失敗：{ex.Message}");
            return 1;
        }
    }

    private static void WriteUsage()
    {
        Console.WriteLine("Dalamud TC Patcher");
        Console.WriteLine("用法：");
        Console.WriteLine("  dalamud.tc.patcher build --manifest <manifest.json> --output <新目錄> --dependencies <相依 DLL 目錄> [--cache <快取目錄>]");
        Console.WriteLine("  dalamud.tc.patcher help");
    }

    private sealed record BuildOptions(string ManifestPath, string OutputDirectory, string DependenciesDirectory, string? CacheDirectory)
    {
        public static BuildOptions Parse(IReadOnlyList<string> args)
        {
            string? manifest = null;
            string? output = null;
            string? cache = null;
            string? dependencies = null;
            for (var i = 0; i < args.Count; i++)
            {
                if (++i >= args.Count)
                    throw new ArgumentException($"選項 {args[i - 1]} 缺少值。");
                switch (args[i - 1])
                {
                    case "--manifest": manifest = args[i]; break;
                    case "--output": output = args[i]; break;
                    case "--dependencies": dependencies = args[i]; break;
                    case "--cache": cache = args[i]; break;
                    default: throw new ArgumentException($"不支援的選項：{args[i - 1]}。");
                }
            }
            if (string.IsNullOrWhiteSpace(manifest) || string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(dependencies))
                throw new ArgumentException("build 必須指定 --manifest、--output 與 --dependencies。");
            return new BuildOptions(manifest, output, dependencies, cache);
        }
    }

}
