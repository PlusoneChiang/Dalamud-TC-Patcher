using Dalamud.Tc.Patcher.Packaging;
using Dalamud.Tc.Patcher.Patching;
using Mono.Cecil;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Runtime.Loader;
using Xunit;

namespace Dalamud.Tc.Patcher.Tests;

public sealed class SourceRulesTests
{
    [Fact]
    public void AuthoredLanguageRules_MapTcToLumina8AndPluginCulture()
    {
        var source = RuleCompiler.ReadSources(["LanguagePatches.cs"])["LanguagePatches.cs"];
        var language = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken).DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "LanguageRules");
        // 純語言測試只取同檔的語言實作，字型所需的官方 API 由完整套用驗證。
        var code = new Dictionary<string, string> { ["LanguageRules.cs"] = """
            using System;
            using System.Globalization;
            using Dalamud;
            using Dalamud.Data;
            using Dalamud.Game;
            using Dalamud.Utility;
            using Lumina.Data;
            namespace Dalamud.Tc.Rules {
            """ + language.ToFullString() + "\n}" };
        code.Add("Stubs.cs", """
            namespace Dalamud.Game { public enum ClientLanguage { Japanese, English, German, French, SimplifiedChinese, TraditionalChinese } }
            namespace Lumina.Data { public enum Language : ushort { None, Japanese, English, German, French, TraditionalChinese = 8 } }
            namespace Dalamud.Data { public sealed class DataManager { public Dalamud.Game.ClientLanguage Language { get; set; } } }
            namespace Dalamud.Utility {
                public static class ClientLanguageExtensions {
                    public static Lumina.Data.Language ToLumina(this Dalamud.Game.ClientLanguage value) => Dalamud.Tc.Rules.LanguageRules.ToLumina(value);
                }
            }
            """);
        var bytes = RuleCompiler.Compile(code, new Dictionary<string, byte[]>(), ReferenceAssemblyBuilder.FindNet9ReferencePack());
        var context = new AssemblyLoadContext("tc-language-fixture", isCollectible: true);
        try
        {
            using var stream = new MemoryStream(bytes);
            var assembly = context.LoadFromStream(stream);
            var rule = assembly.GetType("Dalamud.Tc.Rules.LanguageRules")!;
            var gameLanguage = assembly.GetType("Dalamud.Game.ClientLanguage")!;
            var tc = Enum.ToObject(gameLanguage, 5);
            Assert.Equal(8, Convert.ToInt32(rule.GetMethod("ToLumina")!.Invoke(null, [tc])));
            Assert.Equal("tc", rule.GetMethod("ToCode")!.Invoke(null, [tc]));
            Assert.Equal("zh-Hant", rule.GetMethod("GetPluginLanguageCode")!.Invoke(null, ["tc"]));
            var managerType = assembly.GetType("Dalamud.Data.DataManager")!;
            var manager = Activator.CreateInstance(managerType)!;
            managerType.GetProperty("Language")!.SetValue(manager, tc);
            var resolved = rule.GetMethod("ResolveLuminaLanguageValue")!.Invoke(null, [manager, Enum.ToObject(gameLanguage, 1)]);
            Assert.Equal(8, Convert.ToInt32(resolved));
        }
        finally { context.Unload(); }
    }

    [Fact]
    public async Task AuthoredRules_AccessPrivateParentAndExecuteAsyncWithoutTcBinary()
    {
        var root = Path.Combine(Path.GetTempPath(), "tc-source-rules", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var context = new AssemblyLoadContext("tc-source-fixture", isCollectible: true);
        try
        {
            var pack = ReferenceAssemblyBuilder.FindNet9ReferencePack();
            var original = RuleCompiler.Compile(new Dictionary<string, string> { ["Target.cs"] = """
                namespace Fixture;
                public sealed class Target
                {
                    private readonly int value;
                    public Target(int value) { this.value = value; }
                    public int Calculate(int increment) => this.value + increment;
                    public int Unchanged() => 123;
                    public System.Threading.Tasks.Task<int[]> ValuesAsync() => System.Threading.Tasks.Task.FromResult(new[] { 0 });
                }
                """ }, new Dictionary<string, byte[]>(), pack, "Fixture.Target");
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            using var input = new MemoryStream(original);
            using var target = ModuleDefinition.ReadModule(input, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
            var rules = RuleCompiler.Compile(new Dictionary<string, string> { ["Rule.cs"] = """
                using System.Threading.Tasks;
                namespace Authored;
                public static class Rule
                {
                    public static int Calculate(Fixture.Target self, int increment) => self.value + increment + 1;
                    public static async Task<int[]> ValuesAsync(Fixture.Target self) =>
                        await Task.FromResult(new[] { self.value, self.value + 1 }).ConfigureAwait(false);
                }
                """ }, new Dictionary<string, byte[]> { ["Fixture.Target.dll"] = ReferenceAssemblyBuilder.CreatePublicReference(target) }, pack);
            var editor = new RuleEditor(target) { Helpers = RuleInjector.Inject(target, rules, new Dictionary<string, string> { ["Authored.Rule"] = "Fixture.Target" }) };
            editor.Forward(editor.Method("Fixture.Target", "Calculate"), editor.Helper("Authored.Rule", "Calculate"));
            editor.Forward(editor.Method("Fixture.Target", "ValuesAsync"), editor.Helper("Authored.Rule", "ValuesAsync"));
            Assert.DoesNotContain(target.AssemblyReferences, r => r.Name == "Dalamud.Tc.SourceRules");
            var path = Path.Combine(root, "Fixture.Target.dll");
            target.Write(path);
            var type = context.LoadFromAssemblyPath(path).GetType("Fixture.Target")!;
            var instance = Activator.CreateInstance(type, 7)!;
            Assert.Equal(13, type.GetMethod("Calculate")!.Invoke(instance, [5]));
            Assert.Equal(123, type.GetMethod("Unchanged")!.Invoke(instance, null));
            var values = await (Task<int[]>)type.GetMethod("ValuesAsync")!.Invoke(instance, null)!;
            Assert.Equal([7, 8], values);
            Assert.True(target.GetType("Fixture.Target").Fields.Single(f => f.Name == "value").IsPrivate);
        }
        finally
        {
            context.Unload();
            Directory.Delete(root, recursive: true);
        }
    }
}
