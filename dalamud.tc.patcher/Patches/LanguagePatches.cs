// PATCHER：套件產製時定位與修改 DLL；另一部分是編譯後寫入 DLL 的實際執行邏輯。
#if PATCHER
using Dalamud.Tc.Patcher.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;

namespace Dalamud.Tc.Patcher.Patches;

/// <summary>
/// 語言與字型的 IL 掛載點；實際執行邏輯位於同檔的另一個編譯區段。
/// </summary>
internal static class LanguagePatches
{
    public static void Prepare(RuleEditor editor)
    {
        AddClientLanguage(editor.Type("Dalamud.Game.ClientLanguage"));
        AddDalamudAssets(editor.Type("Dalamud.DalamudAsset"));
        ReplaceInlineNotoSansJapaneseAsset(editor);
    }

    public static void Apply(RuleEditor editor)
    {
        Forward(editor, "Dalamud.Utility.ClientLanguageExtensions", "ToLumina", "Dalamud.Tc.Rules.LanguageRules", "ToLumina");
        Forward(editor, "Dalamud.Utility.ClientLanguageExtensions", "ToCode", "Dalamud.Tc.Rules.LanguageRules", "ToCode");
        Forward(editor, "Dalamud.Utility.ClientLanguageExtensions", "ToClientLanguage", "Dalamud.Tc.Rules.LanguageRules", "ToClientLanguage");
        ReplaceApplicableLanguageCode(editor);
        Forward(editor, "Dalamud.Localization", "GetCultureInfoFromLangCode", "Dalamud.Tc.Rules.LanguageRules", "GetCultureInfo");
        editor.AddForwarder(
            editor.Type("Dalamud.Localization"),
            "GetPluginLanguageCode",
            MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
            editor.Helper("Dalamud.Tc.Rules.LanguageRules", "GetPluginLanguageCode"),
            passThis: false);

        // 建構子寫入的遊戲語言與 plugin-scoped getter 都固定為 TC；不複製建構子其餘邏輯。
        ReplaceSetterInputWithTraditionalChinese(editor.Method("Dalamud.Game.ClientState.ClientState", ".ctor", 3), editor);
        editor.Forward(
            editor.Method("Dalamud.Game.ClientState.ClientStatePluginScoped", "get_ClientLanguage", 0),
            editor.Helper("Dalamud.Tc.Rules.LanguageRules", "TraditionalChinese"));

        // DataManager 的兩個公開 wrapper 保留原泛型方法，只將 language 解析改為 TC 可用的 sqpack 語言。
        editor.AddForwarder(
            editor.Type("Dalamud.Data.DataManager"),
            "ResolveLuminaLanguage",
            MethodAttributes.Assembly | MethodAttributes.HideBySig,
            editor.Helper("Dalamud.Tc.Rules.LanguageRules", "ResolveLuminaLanguageNullable", 2),
            passThis: true);
        editor.AddForwarder(
            editor.Type("Dalamud.Data.DataManager"),
            "ResolveLuminaLanguage",
            MethodAttributes.Assembly | MethodAttributes.HideBySig,
            editor.Helper("Dalamud.Tc.Rules.LanguageRules", "ResolveLuminaLanguageValue", 2),
            passThis: true);

        RewriteDataManagerWrappers(editor);
        RewriteNounLanguageConversions(editor);
        RewriteSeStringCreatorLanguageConversion(editor);
        InsertTraditionalChineseFontBranch(editor);
    }

    public static void ApplyCommon(RuleEditor editor) =>
        AddClientLanguage(editor.Type("Dalamud.Common.ClientLanguage"));

    private static void Forward(RuleEditor editor, string targetType, string targetMethod, string helperType, string helperMethod) =>
        editor.Forward(editor.Method(targetType, targetMethod, null), editor.Helper(helperType, helperMethod));

    private static void AddClientLanguage(TypeDefinition language)
    {
        AddEnumValue(language, "SimplifiedChinese", 4);
        AddEnumValue(language, "TraditionalChinese", 5);
    }

    private static void AddDalamudAssets(TypeDefinition assets)
    {
        var japanese = assets.Fields.First(x => x.Name == "NotoSansJpMedium");
        japanese.Constant = 1999;
        ReplaceAssetPaths(assets, japanese, "NotoSansCJKtc-Regular.otf");
        AddFontAsset(assets, "NotoSansScMedium", 2000, "NotoSansCJKtc-Regular.otf", required: true);
        AddFontAsset(assets, "NotoSansTcRegular", 2005, "NotoSansCJKtc-Regular.otf", required: false);
        AddFontAsset(assets, "NotoSansScRegular", 2006, "NotoSansSC-Regular.ttf", required: false);
    }

    private static void AddFontAsset(TypeDefinition assets, string name, int value, string path, bool required)
    {
        if (assets.Fields.Any(x => x.Name == name))
            return;
        var field = new FieldDefinition(name, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, assets)
        {
            Constant = value,
        };
        assets.Fields.Add(field);
        var template = assets.Fields.First(x => x.Name == "NotoSansJpMedium");
        field.CustomAttributes.Add(CreateAssetAttribute(assets.Module, template, required));
        field.CustomAttributes.Add(CreatePathAttribute(assets.Module, template, path));
    }

    private static void ReplaceAssetPaths(TypeDefinition assets, FieldDefinition field, string path)
    {
        var replacement = CreatePathAttribute(assets.Module, field, path);
        foreach (var attribute in field.CustomAttributes.Where(x => x.AttributeType.FullName == "Dalamud.Storage.Assets.DalamudAssetPathAttribute").ToArray())
            field.CustomAttributes.Remove(attribute);
        field.CustomAttributes.Add(replacement);
    }

    private static CustomAttribute CreateAssetAttribute(ModuleDefinition module, FieldDefinition template, bool required)
    {
        var source = template.CustomAttributes.Single(x => x.AttributeType.FullName == "Dalamud.Storage.Assets.DalamudAssetAttribute");
        var constructor = source.Constructor.Resolve()!.DeclaringType.Methods.Single(x => x.IsConstructor && x.Parameters.Count == 3);
        var attribute = new CustomAttribute(module.ImportReference(constructor));
        attribute.ConstructorArguments.Add(source.ConstructorArguments[0]);
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.ImportReference(constructor.Parameters[1].ParameterType), null));
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.Boolean, required));
        return attribute;
    }

    private static CustomAttribute CreatePathAttribute(ModuleDefinition module, FieldDefinition template, string path)
    {
        var source = template.CustomAttributes.First(x => x.AttributeType.FullName == "Dalamud.Storage.Assets.DalamudAssetPathAttribute");
        var valueType = source.ConstructorArguments[0].Type;
        var elementType = ((ArrayType)valueType).ElementType;
        var values = new[] { new CustomAttributeArgument(elementType, "UIRes"), new CustomAttributeArgument(elementType, path) };
        var attribute = new CustomAttribute(module.ImportReference(source.Constructor));
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(valueType, values));
        return attribute;
    }

    private static void AddEnumValue(TypeDefinition type, string name, int value)
    {
        if (type.Fields.Any(x => x.Name == name))
            return;
        type.Fields.Add(new FieldDefinition(name, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, type)
        {
            Constant = value,
        });
    }

    private static void ReplaceSetterInputWithTraditionalChinese(MethodDefinition constructor, RuleEditor editor)
    {
        var fieldStore = constructor.Body.Instructions.SingleOrDefault(x => x.OpCode == OpCodes.Stfld && x.Operand is FieldReference field && field.Name == "<ClientLanguage>k__BackingField")
            ?? throw new InvalidDataException("找不到 ClientState.ClientLanguage 的設定位置。");
        var start = fieldStore.Previous?.Previous?.Previous?.Previous
            ?? throw new InvalidDataException("ClientState.ClientLanguage 設定指令不完整。");
        RuleEditor.Require(start.OpCode == OpCodes.Ldarg_0 && start.Next?.OpCode == OpCodes.Ldarg_2 && start.Next.Next?.OpCode == OpCodes.Callvirt && start.Next.Next.Next?.OpCode == OpCodes.Callvirt,
            "ClientState 建構子的語言初始化結構已變更。");
        start.Next!.OpCode = OpCodes.Nop;
        start.Next.Operand = null;
        start.Next.Next!.OpCode = OpCodes.Nop;
        start.Next.Next.Operand = null;
        start.Next.Next.Next!.OpCode = OpCodes.Call;
        start.Next.Next.Next.Operand = editor.Helper("Dalamud.Tc.Rules.LanguageRules", "TraditionalChinese");
    }

    private static void ReplaceApplicableLanguageCode(RuleEditor editor)
    {
        var initializer = editor.Method("Dalamud.Localization", ".cctor", 0);
        var code = initializer.Body.Instructions.SingleOrDefault(x => x.OpCode == OpCodes.Ldstr && (string?)x.Operand == "tw")
            ?? throw new InvalidDataException("找不到 Localization.ApplicableLangCodes 的 tw 項目。");
        code.Operand = "tc";
    }

    private static void ReplaceInlineNotoSansJapaneseAsset(RuleEditor editor)
    {
        var family = editor.Method("Dalamud.Interface.FontIdentifier.IFontFamilyId", "ListDalamudFonts", 0);
        ReplaceAssetLiteralBeforeCall(
            family,
            call => call.Name == ".ctor" && call.DeclaringType.FullName == "Dalamud.Interface.FontIdentifier.DalamudAssetFontAndFamilyId",
            "IFontFamilyId.ListDalamudFonts");

        var prebaked = editor.Module.GetTypes()
            .SelectMany(type => type.Methods)
            .SingleOrDefault(method => method.Name == "CreateTemplateFont" && method.Parameters.Count == 2 && method.Body.Instructions.Any(x => x.OpCode == OpCodes.Callvirt && x.Operand is MethodReference call && call.Name == "AddDalamudAssetFont"))
            ?? throw new InvalidDataException("找不到 GamePrebakedFontHandle.CreateTemplateFont。");
        var asset = prebaked.Body.Instructions.SingleOrDefault(x => x.OpCode == OpCodes.Ldc_I4 && (int?)x.Operand == 2000 && x.Previous?.OpCode == OpCodes.Ldarg_1)
            ?? throw new InvalidDataException("找不到 GamePrebakedFontHandle.CreateTemplateFont 的 NotoSansJpMedium 常數。");
        RuleEditor.Require(prebaked.Body.Instructions.Any(x => x.OpCode == OpCodes.Callvirt && x.Operand is MethodReference call && call.Name == "AddDalamudAssetFont" && call.DeclaringType.FullName == "Dalamud.Interface.ManagedFontAtlas.IFontAtlasBuildToolkitPreBuild"),
            "GamePrebakedFontHandle.CreateTemplateFont 的字型載入呼叫已變更。");
        asset.Operand = 1999;
    }

    private static void ReplaceAssetLiteralBeforeCall(MethodDefinition method, Func<MethodReference, bool> callMatches, string context)
    {
        var call = method.Body.Instructions.SingleOrDefault(x => x.OpCode is var opcode && (opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj) && x.Operand is MethodReference reference && callMatches(reference) && x.Previous?.OpCode == OpCodes.Ldc_I4 && (int?)x.Previous.Operand == 2000)
            ?? throw new InvalidDataException($"找不到 {context} 的資產載入呼叫。");
        var asset = call.Previous;
        RuleEditor.Require(asset?.OpCode == OpCodes.Ldc_I4 && (int?)asset.Operand == 2000,
            $"{context} 的 NotoSansJpMedium 常數已變更。");
        asset!.Operand = 1999;
    }

    private static void InsertTraditionalChineseFontBranch(RuleEditor editor)
    {
        var method = editor.Method("Dalamud.Interface.ManagedFontAtlas.Internals.FontAtlasFactory/BuildToolkit", "AttachExtraGlyphsForDalamudLanguage", 1);
        var start = method.Body.Instructions.SingleOrDefault(x => x.OpCode == OpCodes.Ldstr && (string?)x.Operand == "tw")?.Previous?.Previous
            ?? throw new InvalidDataException("找不到字型的 tw 語言分支。");
        RuleEditor.Require(start.OpCode == OpCodes.Call && start.Next?.OpCode == OpCodes.Callvirt && start.Next.Next?.OpCode == OpCodes.Ldstr && (string?)start.Next.Next.Operand == "tw" && start.Next.Next.Next?.OpCode == OpCodes.Call && start.Next.Next.Next.Next?.OpCode.FlowControl == FlowControl.Cond_Branch,
            "字型 tw 語言分支結構已變更。");
        var il = method.Body.GetILProcessor();
        var branch = il.Create(OpCodes.Brfalse, start);
        il.InsertBefore(start, il.Create(OpCodes.Call, (MethodReference)start.Operand));
        il.InsertBefore(start, il.Create(OpCodes.Callvirt, (MethodReference)start.Next!.Operand));
        il.InsertBefore(start, il.Create(OpCodes.Ldstr, "tc"));
        il.InsertBefore(start, il.Create(OpCodes.Call, (MethodReference)start.Next.Next!.Next!.Operand));
        il.InsertBefore(start, branch);
        il.InsertBefore(start, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(start, il.Create(OpCodes.Ldloc, method.Body.Variables[0]));
        il.InsertBefore(start, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(start, il.Create(OpCodes.Call, editor.Helper("Dalamud.Tc.Rules.FontRules", "AddTraditionalChineseFonts", 3)));
        il.InsertBefore(start, il.Create(OpCodes.Ret));
    }

    private static void RewriteDataManagerWrappers(RuleEditor editor)
    {
        var manager = editor.Type("Dalamud.Data.DataManager");
        RewriteDataManagerWrapper(editor.Method(manager, "GetExcelSheet", 2), editor.Helper("Dalamud.Tc.Rules.LanguageRules", "ResolveLuminaLanguageNullable", 2));
        RewriteDataManagerWrapper(editor.Method(manager, "GetSubrowExcelSheet", 2), editor.Helper("Dalamud.Tc.Rules.LanguageRules", "ResolveLuminaLanguageNullable", 2));
    }

    private static void RewriteDataManagerWrapper(MethodDefinition method, MethodReference resolveLanguage)
    {
        var getExcel = method.Body.Instructions.SingleOrDefault(x => x.OpCode == OpCodes.Call && x.Operand is MethodReference reference && reference.Name == "get_Excel")?.Operand as MethodReference
            ?? throw new InvalidDataException($"找不到 {method.FullName} 的 Excel 讀取。\n");
        var getSheet = method.Body.Instructions.SingleOrDefault(x => x.OpCode == OpCodes.Callvirt && x.Operand is MethodReference reference && reference.Name is "GetSheet" or "GetSubrowSheet")?.Operand as MethodReference
            ?? throw new InvalidDataException($"找不到 {method.FullName} 的 sheet 讀取。\n");
        RuleEditor.Require(method.GenericParameters.Count == 1 && method.Parameters.Count == 2, $"{method.FullName} 的泛型簽章已變更。");
        method.Body.Variables.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.Instructions.Clear();
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, getExcel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, resolveLanguage);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, getSheet);
        il.Emit(OpCodes.Ret);
    }

    private static void RewriteNounLanguageConversions(RuleEditor editor)
    {
        var noun = editor.Type("Dalamud.Game.Text.Noun.NounProcessor");
        var dataManager = noun.Fields.SingleOrDefault(x => x.Name == "dataManager")
            ?? throw new InvalidDataException("找不到 NounProcessor.dataManager。\n");
        var resolve = editor.Helper("Dalamud.Tc.Rules.LanguageRules", "ResolveLuminaLanguageNullable", 2);
        foreach (var method in noun.Methods.Where(x => x.Name is "ResolveNounJa" or "ResolveNounEn" or "ResolveNounDe" or "ResolveNounFr"))
            ReplaceNounConversions(method, dataManager, resolve);
    }

    private static void ReplaceNounConversions(MethodDefinition method, FieldReference dataManager, MethodReference resolve)
    {
        var conversions = method.Body.Instructions.Where(x => x.OpCode == OpCodes.Call && x.Operand is MethodReference reference && reference.FullName == "Lumina.Data.Language Dalamud.Utility.ClientLanguageExtensions::ToLumina(Dalamud.Game.ClientLanguage)").ToArray();
        RuleEditor.Require(conversions.Length == 2, $"{method.FullName} 的 Lumina 語言轉換數量不是 目前預期的 2。\n");
        foreach (var conversion in conversions)
        {
            var languageField = conversion.Previous?.Operand as FieldReference;
            RuleEditor.Require(languageField?.Name == "Language", $"{method.FullName} 的 NounParams.Language 載入結構已變更。\n");
            var nullable = conversion.Next;
            RuleEditor.Require(nullable?.OpCode == OpCodes.Newobj && nullable.Operand is MethodReference constructor && constructor.DeclaringType.FullName == "System.Nullable`1<Lumina.Data.Language>",
                $"{method.FullName} 的 Nullable<Language> 建構結構已變更。\n");
            conversion.OpCode = OpCodes.Pop;
            conversion.Operand = null;
            nullable!.OpCode = OpCodes.Ldarg_0;
            nullable.Operand = null;
            var il = method.Body.GetILProcessor();
            il.InsertAfter(nullable, il.Create(OpCodes.Ldfld, dataManager));
            il.InsertAfter(nullable.Next!, il.Create(OpCodes.Ldarg_1));
            il.InsertAfter(nullable.Next!.Next!, il.Create(OpCodes.Ldfld, languageField!));
            il.InsertAfter(nullable.Next!.Next!.Next!, il.Create(OpCodes.Call, resolve));
        }
    }

    private static void RewriteSeStringCreatorLanguageConversion(RuleEditor editor)
    {
        var method = editor.Method("Dalamud.Interface.Internal.Windows.Data.Widgets.SeStringCreatorWidget", "DrawAddFromSheetPopup", 0);
        var conversion = method.Body.Instructions.SingleOrDefault(x => x.OpCode == OpCodes.Call && x.Operand is MethodReference reference && reference.FullName == "Lumina.Data.Language Dalamud.Utility.ClientLanguageExtensions::ToLumina(Dalamud.Game.ClientLanguage)")
            ?? throw new InvalidDataException("找不到 SeStringCreatorWidget 的 Lumina 語言轉換。");
        var nullable = conversion.Next;
        RuleEditor.Require(nullable?.OpCode == OpCodes.Newobj, "SeStringCreatorWidget 的 Nullable<Language> 結構已變更。");
        var start = conversion;
        while (start.Previous is not null && !(start.Previous.OpCode == OpCodes.Callvirt && start.Previous.Operand is MethodReference reference && reference.Name == "get_Excel"))
            start = start.Previous;
        var getExcel = start.Previous ?? throw new InvalidDataException("找不到 SeStringCreatorWidget 的 Excel 讀取。");
        var managerField = getExcel.Previous?.Operand as FieldReference;
        var closureLoad = getExcel.Previous?.Previous;
        var languageField = start.Next?.Operand as FieldReference;
        RuleEditor.Require(managerField?.Name == "dataManager" && closureLoad?.OpCode.Code is Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3 or Code.Ldloc or Code.Ldloc_S && languageField?.Name == "language",
            "SeStringCreatorWidget 的 dataManager／language 載入結構已變更。");

        var span = new List<Instruction>();
        for (var current = start; current is not null; current = current.Next)
        {
            span.Add(current);
            if (current == nullable)
                break;
        }
        RuleEditor.Require(span.Count == 11, "SeStringCreatorWidget 的語言轉換區塊大小已變更。");
        span[0].OpCode = closureLoad!.OpCode;
        span[0].Operand = closureLoad.Operand;
        span[1].OpCode = OpCodes.Ldfld;
        span[1].Operand = managerField;
        span[2].OpCode = OpCodes.Ldarg_0;
        span[2].Operand = null;
        span[3].OpCode = OpCodes.Ldfld;
        span[3].Operand = languageField;
        span[4].OpCode = OpCodes.Call;
        span[4].Operand = editor.Helper("Dalamud.Tc.Rules.LanguageRules", "ResolveLuminaLanguageNullable", 2);
        foreach (var instruction in span.Skip(5))
        {
            instruction.OpCode = OpCodes.Nop;
            instruction.Operand = null;
        }
    }

}
#else
using System;
using System.Globalization;
using Dalamud;
using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Utility;
using Lumina.Data;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration.Internal;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.ManagedFontAtlas.Internals;
using Dalamud.Interface.Utility;
using Dalamud.IoC;
using Dalamud.Storage.Assets;

namespace Dalamud.Tc.Rules
{
    /// <summary>
    /// 目前的 TC 語言規則。這是手寫規則來源，不從 TC 組件複製任何 IL。
    /// </summary>
    public static class LanguageRules
    {
        public static Language ToLumina(ClientLanguage language) => language switch
        {
            ClientLanguage.Japanese => Language.Japanese,
            ClientLanguage.English => Language.English,
            ClientLanguage.German => Language.German,
            ClientLanguage.French => Language.French,
            // TC 固定的 Lumina fork 將 TraditionalChinese (8) 對應為 tc；不可改用國際服的 ChineseTraditional (6)。
            ClientLanguage.SimplifiedChinese or ClientLanguage.TraditionalChinese => Language.TraditionalChinese,
            _ => Language.TraditionalChinese,
        };

        public static string ToCode(ClientLanguage language) => language switch
        {
            ClientLanguage.Japanese => "ja",
            ClientLanguage.English => "en",
            ClientLanguage.German => "de",
            ClientLanguage.French => "fr",
            ClientLanguage.SimplifiedChinese or ClientLanguage.TraditionalChinese => "tc",
            _ => "tc",
        };

        public static ClientLanguage ToClientLanguage(string language) => language switch
        {
            "ja" => ClientLanguage.Japanese,
            "en" => ClientLanguage.English,
            "de" => ClientLanguage.German,
            "fr" => ClientLanguage.French,
            "chs" or "tc" => ClientLanguage.TraditionalChinese,
            _ => ClientLanguage.TraditionalChinese,
        };

        public static CultureInfo GetCultureInfo(string language) =>
            CultureInfo.GetCultureInfo(language switch
            {
                "tw" or "tc" => "zh-hant",
                "zh" => "zh-hans",
                _ => language,
            });

        public static string GetPluginLanguageCode(string language) =>
            language is "tw" or "tc" ? GetCultureInfo(language).Name : language;

        public static ClientLanguage TraditionalChinese() => ClientLanguage.TraditionalChinese;

        public static Language? ResolveLuminaLanguageNullable(DataManager manager, ClientLanguage? requested) =>
            manager.Language is ClientLanguage.TraditionalChinese or ClientLanguage.SimplifiedChinese
                ? manager.Language.ToLumina()
                : requested?.ToLumina();

        public static Language ResolveLuminaLanguageValue(DataManager manager, ClientLanguage requested) =>
            ResolveLuminaLanguageNullable(manager, (ClientLanguage?)requested) ?? manager.Language.ToLumina();
    }
}

namespace Dalamud.Tc.Rules
{
    /// <summary>
    /// TC 字型規則。執行時由 BuildToolkit 的巢狀 helper 呼叫，因此能維持原有封裝邊界。
    /// </summary>
    public static class FontRules
    {
        public static void AddTraditionalChineseFonts(
            FontAtlasFactory.BuildToolkit toolkit,
            ImFontPtr targetFont,
            in SafeFontConfig fontConfig)
        {
            var assetManager = Service<DalamudAssetManager>.GetNullable();
            if (assetManager?.IsStreamImmediatelyAvailable(DalamudAsset.NotoSansTcRegular) is true)
            {
                toolkit.AddDalamudAssetFont(
                    DalamudAsset.NotoSansTcRegular,
                    fontConfig with
                    {
                        MergeFont = targetFont,
                        GlyphRanges = default(FluentGlyphRangeBuilder).WithLanguage("zh-hant").BuildExact(),
                    });
            }
            else
            {
                toolkit.AttachWindowsDefaultFont(CultureInfo.GetCultureInfo("zh-hant"), fontConfig with
                {
                    GlyphRanges = default(FluentGlyphRangeBuilder).WithLanguage("zh-hant").BuildExact(),
                });
            }

            if (assetManager?.IsStreamImmediatelyAvailable(DalamudAsset.NotoSansScRegular) is true)
            {
                toolkit.AddDalamudAssetFont(
                    DalamudAsset.NotoSansScRegular,
                    fontConfig with
                    {
                        MergeFont = targetFont,
                        GlyphRanges = default(FluentGlyphRangeBuilder).WithLanguage("zh-hans").BuildExact(),
                    });
            }
        }
    }
}

namespace Dalamud.Interface
{
    /// <summary>
    /// TC／SC 共用的基本 CJK 字型範圍。
    /// </summary>
    public static class GlyphRangesChinese
    {
        public static ushort[] GlyphRanges =>
        [
            0x0020, 0x00FF,
            0x2000, 0x206F,
            0x3000, 0x30FF,
            0x31F0, 0x31FF,
            0xFF00, 0xFFEF,
            0x4E00, 0x9FAF,
            0,
        ];
    }
}
#endif
