#if PATCHER
using Dalamud.Tc.Patcher.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Dalamud.Tc.Patcher.Patches;

/// <summary>從版本資訊檔顯示 TC 版本，並略過遊戲內的官方新版提示。</summary>
internal static class VersionPatches
{
    public static void Apply(RuleEditor editor)
    {
        var onOpen = editor.Method("Dalamud.Interface.Internal.Windows.PluginInstaller.PluginInstallerWindow", "OnOpen", 0);
        var track = onOpen.Body.Instructions.SingleOrDefault(i =>
            i.OpCode == OpCodes.Call && i.Operand is MethodReference method &&
            method.DeclaringType.FullName == "Dalamud.Utility.Util" && method.Name == "GetActiveTrack")
            ?? throw new InvalidDataException("找不到外掛安裝器的更新通道判斷。");
        RuleEditor.Require(track.Next?.Operand is MethodReference check && check.Name == "IsNullOrEmpty" &&
                           track.Next.Next?.OpCode.Code is Code.Brtrue or Code.Brtrue_S &&
                           track.Next.Next.Operand is Instruction end &&
                           onOpen.Body.Instructions.Skip(onOpen.Body.Instructions.IndexOf(track.Next.Next) + 1)
                               .TakeWhile(i => i != end).Count(i => i.Operand is MethodReference method &&
                                   method.DeclaringType.FullName == "Dalamud.Support.DalamudReleases" &&
                                   method.Name == "GetVersionForCurrentTrack") == 1,
            "外掛安裝器的官方版本檢查結構已變更。");
        // 僅讓此處的空值判斷成立，保留更新通道在其他功能的用途。
        track.OpCode = OpCodes.Ldnull;
        track.Operand = null;
        editor.Forward(editor.Method("Dalamud.Utility.Util", "GetScmVersion", 0),
            editor.Helper("Dalamud.Tc.Rules.VersionRules", "GetScmVersion", 0));
    }
}
#else
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Dalamud.Utility;

namespace Dalamud.Tc.Rules
{
    public static class VersionRules
    {
        public static string GetScmVersion()
        {
            if (Util.scmVersionInternal != null) return Util.scmVersionInternal;
            var assembly = typeof(Util).Assembly;
            try
            {
                var path = Path.Combine(Path.GetDirectoryName(assembly.Location)!, "versionInfo.json");
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                    return Util.scmVersionInternal = id.GetString()!;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                // 版本資訊檔無法讀取時，仍需讓官方版本正常顯示。
            }
            return Util.scmVersionInternal = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "SCMVersion")?.Value ?? assembly.GetName().Version!.ToString();
        }
    }
}
#endif
