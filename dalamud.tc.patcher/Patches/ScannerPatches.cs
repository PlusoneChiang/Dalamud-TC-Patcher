// PATCHER：套件產製時定位與修改 DLL；另一部分是編譯後寫入 DLL 的實際執行邏輯。
#if PATCHER
using Dalamud.Tc.Patcher.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Dalamud.Tc.Patcher.Patches;

/// <summary>套用 目前的 SigScanner 明確來源規則。</summary>
internal static class ScannerPatches
{
    private const string ScannerType = "Dalamud.Game.SigScanner";

    public static void Prepare(RuleEditor editor)
    {
        var scanner = editor.Type(ScannerType);
        var original = editor.Method(scanner, "SetupCopiedSegments", 0);
        var copy = new MethodDefinition("__TcOriginalSetupCopiedSegments", original.Attributes, original.ReturnType);
        copy.Body = new MethodBody(copy) { InitLocals = original.Body.InitLocals, MaxStackSize = original.Body.MaxStackSize };
        var variables = original.Body.Variables.ToDictionary(v => v, v => new VariableDefinition(v.VariableType));
        foreach (var variable in variables.Values) copy.Body.Variables.Add(variable);
        var instructions = original.Body.Instructions.ToDictionary(i => i, i => Instruction.Create(OpCodes.Nop));
        foreach (var instruction in original.Body.Instructions)
        {
            var clone = instructions[instruction];
            clone.OpCode = instruction.OpCode;
            clone.Operand = instruction.Operand switch
            {
                Instruction branch => instructions[branch],
                Instruction[] branches => branches.Select(b => instructions[b]).ToArray(),
                VariableDefinition variable => variables[variable],
                _ => instruction.Operand,
            };
            copy.Body.Instructions.Add(clone);
        }
        RuleEditor.Require(!original.HasGenericParameters && original.Parameters.Count == 0 && original.Body.ExceptionHandlers.Count == 0, "SetupCopiedSegments 原方法結構已改變");
        scanner.Methods.Add(copy);
    }

    public static void Apply(RuleEditor editor)
    {
        var scanner = editor.Type(ScannerType);
        editor.Forward(editor.Method(scanner, "GetStaticAddressFromSig", 2), editor.Helper("Dalamud.Tc.Rules.ScannerRules", "GetStaticAddressFromSig", 3));
        editor.Forward(editor.Method(scanner, "ScanText", 1), editor.Helper("Dalamud.Tc.Rules.ScannerRules", "ScanText", 2));
        editor.Forward(editor.Method(scanner, "TryScanText", 2), editor.Helper("Dalamud.Tc.Rules.ScannerRules", "TryScanText", 3));
        editor.Forward(editor.Method(scanner, "SetupCopiedSegments", 0), editor.Helper("Dalamud.Tc.Rules.ScannerRules", "SetupCopiedSegments", 1));
    }
}
#else
using System;
using System.Collections.Generic;
using Serilog;
using System.IO;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Iced.Intel;

namespace Dalamud.Tc.Rules
{
    /// <summary>
    /// TC client 的 signature 容錯規則。
    /// 此檔是 patcher 的可審查來源；不從 TC 發布 DLL 取用任何 IL。
    /// </summary>
    public static unsafe class ScannerRules
    {
        public static IntPtr GetStaticAddressFromSig(SigScanner self, string signature, int offset)
        {
            try
            {
                var targetAddress = self.ScanText(signature);
                if (targetAddress == IntPtr.Zero)
                {
                    Log.Warning("[SigScanner] 找不到 GetStaticAddressFromSig 的 signature：{Signature}", signature);
                    return IntPtr.Zero;
                }

                var instructionAddress = (byte*)targetAddress + offset;
                var reader = new SigScanner.UnsafeCodeReader(instructionAddress, signature.Length + 8);
                var decoder = Decoder.Create(64, reader, (ulong)instructionAddress, DecoderOptions.AMD);
                while (reader.CanReadByte)
                {
                    var instruction = decoder.Decode();
                    if (!instruction.IsInvalid && (instruction.Op0Kind is OpKind.Memory || instruction.Op1Kind is OpKind.Memory))
                        return (IntPtr)instruction.MemoryDisplacement64;
                }
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "[SigScanner] 無法解析靜態位址：{Signature}", signature);
                return IntPtr.Zero;
            }

            Log.Warning("[SigScanner] signature 未包含可用的靜態位址：{Signature}", signature);
            return IntPtr.Zero;
        }

        public static IntPtr ScanText(SigScanner self, string signature)
        {
            if (self.textCache != null && self.textCache.TryGetValue(signature, out var address))
                return new IntPtr(address + self.Module.BaseAddress.ToInt64());

            try
            {
                var result = SigScanner.Scan(self.TextSectionBase, self.TextSectionSize, signature);
                if (self.IsCopy)
                    result = new IntPtr(result.ToInt64() - self.moduleCopyOffset);

                var firstByte = Marshal.ReadByte(result);
                if (firstByte is 0xE8 or 0xE9)
                {
                    result = SigScanner.ReadJmpCallSig(result);
                    var offset = result - self.Module.BaseAddress;
                    if (offset < 0 || offset >= self.TextSectionSize)
                    {
                        Log.Warning("[SigScanner] signature {Signature} 解析至 .text 範圍外：0x{Offset:X}", signature, offset);
                        return IntPtr.Zero;
                    }
                }

                if (self.textCache != null && result.ToInt64() >= self.Module.BaseAddress.ToInt64())
                    self.textCache[signature] = result.ToInt64() - self.Module.BaseAddress.ToInt64();

                return result;
            }
            catch (KeyNotFoundException)
            {
                Log.Warning("[SigScanner] 找不到 ScanText 的 signature：{Signature}", signature);
                return IntPtr.Zero;
            }
        }

        public static bool TryScanText(SigScanner self, string signature, out IntPtr result)
        {
            result = self.ScanText(signature);
            return result != IntPtr.Zero;
        }

        public static void SetupCopiedSegments(SigScanner self)
        {
            self.__TcOriginalSetupCopiedSegments();

            if (!File.Exists(self.Module.FileName))
                return;

            try
            {
                var image = File.ReadAllBytes(self.Module.FileName);
                using var stream = new FileStream(self.Module.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new BinaryReader(stream);
                stream.Seek(0x3C, SeekOrigin.Begin);
                var ntHeaderOffset = reader.ReadInt32();
                stream.Seek(ntHeaderOffset + 6, SeekOrigin.Begin);
                var sectionCount = reader.ReadInt16();
                stream.Seek(16, SeekOrigin.Current);
                stream.Seek(240, SeekOrigin.Current);

                for (var index = 0; index < sectionCount; index++)
                {
                    var name = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)).TrimEnd('\0');
                    var virtualSize = reader.ReadInt32();
                    var virtualAddress = reader.ReadInt32();
                    _ = reader.ReadInt32();
                    var rawOffset = reader.ReadInt32();
                    var nextSection = stream.Position + 16;
                    if (name == ".text")
                    {
                        Marshal.Copy(image, rawOffset, self.moduleCopyPtr + virtualAddress, virtualSize);
                        return;
                    }

                    stream.Seek(nextSection, SeekOrigin.Begin);
                }
            }
            catch (Exception exception)
            {
                Log.Error(exception, "無法從磁碟映像還原乾淨的 .text 區段。");
            }
        }
    }
}
#endif
