using Dalamud.Tc.Patcher.Patching;

namespace Dalamud.Tc.Patcher.Patches;

/// <summary>
/// 集中登錄目前的來源檔、helper 注入位置與套用順序。
/// 每個 *Patches.cs 同時包含實際執行邏輯與 IL 掛載點。
/// </summary>
internal static class PatchSet
{
    public static IReadOnlyDictionary<string, string> HelperParents { get; } = new Dictionary<string, string>
    {
        ["Dalamud.Tc.Rules.LanguageRules"] = "Dalamud.Utility.ClientLanguageExtensions",
        ["Dalamud.Tc.Rules.FontRules"] = "Dalamud.Interface.ManagedFontAtlas.Internals.FontAtlasFactory/BuildToolkit",
        // 空字串代表新增最外層型別，其餘規則都成為指定型別的 nested helper。
        ["Dalamud.Interface.GlyphRangesChinese"] = "",
        ["Dalamud.Tc.Rules.PluginRepositoryRules"] = "Dalamud.Plugin.Internal.Types.PluginRepository",
        ["Dalamud.Tc.Rules.PluginManagerRules"] = "Dalamud.Plugin.Internal.PluginManager",
        ["Dalamud.Tc.Rules.LocalPluginRules"] = "Dalamud.Plugin.Internal.Types.LocalPlugin",
        ["Dalamud.Tc.Rules.DalamudPluginInterfaceRules"] = "Dalamud.Plugin.DalamudPluginInterface",
        ["Dalamud.Tc.Rules.PluginInstallerWindowRules"] = "Dalamud.Interface.Internal.Windows.PluginInstaller.PluginInstallerWindow",
        ["Dalamud.Tc.Rules.ScannerRules"] = "Dalamud.Game.SigScanner",
        ["Dalamud.Tc.Rules.VersionRules"] = "Dalamud.Utility.Util",
        ["Dalamud.Tc.Rules.InjectorRules"] = "Dalamud.Injector.EntryPoint",
    };

    public static string TargetAssembly(string ruleSet) => ruleSet switch
    {
        "core" => "Dalamud.dll",
        "common" => "Dalamud.Common.dll",
        "injector" => "Dalamud.Injector.dll",
        _ => throw new InvalidDataException($"未知的來源補丁集合：{ruleSet}"),
    };

    public static IReadOnlyList<string> SourceFiles(string ruleSet) => ruleSet switch
    {
        "core" => ["LanguagePatches.cs", "RepositoryPatches.cs", "ScannerPatches.cs", "VersionPatches.cs"],
        "common" => [],
        "injector" => ["InjectorPatches.cs"],
        _ => throw new InvalidDataException($"未知的來源補丁集合：{ruleSet}"),
    };

    /// <summary>在編譯規則前建立必要成員，讓參考副本包含它們。</summary>
    public static void Prepare(string ruleSet, RuleEditor editor)
    {
        switch (ruleSet)
        {
            case "core":
                LanguagePatches.Prepare(editor);
                RepositoryPatches.Prepare(editor);
                ScannerPatches.Prepare(editor);
                break;
            case "common":
                LanguagePatches.ApplyCommon(editor);
                break;
        }
    }

    /// <summary>在 helper 注入完成後，接上官方方法與呼叫點。</summary>
    public static void Apply(string ruleSet, RuleEditor editor)
    {
        switch (ruleSet)
        {
            case "core":
                LanguagePatches.Apply(editor);
                RepositoryPatches.Apply(editor);
                ScannerPatches.Apply(editor);
                VersionPatches.Apply(editor);
                break;
            case "injector":
                InjectorPatches.Apply(editor);
                break;
        }
    }
}
