using Mono.Cecil;

namespace Dalamud.Tc.Patcher.Patching;

/// <summary>
/// 將已編譯的規則型別注入目標 Dalamud 模組，協調注入位置與巢狀型別命名，不放置本地化業務規則。
/// </summary>
internal static class RuleInjector
{
    /// <summary>
    /// 於規則編譯完成後呼叫，依父型別對應建立規則型別並回傳可供後續方法替換使用的型別對照。
    /// </summary>
    public static IReadOnlyDictionary<string, TypeDefinition> Inject(ModuleDefinition target, byte[] compiledRules, IReadOnlyDictionary<string, string> parents)
    {
        using var stream = new MemoryStream(compiledRules);
        using var source = ModuleDefinition.ReadModule(stream, new ReaderParameters { InMemory = true, AssemblyResolver = target.AssemblyResolver });
        var selected = source.Types.Where(t => t.Name != "<Module>").ToList();
        var copier = new MetadataCopier(source, target, target.Assembly.Name, renameGeneratedRoots: true);
        var result = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var type in selected)
        {
            if (parents.TryGetValue(type.FullName, out var parentName))
            {
                if (parentName.Length == 0)
                {
                    if (target.GetTypes().Any(t => t.FullName == type.FullName))
                        throw new InvalidDataException($"新增規則型別已存在：{type.FullName}");
                    copier.DefineType(type, target.Types);
                    result.Add(type.FullName, copier.MappedType(type));
                    continue;
                }
                var parent = target.GetTypes().SingleOrDefault(t => t.FullName == parentName) ?? throw new InvalidDataException($"找不到規則目標型別：{parentName}");
                copier.DefineType(type, parent.NestedTypes);
                var injected = copier.MappedType(type);
                injected.Namespace = string.Empty;
                injected.Name = "__Tc_" + type.Name;
                injected.Attributes = (injected.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedAssembly;
                result.Add(type.FullName, injected);
            }
            else
            {
                if (!MetadataCopier.IsGeneratedRoot(type))
                    throw new InvalidDataException($"規則類別未指定目標：{type.FullName}");
                copier.DefineType(type, target.Types);
            }
        }
        copier.PopulateAll();
        // PopulateAll 複製來源可見性後，再恢復 nested helper 的可見性。
        foreach (var type in result.Values.Where(t => t.IsNested))
            type.Attributes = (type.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedAssembly;
        return result;
    }
}
