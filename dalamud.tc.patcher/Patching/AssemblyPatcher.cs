using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using Dalamud.Tc.Patcher.Packaging;
using Dalamud.Tc.Patcher.Patches;

namespace Dalamud.Tc.Patcher.Patching;

/// <summary>依序開啟官方組件、準備參考、編譯與注入規則、套用掛點並寫回 DLL。</summary>
internal static class AssemblyPatcher
{
    public static IReadOnlyDictionary<string, string> Apply(string directory, IReadOnlyList<AssemblyPatchSpec> specs)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(directory);
        resolver.AddSearchDirectory(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var modules = new Dictionary<string, ModuleDefinition>(StringComparer.Ordinal);
        try
        {
            foreach (var spec in specs)
            {
                var module = ModuleDefinition.ReadModule(Path.Combine(directory, spec.Path), new ReaderParameters { InMemory = true, ReadSymbols = false, AssemblyResolver = resolver });
                if (module.Assembly.Name.HasPublicKey || !module.Attributes.HasFlag(ModuleAttributes.ILOnly))
                    throw new InvalidDataException($"來源補丁僅支援未使用 strong name 的純 IL 組件：{spec.Path}");
                modules.Add(spec.RuleSet, module);
                PatchSet.Prepare(spec.RuleSet, new RuleEditor(module));
            }
            var references = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(directory, "*.dll"))
            {
                try
                {
                    using var dependency = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
                    references.Add(Path.GetFileName(path), File.ReadAllBytes(path));
                    foreach (var module in modules.Values)
                    foreach (var reference in module.AssemblyReferences.Where(r => r.Name == dependency.Assembly.Name.Name))
                    {
                        reference.Version = dependency.Assembly.Name.Version;
                        reference.PublicKeyToken = dependency.Assembly.Name.PublicKeyToken;
                    }
                }
                catch (BadImageFormatException) { }
            }
            foreach (var spec in specs) references[spec.Path] = ReferenceAssemblyBuilder.CreatePublicReference(modules[spec.RuleSet]);
            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var spec in specs)
            {
                var module = modules[spec.RuleSet];
                var sources = PatchSet.SourceFiles(spec.RuleSet);
                if (sources.Count > 0)
                {
                    var code = RuleCompiler.ReadSources(sources);
                    foreach (var (file, text) in code) hashes[file] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
                    var compiled = RuleCompiler.Compile(code, references, ReferenceAssemblyBuilder.FindNet9ReferencePack());
                    var editor = new RuleEditor(module) { Helpers = RuleInjector.Inject(module, compiled, PatchSet.HelperParents) };
                    PatchSet.Apply(spec.RuleSet, editor);
                }
                foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
                {
                    method.Body.SimplifyMacros();
                    method.Body.OptimizeMacros();
                }
                module.Write(Path.Combine(directory, spec.Path), new WriterParameters { WriteSymbols = false, Timestamp = 0, DeterministicMvid = true });
                File.Delete(Path.Combine(directory, Path.ChangeExtension(spec.Path, ".pdb")));
            }
            return hashes;
        }
        finally
        {
            foreach (var module in modules.Values) module.Dispose();
        }
    }
}
