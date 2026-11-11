using Mono.Cecil;

namespace Dalamud.Tc.Patcher.Patching;

/// <summary>
/// 建立規則編譯所需的參考組件並尋找 .NET reference pack；僅調整編譯期可見性，不放置補丁規則。
/// </summary>
internal static class ReferenceAssemblyBuilder
{
    /// <summary>建立成員皆可供規則編譯使用的參考組件；只用於編譯，不能作為實際輸出的 Dalamud DLL。</summary>
    public static byte[] CreatePublicReference(ModuleDefinition source)
    {
        using var original = new MemoryStream();
        source.Write(original, new WriterParameters { WriteSymbols = false, Timestamp = 0 });
        original.Position = 0;
        using var copy = ModuleDefinition.ReadModule(original, new ReaderParameters { InMemory = true, AssemblyResolver = source.AssemblyResolver });
        foreach (var type in copy.GetTypes())
        {
            type.Attributes = (type.Attributes & ~Mono.Cecil.TypeAttributes.VisibilityMask) | (type.IsNested ? Mono.Cecil.TypeAttributes.NestedPublic : Mono.Cecil.TypeAttributes.Public);
            foreach (var field in type.Fields)
                field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask & ~FieldAttributes.InitOnly) | FieldAttributes.Public;
            foreach (var method in type.Methods)
                method.Attributes = (method.Attributes & ~Mono.Cecil.MethodAttributes.MemberAccessMask) | Mono.Cecil.MethodAttributes.Public;
        }
        using var output = new MemoryStream();
        copy.Write(output, new WriterParameters { WriteSymbols = false, Timestamp = 0 });
        return output.ToArray();
    }

    /// <summary>尋找已安裝的最新 .NET 9 reference pack，編譯 規則前呼叫；找不到時明確要求安裝 SDK。</summary>
    public static string FindNet9ReferencePack()
    {
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var root = Path.GetFullPath(Path.Combine(runtime, "../../.."));
        var packs = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
        var found = Directory.Exists(packs) ? Directory.GetDirectories(packs).Where(p => Path.GetFileName(p).StartsWith("9.", StringComparison.Ordinal)).OrderByDescending(p => Version.Parse(Path.GetFileName(p))).Select(p => Path.Combine(p, "ref", "net9.0")).FirstOrDefault(Directory.Exists) : null;
        return found ?? throw new InvalidOperationException("需要 .NET 9 reference pack 才能編譯 目標套件補丁；請使用已安裝 .NET 9 SDK 的開發環境。");
    }
}
