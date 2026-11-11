using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Dalamud.Tc.Patcher.Patching;

/// <summary>
/// 讀取內嵌規則原始碼並編譯成可注入組件；只負責編譯程序，不定義任何本地化補丁規則。
/// </summary>
internal static class RuleCompiler
{
    /// <summary>在準備編譯時讀取指定的內嵌規則來源，缺少資源即中止，避免產生不完整的補丁。</summary>
    public static IReadOnlyDictionary<string, string> ReadSources(IEnumerable<string> names)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var input = typeof(RuleCompiler).Assembly.GetManifestResourceStream("rules." + name)
                              ?? throw new InvalidDataException($"找不到補丁原始碼：{name}");
            using var reader = new StreamReader(input);
            result.Add(name, reader.ReadToEnd());
        }
        return result;
    }

    /// <summary>使用固定編譯選項將規則來源編譯為組件，供 <see cref="RuleInjector"/> 注入目標 DLL。</summary>
    public static byte[] Compile(IReadOnlyDictionary<string, string> sources, IReadOnlyDictionary<string, byte[]> references, string referencePack, string assemblyName = "Dalamud.Tc.SourceRules")
    {
        var framework = Directory.GetFiles(referencePack, "*.dll").OrderBy(p => p, StringComparer.Ordinal).Select(p => MetadataReference.CreateFromFile(p));
        var local = references.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => MetadataReference.CreateFromImage(p.Value));
        // 不定義 PATCHER，讓同一份補丁檔案只編譯實際執行邏輯。
        var syntax = sources.Select(p => CSharpSyntaxTree.ParseText(p.Value, new CSharpParseOptions(LanguageVersion.CSharp13), p.Key));
        var compilation = CSharpCompilation.Create(assemblyName, syntax, framework.Concat<MetadataReference>(local),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: true, deterministic: true, nullableContextOptions: NullableContextOptions.Enable));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        if (!result.Success)
            throw new InvalidDataException("補丁原始碼編譯失敗：\n" + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return output.ToArray();
    }

}
