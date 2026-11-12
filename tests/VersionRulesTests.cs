using Dalamud.Tc.Patcher.Patches;
using Dalamud.Tc.Patcher.Patching;
using Mono.Cecil;
using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace Dalamud.Tc.Patcher.Tests;

public sealed class VersionRulesTests
{
    [Theory]
    [InlineData("{\"id\":\"13.0.0.16-tc.1-rc.2\"}", "13.0.0.16-tc.1-rc.2")]
    [InlineData(null, "13.0.0.16")]
    [InlineData("{", "13.0.0.16")]
    [InlineData("{}", "13.0.0.16")]
    [InlineData("{\"id\":\" \"}", "13.0.0.16")]
    [InlineData("{\"id\":123}", "13.0.0.16")]
    [InlineData("[]", "13.0.0.16")]
    public void VersionRules_ReadMetadataAndSkipOfficialCheckWithoutChangingCompatibility(string? versionInfo, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "tc-version-rules", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var context = new AssemblyLoadContext("tc-version-fixture", isCollectible: true);
        try
        {
            var pack = ReferenceAssemblyBuilder.FindNet9ReferencePack();
            var original = RuleCompiler.Compile(new Dictionary<string, string> { ["Fixture.cs"] = """
                using System.Reflection;
                [assembly: AssemblyVersion("13.0.0.16")]
                [assembly: AssemblyMetadata("SCMVersion", "13.0.0.16")]
                [assembly: AssemblyMetadata("GitHash", "official-commit")]
                namespace Dalamud.Utility
                {
                    public static class Util
                    {
                        private static string scmVersionInternal;
                        public static string GetScmVersion() => scmVersionInternal ??= "13.0.0.16";
                        public static string GetActiveTrack() => "release";
                    }
                    public static class StringExtensions
                    {
                        public static bool IsNullOrEmpty(this string text) => string.IsNullOrEmpty(text);
                    }
                }
                namespace Dalamud.Support
                {
                    public static class DalamudReleases
                    {
                        public static int Requests;
                        public static void GetVersionForCurrentTrack() => Requests++;
                    }
                }
                namespace Dalamud.Interface.Internal.Windows.PluginInstaller
                {
                    public sealed class PluginInstallerWindow
                    {
                        public static int Reloads;
                        public static int Scans;
                        public void OnOpen()
                        {
                            Reloads++;
                            Scans++;
                            if (!Dalamud.Utility.StringExtensions.IsNullOrEmpty(Dalamud.Utility.Util.GetActiveTrack()))
                                Dalamud.Support.DalamudReleases.GetVersionForCurrentTrack();
                        }
                    }
                }
                """ }, new Dictionary<string, byte[]>(), pack, "Fixture.Version");
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            using var input = new MemoryStream(original);
            using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
            var code = RuleCompiler.ReadSources(["VersionPatches.cs"]);
            var compiled = RuleCompiler.Compile(code,
                new Dictionary<string, byte[]> { ["Fixture.Version.dll"] = ReferenceAssemblyBuilder.CreatePublicReference(module) }, pack);
            var editor = new RuleEditor(module) { Helpers = RuleInjector.Inject(module, compiled, PatchSet.HelperParents) };
            VersionPatches.Apply(editor);
            var path = Path.Combine(root, "Fixture.Version.dll");
            module.Write(path);
            var infoPath = Path.Combine(root, "versionInfo.json");
            if (versionInfo != null) File.WriteAllText(infoPath, versionInfo);
            var assembly = context.LoadFromAssemblyPath(path);
            var util = assembly.GetType("Dalamud.Utility.Util")!;
            Assert.Equal(expected, util.GetMethod("GetScmVersion")!.Invoke(null, null));
            File.WriteAllText(infoPath, "{\"id\":\"changed-after-first-read\"}");
            Assert.Equal(expected, util.GetMethod("GetScmVersion")!.Invoke(null, null));
            Assert.Equal("release", util.GetMethod("GetActiveTrack")!.Invoke(null, null));
            Assert.Equal(new Version(13, 0, 0, 16), assembly.GetName().Version);
            Assert.Equal("13.0.0.16", assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "SCMVersion").Value);
            Assert.Equal("official-commit", assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "GitHash").Value);
            var installer = assembly.GetType("Dalamud.Interface.Internal.Windows.PluginInstaller.PluginInstallerWindow")!;
            installer.GetMethod("OnOpen")!.Invoke(Activator.CreateInstance(installer), null);
            Assert.Equal(1, installer.GetField("Reloads")!.GetValue(null));
            Assert.Equal(1, installer.GetField("Scans")!.GetValue(null));
            Assert.Equal(0, assembly.GetType("Dalamud.Support.DalamudReleases")!.GetField("Requests")!.GetValue(null));
        }
        finally
        {
            context.Unload();
            Directory.Delete(root, recursive: true);
        }
    }
}
