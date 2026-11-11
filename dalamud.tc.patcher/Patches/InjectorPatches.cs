// PATCHER：套件產製時定位與修改 DLL；另一部分是編譯後寫入 DLL 的實際執行邏輯。
#if PATCHER
using Dalamud.Tc.Patcher.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Dalamud.Tc.Patcher.Patches;

/// <summary>定位 Injector 的啟動參數方法，接上 VEH 平台設定規則。</summary>
internal static class InjectorPatches
{
    public static void Apply(RuleEditor editor)
    {
        var method = editor.Method("Dalamud.Injector.EntryPoint", "ExtractAndInitializeStartInfoFromArguments", 2);
        var helper = editor.Helper("Dalamud.Tc.Rules.InjectorRules", "ShouldEnableVeh");
        ReplaceVehAssignment(method, helper);
    }

    private static void ReplaceVehAssignment(MethodDefinition method, MethodReference helper)
    {
        RuleEditor.Require(method.HasBody && method.Parameters.Count == 2, "Injector 啟動參數方法簽章已變更。");
        var vehicleSetter = method.Body.Instructions.SingleOrDefault(x => x.OpCode == OpCodes.Callvirt && x.Operand is MethodReference reference && reference.Name == "set_BootVehEnabled")
            ?? throw new InvalidDataException("找不到 BootVehEnabled 設定位置。");
        var enabled = vehicleSetter.Previous;
        RuleEditor.Require(enabled?.OpCode == OpCodes.Ldc_I4_1 && enabled.Previous?.OpCode == OpCodes.Ldarg_0,
            "BootVehEnabled 的預設值結構已變更。");
        var platformStore = method.Body.Instructions.SingleOrDefault(x => x.OpCode == OpCodes.Callvirt && x.Operand is MethodReference reference && reference.Name == "set_Platform")
            ?? throw new InvalidDataException("找不到 Injector 平台設定位置。");
        var platform = platformStore.Previous ?? throw new InvalidDataException("找不到 Injector 平台本地變數。");
        RuleEditor.Require(platform.OpCode.Code is Code.Ldloc or Code.Ldloc_S,
            "Injector 平台本地變數結構已變更。");

        enabled!.OpCode = platform.OpCode;
        enabled.Operand = platform.Operand;
        var il = method.Body.GetILProcessor();
        var contains = method.Body.Instructions.FirstOrDefault(x => x.OpCode == OpCodes.Callvirt && x.Operand is MethodReference reference && reference.Name == "Contains" && reference.DeclaringType.FullName == "System.Collections.Generic.List`1<System.String>")?.Operand as MethodReference
            ?? throw new InvalidDataException("找不到 Injector 參數 Contains 呼叫。");
        il.InsertAfter(enabled, il.Create(OpCodes.Ldarg_1));
        il.InsertAfter(enabled.Next!, il.Create(OpCodes.Ldstr, "--veh"));
        il.InsertAfter(enabled.Next!.Next!, il.Create(OpCodes.Callvirt, contains));
        il.InsertAfter(enabled.Next!.Next!.Next!, il.Create(OpCodes.Call, helper));
    }
}
#else
using System.Runtime.InteropServices;

namespace Dalamud.Tc.Rules
{
    /// <summary>
    /// 讓非 Windows 平台沿用原本的 --veh 顯式開關。
    /// </summary>
    public static class InjectorRules
    {
        public static bool ShouldEnableVeh(OSPlatform platform, bool requested) =>
            platform == OSPlatform.Windows || requested;
    }
}
#endif
