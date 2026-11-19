using System.Reflection;
using System.Runtime.Loader;

using Dalamud.Tc.Patcher.Patching;
using Dalamud.Tc.Patcher.Patches;

using Mono.Cecil;
using Mono.Cecil.Rocks;

using Xunit;

namespace Dalamud.Tc.Patcher.Tests;

public sealed class FontRulesTests
{
    [Theory]
    [InlineData("tc", null, 1, 0)]
    [InlineData("tc", false, 1, 0)]
    [InlineData("tc", true, 1, 0)]
    [InlineData("tw", null, 0, 1)]
    [InlineData("zh", null, 0, 0)]
    [InlineData("en", null, 0, 0)]
    [InlineData("ko", null, 0, 0)]
    public void TraditionalChineseFontBranch_會從所有既有路徑執行(string language, bool? encounteredHangul, int expectedTraditionalChineseCalls, int expectedTwCalls)
    {
        var original = RuleCompiler.Compile(new Dictionary<string, string>
        {
            ["Fixture.cs"] = """
                namespace Dalamud
                {
                    public static class Service<T> where T : class, new()
                    {
                        public static T Value = new();
                        public static T? Nullable;
                        public static T Get() => Value;
                        public static T? GetNullable() => Nullable;
                    }
                }

                namespace Dalamud.Configuration.Internal
                {
                    public sealed class DalamudConfiguration { public string EffectiveLanguage { get; set; } = "en"; }
                }

                namespace Dalamud.Interface.Internal
                {
                    public sealed class DalamudIme { public bool EncounteredHangul { get; set; } }
                }

                namespace Dalamud.Interface.ManagedFontAtlas
                {
                    public struct ImFontPtr { public bool IsNull { get; set; } }
                    public struct SafeFontConfig { public ImFontPtr MergeFont { get; set; } }
                }

                namespace Dalamud.Interface.ManagedFontAtlas.Internals
                {
                    using Dalamud;
                    using Dalamud.Configuration.Internal;
                    using Dalamud.Interface.Internal;
                    using Dalamud.Interface.ManagedFontAtlas;

                    public sealed class FontAtlasFactory
                    {
                        public sealed class BuildToolkit
                        {
                            public ImFontPtr Font => new() { IsNull = false };

                            public void AttachExtraGlyphsForDalamudLanguage(in SafeFontConfig fontConfig)
                            {
                                var targetFont = fontConfig.MergeFont;
                                if (targetFont.IsNull)
                                    targetFont = this.Font;
                                if (targetFont.IsNull)
                                    return;

                                if (Service<DalamudConfiguration>.Get().EffectiveLanguage == "ko"
                                    || Service<DalamudIme>.GetNullable()?.EncounteredHangul is true)
                                    Dalamud.Tc.Rules.FontRules.KoreanCalls++;

                                if (Service<DalamudConfiguration>.Get().EffectiveLanguage == "tw")
                                    Dalamud.Tc.Rules.FontRules.TwCalls++;
                                else if (Service<DalamudConfiguration>.Get().EffectiveLanguage == "zh")
                                    _ = targetFont;
                            }
                        }
                    }
                }

                namespace Dalamud.Tc.Rules
                {
                    using Dalamud.Interface.ManagedFontAtlas;
                    using Dalamud.Interface.ManagedFontAtlas.Internals;

                    public static class FontRules
                    {
                        public static int TraditionalChineseCalls;
                        public static int TwCalls;
                        public static int KoreanCalls;

                        public static void AddTraditionalChineseFonts(FontAtlasFactory.BuildToolkit toolkit, ImFontPtr targetFont, in SafeFontConfig fontConfig) => TraditionalChineseCalls++;
                    }
                }
                """,
        }, new Dictionary<string, byte[]>(), ReferenceAssemblyBuilder.FindNet9ReferencePack(), "Fixture.FontRules");

        using var input = new MemoryStream(original);
        using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { InMemory = true });
        var editor = new RuleEditor(module)
        {
            Helpers = new Dictionary<string, TypeDefinition>
            {
                ["Dalamud.Tc.Rules.FontRules"] = module.GetType("Dalamud.Tc.Rules.FontRules")!,
            },
        };
        typeof(LanguagePatches).GetMethod("InsertTraditionalChineseFontBranch", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [editor]);

        foreach (var method in module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            method.Body.SimplifyMacros();
            method.Body.OptimizeMacros();
        }

        using var output = new MemoryStream();
        module.Write(output);
        var context = new AssemblyLoadContext("tc-font-branch-fixture", isCollectible: true);
        try
        {
            using var assemblyStream = new MemoryStream(output.ToArray());
            var assembly = context.LoadFromStream(assemblyStream);
            var configuration = assembly.GetType("Dalamud.Configuration.Internal.DalamudConfiguration")!;
            configuration.GetProperty("EffectiveLanguage")!.SetValue(GetServiceValue(assembly, configuration), language);
            var ime = assembly.GetType("Dalamud.Interface.Internal.DalamudIme")!;
            SetServiceNullable(assembly, ime, encounteredHangul is null ? null : Activator.CreateInstance(ime));
            if (encounteredHangul is not null)
                ime.GetProperty("EncounteredHangul")!.SetValue(GetServiceNullable(assembly, ime), encounteredHangul.Value);

            var toolkit = Activator.CreateInstance(assembly.GetType("Dalamud.Interface.ManagedFontAtlas.Internals.FontAtlasFactory+BuildToolkit")!)!;
            var config = Activator.CreateInstance(assembly.GetType("Dalamud.Interface.ManagedFontAtlas.SafeFontConfig")!)!;
            toolkit.GetType().GetMethod("AttachExtraGlyphsForDalamudLanguage")!.Invoke(toolkit, [config]);

            var rules = assembly.GetType("Dalamud.Tc.Rules.FontRules")!;
            Assert.Equal(expectedTraditionalChineseCalls, rules.GetField("TraditionalChineseCalls")!.GetValue(null));
            Assert.Equal(expectedTwCalls, rules.GetField("TwCalls")!.GetValue(null));
        }
        finally
        {
            context.Unload();
        }
    }

    private static object GetServiceValue(Assembly assembly, Type serviceType) =>
        assembly.GetType("Dalamud.Service`1")!.MakeGenericType(serviceType).GetField("Value")!.GetValue(null)!;

    private static object? GetServiceNullable(Assembly assembly, Type serviceType) =>
        assembly.GetType("Dalamud.Service`1")!.MakeGenericType(serviceType).GetField("Nullable")!.GetValue(null);

    private static void SetServiceNullable(Assembly assembly, Type serviceType, object? value) =>
        assembly.GetType("Dalamud.Service`1")!.MakeGenericType(serviceType).GetField("Nullable")!.SetValue(null, value);
}
