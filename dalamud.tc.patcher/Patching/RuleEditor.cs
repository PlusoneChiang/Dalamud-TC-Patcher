using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Dalamud.Tc.Patcher.Patching;

/// <summary>供個別補丁定位官方成員與已注入 helper，建立明確的方法轉呼叫。</summary>
internal sealed class RuleEditor(ModuleDefinition module)
{
    public ModuleDefinition Module { get; } = module;
    public IReadOnlyDictionary<string, TypeDefinition> Helpers { get; set; } = new Dictionary<string, TypeDefinition>();

    public TypeDefinition Type(string name) => Module.GetTypes().SingleOrDefault(t => t.FullName == name)
        ?? throw new InvalidDataException($"找不到補丁目標型別：{name}");

    public MethodDefinition Method(string type, string name, int? parameterCount = null) =>
        SingleMethod(Type(type), name, parameterCount);

    public MethodDefinition Method(TypeDefinition type, string name, int? parameterCount = null) => SingleMethod(type, name, parameterCount);

    public MethodReference Helper(string type, string name, int? parameterCount = null) =>
        SingleMethod(Helpers.TryGetValue(type, out var helper) ? helper : throw new InvalidDataException($"找不到已編譯規則：{type}"), name, parameterCount);

    private static MethodDefinition SingleMethod(TypeDefinition type, string name, int? count)
    {
        var found = type.Methods.Where(m => m.Name == name && (!count.HasValue || m.Parameters.Count == count)).ToArray();
        if (found.Length != 1)
            throw new InvalidDataException($"補丁定位失敗：{type.FullName}::{name}，符合項目 {found.Length}。");
        return found[0];
    }

    /// <summary>以載入參數、呼叫 helper、回傳的新 body 完整取代原方法；原邏輯不會繼續執行。</summary>
    public void Forward(MethodDefinition target, MethodReference helper)
    {
        var passThis = target.HasThis && helper.Parameters.Count == target.Parameters.Count + 1;
        if (helper.HasThis || helper.Parameters.Count != target.Parameters.Count + (passThis ? 1 : 0) || helper.ReturnType.FullName != target.ReturnType.FullName)
            throw new InvalidDataException($"補丁轉呼叫簽章不符：{target.FullName} → {helper.FullName}");
        target.Body = new MethodBody(target);
        var il = target.Body.GetILProcessor();
        if (passThis) il.Emit(OpCodes.Ldarg_0);
        foreach (var argument in target.Parameters) il.Emit(OpCodes.Ldarg, argument);
        il.Emit(OpCodes.Call, helper);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>新增一個轉呼叫 helper 的方法，供既有程式或其他規則呼叫。</summary>
    public MethodDefinition AddForwarder(TypeDefinition owner, string name, MethodAttributes attributes, MethodReference helper, bool passThis)
    {
        var method = new MethodDefinition(name, passThis ? attributes & ~MethodAttributes.Static : attributes | MethodAttributes.Static, helper.ReturnType);
        foreach (var parameter in helper.Parameters.Skip(passThis ? 1 : 0))
            method.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
        owner.Methods.Add(method);
        Forward(method, helper);
        return method;
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException($"補丁前置條件不符：{message}");
    }
}
