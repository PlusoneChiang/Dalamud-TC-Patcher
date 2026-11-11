// PATCHER：套件產製時定位與修改 DLL；另一部分是編譯後寫入 DLL 的實際執行邏輯。
#if PATCHER
using Dalamud.Tc.Patcher.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Dalamud.Tc.Patcher.Patches;

/// <summary>目前的多主外掛庫與安裝器補丁。</summary>
internal static class RepositoryPatches
{
    public static void Prepare(RuleEditor editor)
    {
        // 所有狀態由注入 helper 保存，避免擴大 PluginManager 的公開 API。
    }

    public static void Apply(RuleEditor editor)
    {
        PatchRepositoryConstructor(editor);

        var localPlugin = editor.Type("Dalamud.Plugin.Internal.Types.LocalPlugin");
        editor.Forward(editor.Method(localPlugin, "get_IsDecommissioned", 0), editor.Helper("Dalamud.Tc.Rules.LocalPluginRules", "IsDecommissioned", 1));
        editor.Forward(editor.Method(localPlugin, "GetSourceRepository", 0), editor.Helper("Dalamud.Tc.Rules.LocalPluginRules", "GetSourceRepository", 1));

        var manager = editor.Type("Dalamud.Plugin.Internal.PluginManager");
        editor.Forward(editor.Method(manager, "SetPluginReposFromConfigAsync", 1), editor.Helper("Dalamud.Tc.Rules.PluginManagerRules", "SetPluginReposFromConfigAsync", 2));
        editor.Forward(editor.Method(manager, "ReloadPluginMastersAsync", 1), editor.Helper("Dalamud.Tc.Rules.PluginManagerRules", "ReloadPluginMastersAsync", 2));
        ReplaceMainRepoCreation(editor, manager);

        var installer = editor.Type("Dalamud.Interface.Internal.Windows.PluginInstaller.PluginInstallerWindow");
        editor.Forward(editor.Method(installer, "DrawAvailablePluginList", 0), editor.Helper("Dalamud.Tc.Rules.PluginInstallerWindowRules", "DrawAvailablePluginList", 1));

        ApplyPluginInterface(editor);
        // Cross-main-repository update 判斷僅替換 IsMainRepoUrl 的呼叫點；保留原本的更新 UI 流程。
        ReplaceMainRepoComparison(editor, installer);
        ReplaceRepositorySourceDisplay(editor, installer);
        InsertRefreshButton(editor, installer);
    }

    private static void ReplaceMainRepoCreation(RuleEditor editor, TypeDefinition manager)
    {
        var constructor = editor.Method(manager, ".ctor", 2);
        var helper = editor.Helper("Dalamud.Tc.Rules.PluginManagerRules", "InitializeAndGetPrimary", 2);
        var createMainRepo = constructor.Body.Instructions.Single(instruction =>
            instruction.Operand is MethodReference method &&
            method.Name == "CreateMainRepo" &&
            method.DeclaringType.FullName == "Dalamud.Plugin.Internal.Types.PluginRepository");
        var clientLoad = createMainRepo.Previous ?? throw new InvalidDataException("找不到建立主庫所需的 HTTP 用戶端。\n");
        RuleEditor.Require(clientLoad.OpCode == OpCodes.Ldfld && clientLoad.Operand is FieldReference field && field.Name == "happyHttpClient", "建立主庫的 目前 欄位模式不符。");

        var processor = constructor.Body.GetILProcessor();
        // 原本 stack 為 assignment-this, client；在載入 client 前壓入 self，讓 helper 取得 self, client。
        processor.InsertBefore(clientLoad, processor.Create(OpCodes.Ldarg_0));
        createMainRepo.OpCode = OpCodes.Call;
        createMainRepo.Operand = helper;
    }

    private static void PatchRepositoryConstructor(RuleEditor editor)
    {
        var repository = editor.Type("Dalamud.Plugin.Internal.Types.PluginRepository");
        var constructor = editor.Method(repository, ".ctor", 3);
        var backingField = repository.Fields.Single(field => field.Name == "<IsThirdParty>k__BackingField");
        var assignment = constructor.Body.Instructions.Single(instruction =>
            instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field && field.FullName == backingField.FullName);
        var comparison = assignment.Previous ?? throw new InvalidDataException("外掛庫 IsThirdParty 指派缺少比較運算。\n");
        RuleEditor.Require(comparison.Operand is MethodReference method && method.Name == "op_Inequality", "外掛庫 IsThirdParty 的 目前 比較模式不符。");
        var urlLiteral = comparison.Previous ?? throw new InvalidDataException("外掛庫 IsThirdParty 指派缺少主庫 URL。\n");
        RuleEditor.Require(urlLiteral.OpCode == OpCodes.Ldstr, "外掛庫 IsThirdParty 的 目前 主庫 URL 模式不符。");

        // Stack 原本為 this,url,"official"，改成 this,IsMainRepoUrl(url),false，再以 ceq 取得 IsThirdParty。
        urlLiteral.OpCode = OpCodes.Call;
        urlLiteral.Operand = editor.Helper("Dalamud.Tc.Rules.PluginRepositoryRules", "IsMainRepoUrl", 1);
        comparison.OpCode = OpCodes.Ldc_I4_0;
        comparison.Operand = null;
        constructor.Body.GetILProcessor().InsertBefore(assignment, constructor.Body.GetILProcessor().Create(OpCodes.Ceq));
    }

    private static void ReplaceMainRepoComparison(RuleEditor editor, TypeDefinition installer)
    {
        var method = editor.Method(installer, "DrawInstalledPlugin", 5);
        var helper = editor.Helper("Dalamud.Tc.Rules.PluginInstallerWindowRules", "IsMainRepoCrossUpdate", 1);
        method.Body.SimplifyMacros();
        var instructions = method.Body.Instructions;
        var officialUrl = instructions.SingleOrDefault(instruction =>
            instruction.OpCode == OpCodes.Ldstr &&
            string.Equals(instruction.Operand as string, "https://kamori.goats.dev/Plugin/PluginMaster", StringComparison.Ordinal))
            ?? throw new InvalidDataException("找不到 目前的原始主庫 URL 判斷。\n");
        var start = instructions
            .Take(instructions.IndexOf(officialUrl))
            .LastOrDefault(instruction => IsLoadAvailableUpdate(instruction) &&
                instruction.Next?.OpCode == OpCodes.Ldnull && instruction.Next.Next?.Operand is MethodReference comparison && comparison.Name == "op_Inequality")
            ?? throw new InvalidDataException("找不到 目前的主庫更新判斷起點。\n");
        var result = instructions.Skip(instructions.IndexOf(start)).FirstOrDefault(x => x.OpCode == OpCodes.Stloc && x.Operand is VariableDefinition)
            ?? throw new InvalidDataException("找不到 目前的主庫更新判斷結果。\n");

        // 以完整 update 物件交給規則判斷，取代原本只比較單一官方 URL 的區塊。
        var helperCall = start.Next!;
        var comparison = helperCall.Next!;
        helperCall.OpCode = OpCodes.Call;
        helperCall.Operand = helper;
        comparison.OpCode = OpCodes.Stloc;
        comparison.Operand = result.Operand;
        for (var i = instructions.IndexOf(comparison.Next!); i <= instructions.IndexOf(result); i++)
        {
            instructions[i].OpCode = OpCodes.Nop;
            instructions[i].Operand = null;
        }
    }

    private static void InsertRefreshButton(RuleEditor editor, TypeDefinition installer)
    {
        var method = editor.Method(installer, "DrawHeader", 0);
        var instructions = method.Body.Instructions;
        var setCursor = instructions.SingleOrDefault(x => x.OpCode == OpCodes.Call && x.Operand is MethodReference call && call.Name == "SetCursorPosX")
            ?? throw new InvalidDataException("找不到外掛安裝器標頭的搜尋列位置。\n");
        // 目前的搜尋欄寬度是設定搜尋輸入前、且位於 SetCursorPosX 之後的第一個 SetNextItemWidth。
        // 不依賴 Ldloc_2／Ldloc 的巨集形式，避免 Cecil 在注入後改寫指令型式。
        var searchWidth = instructions
            .Skip(instructions.IndexOf(setCursor) + 1)
            .FirstOrDefault(x => x.OpCode == OpCodes.Call && x.Operand is MethodReference call && call.Name == "SetNextItemWidth")
            ?? throw new InvalidDataException("找不到外掛安裝器標頭的搜尋欄位寬度。\n");
        var clearWidth = method.Body.Variables.Single(variable => variable.Index == 3);
        var downShift = method.Body.Variables.Single(variable => variable.Index == 12);

        var il = method.Body.GetILProcessor();
        var shiftWidth = il.Create(OpCodes.Ldloc, clearWidth);
        il.InsertAfter(setCursor, shiftWidth);
        il.InsertAfter(shiftWidth, il.Create(OpCodes.Call, editor.Helper("Dalamud.Tc.Rules.PluginInstallerWindowRules", "ShiftHeaderForRefresh", 1)));

        var refreshAnchor = searchWidth.Previous!;
        var refreshSelf = il.Create(OpCodes.Ldarg_0);
        il.InsertBefore(refreshAnchor, refreshSelf);
        var refreshWidth = il.Create(OpCodes.Ldloc, clearWidth);
        il.InsertAfter(refreshSelf, refreshWidth);
        var refreshDownShift = il.Create(OpCodes.Ldloc, downShift);
        il.InsertAfter(refreshWidth, refreshDownShift);
        il.InsertAfter(refreshDownShift, il.Create(OpCodes.Call, editor.Helper("Dalamud.Tc.Rules.PluginInstallerWindowRules", "DrawRepositoryRefreshButton", 3)));
    }

    private static void ReplaceRepositorySourceDisplay(RuleEditor editor, TypeDefinition installer)
    {
        var available = editor.Method(installer, "DrawAvailablePlugin", 2);
        var availableInstructions = available.Body.Instructions;
        var indent = availableInstructions.Single(instruction =>
            instruction.Operand is MethodReference method && method.DeclaringType.FullName == "Dalamud.Bindings.ImGui.ImGui" && method.Name == "Indent");
        var sourceCheck = availableInstructions
            .Skip(availableInstructions.IndexOf(indent))
            .First(instruction => instruction.Operand is MethodReference method && method.Name == "get_IsThirdParty");
        sourceCheck.OpCode = OpCodes.Call;
        sourceCheck.Operand = editor.Helper("Dalamud.Tc.Rules.PluginInstallerWindowRules", "HasRepository", 1);
        var availableText = availableInstructions.Single(instruction =>
            instruction.Operand is MethodReference method && method.DeclaringType.Name == "Locs" && method.Name == "PluginBody_Plugin3rdPartyRepo");
        availableText.OpCode = OpCodes.Call;
        availableText.Operand = editor.Helper("Dalamud.Tc.Rules.PluginInstallerWindowRules", "FormatRepositoryText", 1);

        var installed = editor.Method(installer, "DrawInstalledPlugin", 5);
        var installedInstructions = installed.Body.Instructions;
        var installedText = installedInstructions.Single(instruction =>
            instruction.Operand is MethodReference method && method.DeclaringType.Name == "Locs" && method.Name == "PluginBody_Plugin3rdPartyRepo");
        var oldCondition = installedInstructions
            .Take(installedInstructions.IndexOf(installedText))
            .LastOrDefault(instruction => instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
            ?? throw new InvalidDataException("找不到已安裝外掛來源的 目前 條件分支。\n");
        RuleEditor.Require(oldCondition.Previous is not null && oldCondition.Operand is Instruction, "已安裝外掛來源的 目前 條件模式不符。");

        var il = installed.Body.GetILProcessor();
        var self = oldCondition.Previous!;
        self.OpCode = OpCodes.Ldarg_0;
        self.Operand = null;
        var displayClass = il.Create(OpCodes.Ldloc_0);
        il.InsertAfter(self, displayClass);
        var displayClassType = installed.Body.Variables[0].VariableType.Resolve()
            ?? throw new InvalidDataException("找不到已安裝外掛繪製閉包型別。\n");
        var pluginField = displayClassType.Fields.Single(field => field.Name == "plugin");
        var loadPlugin = il.Create(OpCodes.Ldfld, pluginField);
        il.InsertAfter(displayClass, loadPlugin);
        il.InsertAfter(loadPlugin, il.Create(OpCodes.Call, editor.Helper("Dalamud.Tc.Rules.PluginInstallerWindowRules", "DrawInstalledRepositorySource", 2)));
    }

    private static void ApplyPluginInterface(RuleEditor editor)
    {
        var type = editor.Type("Dalamud.Plugin.DalamudPluginInterface");
        var constructor = editor.Method(type, ".ctor", 2);
        var sourceRepository = type.Fields.Single(x => x.Name == "<SourceRepository>k__BackingField");
        var existingAssignment = constructor.Body.Instructions.Single(x => x.OpCode == OpCodes.Stfld && x.Operand is FieldReference field && field.FullName == sourceRepository.FullName);
        var sourceHelper = editor.Helper("Dalamud.Tc.Rules.DalamudPluginInterfaceRules", "GetSourceRepository", 2);
        var il = constructor.Body.GetILProcessor();
        il.InsertAfter(existingAssignment, il.Create(OpCodes.Ldarg_0));
        il.InsertAfter(existingAssignment.Next, il.Create(OpCodes.Ldarg_1));
        il.InsertAfter(existingAssignment.Next.Next, il.Create(OpCodes.Ldarg_0));
        il.InsertAfter(existingAssignment.Next.Next.Next, il.Create(OpCodes.Call, type.Methods.Single(x => x.Name == "get_IsDev")));
        il.InsertAfter(existingAssignment.Next.Next.Next.Next, il.Create(OpCodes.Call, sourceHelper));
        il.InsertAfter(existingAssignment.Next.Next.Next.Next.Next, il.Create(OpCodes.Stfld, sourceRepository));

        var languageHelper = editor.Helper("Dalamud.Tc.Rules.LanguageRules", "GetPluginLanguageCode", 1);
        var overrideSetter = constructor.Body.Instructions.Single(x => x.OpCode == OpCodes.Call && x.Operand is MethodReference call && call.Name == "set_UiLanguage" && x.Previous?.Operand is MethodReference previous && previous.Name == "get_LanguageOverride");
        il.InsertBefore(overrideSetter, il.Create(OpCodes.Call, languageHelper));

        var changed = editor.Method(type, "OnLocalizationChanged", 1);
        var changedIl = changed.Body.GetILProcessor();
        var first = changed.Body.Instructions.First();
        changedIl.InsertBefore(first, changedIl.Create(OpCodes.Ldarg_1));
        changedIl.InsertBefore(first, changedIl.Create(OpCodes.Call, languageHelper));
        changedIl.InsertBefore(first, changedIl.Create(OpCodes.Starg_S, changed.Parameters[0]));
    }

    private static bool IsLoadAvailableUpdate(Instruction instruction) =>
        instruction.OpCode is var code &&
        (code == OpCodes.Ldarg || code == OpCodes.Ldarg_S || code == OpCodes.Ldarg_0 || code == OpCodes.Ldarg_1 || code == OpCodes.Ldarg_2 || code == OpCodes.Ldarg_3) &&
        instruction.Operand is ParameterDefinition parameter && parameter.Name == "availablePluginUpdate";
}
#else
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Dalamud.Networking.Http;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Internal.Types.Manifest;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration.Internal;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Internal.Windows.PluginInstaller;
using Dalamud.Utility;

namespace Dalamud.Tc.Rules
{
    /// <summary>多主外掛庫的行為；注入至 <see cref="PluginRepository"/>。</summary>
    public static class PluginRepositoryRules
    {
        public const string DefaultMainRepoUrl = "https://kamori.goats.dev/Plugin/PluginMaster";

        private static readonly IReadOnlyList<string> EffectiveMainRepoUrls = ReadMainRepoUrls();

        public static IReadOnlyList<string> GetMainRepoUrls() => EffectiveMainRepoUrls;

        private static IReadOnlyList<string> ReadMainRepoUrls()
        {
            var value = Environment.GetEnvironmentVariable("DALAMUD_MAIN_REPO_URL");
            var urls = string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return urls.Length == 0 ? new[] { DefaultMainRepoUrl } : urls;
        }

        public static bool IsMainRepoUrl(string? url) =>
            !string.IsNullOrEmpty(url) && GetMainRepoUrls().Contains(url, StringComparer.OrdinalIgnoreCase);

        public static List<PluginRepository> CreateMainRepos(HappyHttpClient client) =>
            GetMainRepoUrls().Select(url => new PluginRepository(client, url, true)).ToList();
    }

    /// <summary>多主外掛庫的 PluginManager 狀態；注入至 <see cref="PluginManager"/>。</summary>
    public static class PluginManagerRules
    {
        private static readonly ConcurrentDictionary<PluginManager, IReadOnlyList<PluginRepository>> MainRepos = new();

        /// <summary>
        /// 在原本指派 MainRepo 的位置建立所有主庫，並回傳第一個主庫給既有唯讀屬性。
        /// 這必須在啟動工作註冊前完成，避免載入工作讀到國際服預設主庫。
        /// </summary>
        public static PluginRepository InitializeAndGetPrimary(PluginManager self, HappyHttpClient client)
        {
            var repos = PluginRepositoryRules.CreateMainRepos(client);
            MainRepos[self] = repos;
            return repos[0];
        }

        public static IReadOnlyList<PluginRepository> GetMainRepos(PluginManager self) =>
            MainRepos.TryGetValue(self, out var repos) ? repos : new[] { self.MainRepo };

        public static async System.Threading.Tasks.Task SetPluginReposFromConfigAsync(PluginManager self, bool notify)
        {
            var repos = new List<PluginRepository>(GetMainRepos(self));
            repos.AddRange(self.configuration.ThirdRepoList
                .Where(repo => repo.IsEnabled)
                .Select(repo => new PluginRepository(self.happyHttpClient, repo.Url, repo.IsEnabled)));
            self.Repos = repos;
            await self.ReloadPluginMastersAsync(notify);
        }

        /// <summary>依序先載入全部主庫，再平行載入第三方庫，維持主庫優先的篩選語意。</summary>
        public static async System.Threading.Tasks.Task ReloadPluginMastersAsync(PluginManager self, bool notify)
        {
            PluginManager.Log.Information("Now reloading all PluginMasters...");
            self.ReposReady = false;

            try
            {
                var mainRepos = GetMainRepos(self);
                System.Diagnostics.Debug.Assert(mainRepos.All(repo => !repo.IsThirdParty), "Main repositories should not be third party repositories");
                await System.Threading.Tasks.Task.WhenAll(mainRepos.Select(repo => repo.ReloadPluginMasterAsync()));
                await System.Threading.Tasks.Task.WhenAll(self.Repos.Skip(mainRepos.Count).Select(repo => repo.ReloadPluginMasterAsync()));

                PluginManager.Log.Information("PluginMasters reloaded, now refiltering...");
                self.RefilterPluginMasters(notify);
            }
            catch (Exception ex)
            {
                PluginManager.Log.Error(ex, "Could not reload plugin repositories");
            }
            finally
            {
                self.ReposReady = true;
            }
        }
    }

    /// <summary>多主庫下的已安裝外掛判斷；注入至 <see cref="LocalPlugin"/>。</summary>
    public static class LocalPluginRules
    {
        public static bool IsDecommissioned(LocalPlugin self)
        {
            if (self.IsDev)
                return false;

            var repos = global::Dalamud.Service<PluginManager>.Get().Repos;
            if (self.manifest.IsThirdParty)
            {
                var source = GetSourceRepository(self);
                return source?.State == PluginRepositoryState.Success &&
                       source.PluginMaster?.All(x => x.InternalName != self.manifest.InternalName) == true;
            }

            var main = repos.Where(x => !x.IsThirdParty).ToList();
            return main.Any() &&
                   main.All(x => x.State == PluginRepositoryState.Success) &&
                   !main.Any(x => x.PluginMaster?.Any(p => p.InternalName == self.manifest.InternalName) == true);
        }

        public static PluginRepository? GetSourceRepository(LocalPlugin self)
        {
            if (self.IsDev)
                return null;

            var repos = global::Dalamud.Service<PluginManager>.Get().Repos;
            if (!string.IsNullOrEmpty(self.manifest.InstalledFromUrl))
            {
                var exact = repos.FirstOrDefault(x => string.Equals(x.PluginMasterUrl, self.manifest.InstalledFromUrl, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                    return exact;
            }

            var matched = repos.FirstOrDefault(x => x.PluginMaster?.Any(p =>
                string.Equals(p.InternalName, self.manifest.InternalName, StringComparison.OrdinalIgnoreCase)) == true);
            return matched ?? (!self.manifest.IsThirdParty ? repos.FirstOrDefault(x => !x.IsThirdParty) : null);
        }
    }
}

namespace Dalamud.Tc.Rules
{
    /// <summary>外掛公開語言與來源資訊；注入至 <see cref="DalamudPluginInterface"/>。</summary>
    public static class DalamudPluginInterfaceRules
    {
        public static string GetSourceRepository(LocalPlugin plugin, bool isDev)
        {
            if (isDev)
                return SpecialPluginSource.DevPlugin;

            var source = LocalPluginRules.GetSourceRepository(plugin);
            return !string.IsNullOrEmpty(source?.PluginMasterUrl) ? source.PluginMasterUrl : plugin.Manifest.InstalledFromUrl;
        }

    }
}

namespace Dalamud.Tc.Rules
{
    /// <summary>外掛安裝器的多主庫分類與來源文字；注入至 PluginInstallerWindow。</summary>
    public static class PluginInstallerWindowRules
    {
        public static IEnumerable<IGrouping<string, PluginInstallerWindow.PluginInstallerAvailablePluginProxy>> GroupByRepository(
            IEnumerable<PluginInstallerWindow.PluginInstallerAvailablePluginProxy> proxies) =>
            proxies.GroupBy(proxy =>
            {
                if (proxy.LocalPlugin is { IsDev: false } local && !string.IsNullOrEmpty(local.Manifest.InstalledFromUrl))
                    return local.Manifest.InstalledFromUrl;
                return proxy.RemoteManifest?.SourceRepo?.PluginMasterUrl ?? "Official / Local";
            }).OrderBy(group => group.Key);

        public static bool IsMainRepoCrossUpdate(AvailablePluginUpdate? update) =>
            update is not null &&
            update.UpdateManifest.RepoUrl != update.InstalledPlugin.Manifest.RepoUrl &&
            PluginRepositoryRules.IsMainRepoUrl(update.UpdateManifest.RepoUrl);

        public static bool HasRepository(PluginRepository? repository) => repository is not null;

        public static string FormatRepositoryText(string repositoryUrl) =>
            PluginRepositoryRules.IsMainRepoUrl(repositoryUrl)
                ? Loc.Localize("InstallerPluginMainRepo", "From main repository {0}").Format(repositoryUrl)
                : PluginInstallerWindow.Locs.PluginBody_Plugin3rdPartyRepo(repositoryUrl);

        /// <summary>
        /// 繪製已安裝外掛的來源，並回傳 false 以略過舊有僅第三方庫的顯示區塊。
        /// </summary>
        public static bool DrawInstalledRepositorySource(PluginInstallerWindow self, LocalPlugin plugin)
        {
            var repoUrl = !string.IsNullOrEmpty(plugin.Manifest.InstalledFromUrl)
                ? plugin.Manifest.InstalledFromUrl
                : self.pluginListAvailable.FirstOrDefault(item => item.InternalName == plugin.InternalName)?.SourceRepo?.PluginMasterUrl;
            if (!string.IsNullOrEmpty(repoUrl))
                ImGui.TextColored(ImGuiColors.DalamudGrey3, FormatRepositoryText(repoUrl));

            return false;
        }

        public static void DrawAvailablePluginList(PluginInstallerWindow self)
        {
            var groups = GroupByRepository(self.GatherProxies()).ToList();
            var index = 0;
            foreach (var group in groups)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.22f, 0.24f, 0.27f, 0.90f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.30f, 0.32f, 0.36f, 1.00f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0.18f, 0.20f, 0.22f, 1.00f));
                if (ImGui.Button($"{group.Key}###repo_{index}", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X, 26f * ImGuiHelpers.GlobalScale)))
                    ImGui.SetClipboardText(group.Key);
                ImGui.PopStyleColor(3);

                foreach (var proxy in group)
                {
                    IPluginManifest manifest = proxy.LocalPlugin is not null ? proxy.LocalPlugin.Manifest : proxy.RemoteManifest
                        ?? throw new InvalidOperationException("找不到可顯示外掛的 manifest。");
                    ImGui.PushID($"{manifest.InternalName}{manifest.AssemblyVersion}");
                    if (proxy.LocalPlugin is not null)
                    {
                        var update = self.pluginListUpdatable.FirstOrDefault(value => value.InstalledPlugin == proxy.LocalPlugin);
                        self.DrawInstalledPlugin(proxy.LocalPlugin, index++, proxy.RemoteManifest, update);
                    }
                    else if (proxy.RemoteManifest is not null)
                    {
                        self.DrawAvailablePlugin(proxy.RemoteManifest, index++);
                    }

                    ImGui.PopID();
                }
            }

            if (index == 0 && self.categoryManager.CurrentCategoryKind == PluginCategoryManager.CategoryKind.Hidden)
                self.categoryManager.CurrentCategoryKind = PluginCategoryManager.CategoryKind.All;

            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                var hasSearch = !self.searchText.IsNullOrEmpty();
                if (index == 0 && !hasSearch)
                    PluginInstallerWindow.DrawMutedBodyText(PluginInstallerWindow.Locs.TabBody_NoPluginsAvailable, 60, 20);
                else if (index == 0)
                    PluginInstallerWindow.DrawMutedBodyText(PluginInstallerWindow.Locs.TabBody_SearchNoMatching, 60, 20);
                else if (hasSearch)
                    PluginInstallerWindow.DrawMutedBodyText(PluginInstallerWindow.Locs.TabBody_NoMoreResultsFor(self.searchText), 20, 20);
            }
        }

        public static void DrawRepositoryRefreshButton(PluginInstallerWindow self, float width, float downShift)
        {
            ImGui.SetNextItemWidth(width);
            if (ImGuiComponents.IconButton(global::Dalamud.Interface.FontAwesomeIcon.Sync))
            {
                var manager = global::Dalamud.Service<PluginManager>.GetNullable();
                if (manager is not null)
                    _ = System.Threading.Tasks.Task.Run(() => manager.SetPluginReposFromConfigAsync(true));
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("重新整理套件庫 (Refresh Plugin Repositories)");
            ImGui.SameLine();
            ImGui.SetCursorPosY(downShift);
        }

        public static void ShiftHeaderForRefresh(float width) =>
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() - width - ImGui.GetStyle().ItemSpacing.X);
    }
}
#endif
