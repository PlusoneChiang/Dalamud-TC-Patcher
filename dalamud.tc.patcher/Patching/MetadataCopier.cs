using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Dalamud.Tc.Patcher.Patching;

/// <summary>
/// 將已編譯規則的 Cecil metadata 與 IL 複製至目標模組；只處理技術性匯入，不承載本地化業務規則。
/// </summary>
internal sealed class MetadataCopier
{
    private readonly ModuleDefinition sourceModule;
    private readonly ModuleDefinition targetModule;
    private readonly IMetadataScope sourceSelf;
    private readonly bool renameGeneratedRoots;
    private readonly Dictionary<TypeDefinition, TypeDefinition> types = new();
    private readonly Dictionary<TypeReference, TypeReference> importedTypes = new();
    private readonly Dictionary<FieldDefinition, FieldDefinition> fields = new();
    private readonly Dictionary<MethodDefinition, MethodDefinition> methods = new();
    private readonly Dictionary<MethodReference, MethodReference> importedMethods = new();
    private readonly Dictionary<GenericParameter, GenericParameter> genericParameters = new();
    private readonly List<TypeDefinition> pendingTypes = new();

    /// <summary>
    /// 建立一次規則組件至目標組件的複製工作階段，在 <see cref="RuleInjector"/> 已選定要注入的型別後使用。
    /// </summary>
    public MetadataCopier(ModuleDefinition sourceModule, ModuleDefinition targetModule, AssemblyNameReference sourceIdentity, bool renameGeneratedRoots)
    {
        this.sourceModule = sourceModule;
        this.targetModule = targetModule;
        this.sourceSelf = sourceIdentity.Name == targetModule.Assembly.Name.Name ? targetModule : EnsureReference(sourceIdentity);
        this.renameGeneratedRoots = renameGeneratedRoots;
        SynchronizeExternalAssemblyReferences();
    }

    /// <summary>取得已定義至目標模組的對應型別，供注入流程設定巢狀型別名稱與可見性。</summary>
    public TypeDefinition MappedType(TypeDefinition source) => types[source];

    /// <summary>先建立型別骨架與巢狀型別，待全部定義完成後再由 <see cref="PopulateAll"/> 複製成員與 IL。</summary>
    public void DefineType(TypeDefinition source, Collection<TypeDefinition> destination)
    {
        if (types.TryGetValue(source, out var existing))
        {
            if (!destination.Contains(existing))
                destination.Add(existing);
            return;
        }

        var target = new TypeDefinition(source.Namespace, GetTargetTypeName(source), source.Attributes);
        types.Add(source, target);
        destination.Add(target);
        pendingTypes.Add(source);
        foreach (var nested in source.NestedTypes)
            DefineType(nested, target.NestedTypes);
    }

    internal static bool IsGeneratedRoot(TypeDefinition type) => type.Name.StartsWith("<", StringComparison.Ordinal) || type.Namespace is "Microsoft.CodeAnalysis" or "System.Runtime.CompilerServices";

    private static TypeDefinition? FindTopLevelType(ModuleDefinition module, string name) => module.Types.FirstOrDefault(t => t.FullName == name);

    private string GetTargetTypeName(TypeDefinition source) =>
        renameGeneratedRoots && source.DeclaringType is null && IsGeneratedRoot(source)
            ? "__TcGenerated_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.FullName))).ToLowerInvariant()[..16]
            : source.Name;

    /// <summary>在所有型別骨架建立後複製成員、metadata 與方法 IL，避免前向參照遺失。</summary>
    public void PopulateAll()
    {
        // IL 可能向前參照其他型別或成員，必須先建立全部定義再複製方法內容。
        foreach (var source in pendingTypes)
        {
            var target = types[source];
            target.Attributes = source.Attributes;
            CopyGenericParameters(source, target);
        }
        foreach (var source in pendingTypes)
            DefineMembers(source);
        for (var i = 0; i < pendingTypes.Count; i++)
            PopulateTypeMetadata(pendingTypes[i]);
        foreach (var source in pendingTypes)
            foreach (var method in source.Methods)
                CopyMethodBody(method, methods[method]);
    }

    private void DefineMembers(TypeDefinition source)
    {
        var target = types[source];
        foreach (var sourceField in source.Fields)
        {
            var targetField = target.Fields.FirstOrDefault(x => x.Name == sourceField.Name && SameType(x.FieldType, sourceField.FieldType))
                ?? AddField(target, sourceField);
            fields[sourceField] = targetField;
        }
        foreach (var sourceMethod in source.Methods)
        {
            var targetMethod = target.Methods.FirstOrDefault(x => SameMethod(x, sourceMethod))
                ?? AddMethod(target, sourceMethod);
            methods[sourceMethod] = targetMethod;
        }
    }

    private void PopulateTypeMetadata(TypeDefinition source)
    {
        var target = types[source];
        target.Attributes = source.Attributes;
        target.BaseType = ImportType(source.BaseType);
        target.PackingSize = source.PackingSize;
        target.ClassSize = source.ClassSize;
        target.Interfaces.Clear();
        foreach (var implementation in source.Interfaces)
        {
            var clone = new InterfaceImplementation(ImportType(implementation.InterfaceType));
            CopyCustomAttributes(implementation, clone);
            target.Interfaces.Add(clone);
        }
        CopyCustomAttributes(source, target);

        foreach (var sourceField in source.Fields)
            CopyField(sourceField, fields[sourceField]);

        foreach (var sourceMethod in source.Methods)
            PrepareMethod(sourceMethod, methods[sourceMethod]);

        CopyPropertiesAndEvents(source, target);
    }

    private FieldDefinition AddField(TypeDefinition target, FieldDefinition source)
    {
        var field = new FieldDefinition(source.Name, source.Attributes, targetModule.TypeSystem.Object);
        target.Fields.Add(field);
        return field;
    }

    private void CopyField(FieldDefinition source, FieldDefinition target)
    {
        target.Attributes = source.Attributes;
        target.FieldType = ImportType(source.FieldType);
        if (source.HasConstant)
            target.Constant = CloneConstant(source.Constant);
        target.InitialValue = source.InitialValue?.ToArray() ?? Array.Empty<byte>();
        target.Offset = source.Offset;
        target.MarshalInfo = source.MarshalInfo;
        CopyCustomAttributes(source, target);
    }

    private MethodDefinition AddMethod(TypeDefinition target, MethodDefinition source)
    {
        var method = new MethodDefinition(source.Name, source.Attributes, targetModule.TypeSystem.Void)
        {
            ImplAttributes = source.ImplAttributes,
            CallingConvention = source.CallingConvention,
        };
        target.Methods.Add(method);
        return method;
    }

    private void PrepareMethod(MethodDefinition source, MethodDefinition target)
    {
        target.Attributes = source.Attributes;
        target.ImplAttributes = source.ImplAttributes;
        target.CallingConvention = source.CallingConvention;
        CopyGenericParameters(source, target);
        target.ReturnType = ImportType(source.ReturnType);
        target.Parameters.Clear();
        foreach (var parameter in source.Parameters)
        {
            var clone = new ParameterDefinition(parameter.Name, parameter.Attributes, ImportType(parameter.ParameterType))
            {
                MarshalInfo = parameter.MarshalInfo,
            };
            if (parameter.HasConstant)
                clone.Constant = CloneConstant(parameter.Constant);
            CopyCustomAttributes(parameter, clone);
            target.Parameters.Add(clone);
        }

        CopyCustomAttributes(source.MethodReturnType, target.MethodReturnType);
        CopyCustomAttributes(source, target);
        target.Overrides.Clear();
        foreach (var implementation in source.Overrides)
            target.Overrides.Add(ImportMethod(implementation));
        if (source.PInvokeInfo is not null)
            target.PInvokeInfo = new PInvokeInfo(source.PInvokeInfo.Attributes, source.PInvokeInfo.EntryPoint, ImportModuleReference(source.PInvokeInfo.Module));
    }

    private void CopyMethodBody(MethodDefinition source, MethodDefinition target)
    {
        if (source.Body is null)
        {
            target.Body = null;
            return;
        }

        var body = new MethodBody(target) { InitLocals = source.Body.InitLocals, MaxStackSize = source.Body.MaxStackSize };
        target.Body = body;
        var variables = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var variable in source.Body.Variables)
        {
            var clone = new VariableDefinition(ImportType(variable.VariableType));
            body.Variables.Add(clone);
            variables.Add(variable, clone);
        }

        var instructions = new Dictionary<Instruction, Instruction>();
        foreach (var instruction in source.Body.Instructions)
        {
            var clone = Instruction.Create(OpCodes.Nop);
            clone.OpCode = instruction.OpCode;
            body.Instructions.Add(clone);
            instructions.Add(instruction, clone);
        }

        foreach (var instruction in source.Body.Instructions)
            instructions[instruction].Operand = ImportOperand(instruction.Operand, instructions, variables);

        foreach (var handler in source.Body.ExceptionHandlers)
        {
            body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                CatchType = ImportType(handler.CatchType),
                TryStart = MapInstruction(handler.TryStart, instructions),
                TryEnd = MapInstruction(handler.TryEnd, instructions),
                HandlerStart = MapInstruction(handler.HandlerStart, instructions),
                HandlerEnd = MapInstruction(handler.HandlerEnd, instructions),
                FilterStart = MapInstruction(handler.FilterStart, instructions),
            });
        }
    }

    private object? ImportOperand(object? operand, Dictionary<Instruction, Instruction> instructions, Dictionary<VariableDefinition, VariableDefinition> variables) => operand switch
    {
        null => null,
        Instruction instruction => instructions[instruction],
        Instruction[] instructionArray => instructionArray.Select(x => instructions[x]).ToArray(),
        VariableDefinition variable => variables[variable],
        ParameterDefinition parameter => ImportParameter(parameter),
        MethodReference method => ImportMethod(method),
        FieldReference field => ImportField(field),
        TypeReference type => ImportType(type),
        CallSite callSite => ImportCallSite(callSite),
        _ => operand,
    };

    private static Instruction? MapInstruction(Instruction? instruction, Dictionary<Instruction, Instruction> map) => instruction is null ? null : map[instruction];

    private ParameterDefinition ImportParameter(ParameterDefinition source)
    {
        var sourceMethod = source.Method as MethodDefinition;
        if (sourceMethod is null || !methods.TryGetValue(sourceMethod, out var targetMethod))
            throw new NotSupportedException($"參數運算元 '{source}' 所屬的方法不在補丁範圍內。");
        return targetMethod.Parameters[source.Index];
    }

    private TypeReference ImportType(TypeReference? source)
    {
        if (source is null)
            return null!;
        if (source is GenericParameter parameter)
            return ImportGenericParameter(parameter);
        var primitive = ImportPrimitive(source.MetadataType);
        if (primitive is not null)
            return primitive;
        if (source is TypeSpecification specification)
            return ImportTypeSpecification(specification);
        if (importedTypes.TryGetValue(source, out var cached))
            return cached;

        var resolved = ResolveType(source);
        if (resolved is not null && types.TryGetValue(resolved, out var included))
            return included;
        if (resolved is not null && resolved.Module == sourceModule)
        {
            var target = FindTopLevelType(targetModule, resolved.DeclaringType?.FullName ?? resolved.FullName);
            if (target is null && resolved.DeclaringType is not null)
                target = FindTargetNested(resolved);
            if (target is not null)
                return target;
        }

        var scope = ImportScope(source.Scope);
        var result = new TypeReference(source.Namespace, source.Name, targetModule, scope, source.IsValueType || resolved?.IsValueType == true)
        {
            DeclaringType = source.DeclaringType is null ? null : ImportType(source.DeclaringType),
        };
        importedTypes.Add(source, result);
        foreach (var genericDefinitionParameter in source.GenericParameters)
        {
            var clone = new GenericParameter(genericDefinitionParameter.Name, result) { Attributes = genericDefinitionParameter.Attributes };
            result.GenericParameters.Add(clone);
            genericParameters[genericDefinitionParameter] = clone;
        }
        return result;
    }

    private TypeReference? ImportPrimitive(MetadataType metadataType) => metadataType switch
    {
        MetadataType.Void => targetModule.TypeSystem.Void,
        MetadataType.Boolean => targetModule.TypeSystem.Boolean,
        MetadataType.Char => targetModule.TypeSystem.Char,
        MetadataType.SByte => targetModule.TypeSystem.SByte,
        MetadataType.Byte => targetModule.TypeSystem.Byte,
        MetadataType.Int16 => targetModule.TypeSystem.Int16,
        MetadataType.UInt16 => targetModule.TypeSystem.UInt16,
        MetadataType.Int32 => targetModule.TypeSystem.Int32,
        MetadataType.UInt32 => targetModule.TypeSystem.UInt32,
        MetadataType.Int64 => targetModule.TypeSystem.Int64,
        MetadataType.UInt64 => targetModule.TypeSystem.UInt64,
        MetadataType.Single => targetModule.TypeSystem.Single,
        MetadataType.Double => targetModule.TypeSystem.Double,
        MetadataType.String => targetModule.TypeSystem.String,
        MetadataType.Object => targetModule.TypeSystem.Object,
        MetadataType.IntPtr => targetModule.TypeSystem.IntPtr,
        MetadataType.UIntPtr => targetModule.TypeSystem.UIntPtr,
        MetadataType.TypedByReference => targetModule.TypeSystem.TypedReference,
        _ => null,
    };

    private TypeReference ImportTypeSpecification(TypeSpecification source) => source switch
    {
        ArrayType array => ImportArrayType(array),
        ByReferenceType byReference => new ByReferenceType(ImportType(byReference.ElementType)),
        PointerType pointer => new PointerType(ImportType(pointer.ElementType)),
        PinnedType pinned => new PinnedType(ImportType(pinned.ElementType)),
        SentinelType sentinel => new SentinelType(ImportType(sentinel.ElementType)),
        OptionalModifierType optional => new OptionalModifierType(ImportType(optional.ModifierType), ImportType(optional.ElementType)),
        RequiredModifierType required => new RequiredModifierType(ImportType(required.ModifierType), ImportType(required.ElementType)),
        GenericInstanceType instance => ImportGenericInstance(instance),
        FunctionPointerType functionPointer => ImportFunctionPointer(functionPointer),
        _ => throw new NotSupportedException($"不支援型別規格 '{source.GetType().Name}'。"),
    };

    private GenericInstanceType ImportGenericInstance(GenericInstanceType source)
    {
        var result = new GenericInstanceType(ImportType(source.ElementType)) { IsValueType = source.IsValueType };
        foreach (var argument in source.GenericArguments)
            result.GenericArguments.Add(ImportType(argument));
        return result;
    }

    private ArrayType ImportArrayType(ArrayType source)
    {
        if (source.IsVector)
            return new ArrayType(ImportType(source.ElementType));
        var result = new ArrayType(ImportType(source.ElementType), source.Rank);
        result.Dimensions.Clear();
        foreach (var dimension in source.Dimensions)
            result.Dimensions.Add(new ArrayDimension(dimension.LowerBound, dimension.UpperBound));
        return result;
    }

    private FunctionPointerType ImportFunctionPointer(FunctionPointerType source)
    {
        var result = new FunctionPointerType { CallingConvention = source.CallingConvention, HasThis = source.HasThis, ExplicitThis = source.ExplicitThis, ReturnType = ImportType(source.ReturnType) };
        foreach (var parameter in source.Parameters)
            result.Parameters.Add(new ParameterDefinition(ImportType(parameter.ParameterType)));
        return result;
    }

    private GenericParameter ImportGenericParameter(GenericParameter source)
    {
        if (genericParameters.TryGetValue(source, out var result))
            return result;
        if (source.Owner is TypeReference ownerType)
        {
            var importedOwner = ImportType(ownerType);
            // Payload 的 TypeReference 可能保留 !n operand，卻未在 owner 具現
            // GenericParameters；此處補齊必要的 owner slot。
            while (importedOwner.GenericParameters.Count <= source.Position)
                importedOwner.GenericParameters.Add(new GenericParameter($"!{importedOwner.GenericParameters.Count}", importedOwner));
            if (source.Position < importedOwner.GenericParameters.Count)
                return importedOwner.GenericParameters[source.Position];
            var targetType = ResolveTargetType(ImportType(ownerType));
            if (targetType is not null && source.Position < targetType.GenericParameters.Count)
                return targetType.GenericParameters[source.Position];
        }
        if (source.Owner is MethodReference ownerMethod)
        {
            var targetType = ResolveTargetType(ImportType(ownerMethod.DeclaringType));
            var targetMethod = targetType?.Methods.FirstOrDefault(x =>
                x.Name == ownerMethod.Name && x.GenericParameters.Count == ownerMethod.GenericParameters.Count &&
                x.Parameters.Count == ownerMethod.Parameters.Count);
            if (targetMethod is not null && source.Position < targetMethod.GenericParameters.Count)
                return targetMethod.GenericParameters[source.Position];
        }
        throw new NotSupportedException($"無法對應泛型參數 '{source.FullName}'；擁有者為 '{source.Owner?.GetType().Name}: {source.Owner}'.");
    }

    private FieldReference ImportField(FieldReference source)
    {
        var resolved = ResolveField(source);
        if (source.DeclaringType is not TypeSpecification && resolved is not null && fields.TryGetValue(resolved, out var mapped))
            return mapped;
        var declaring = ImportType(source.DeclaringType);
        var targetDefinition = ResolveTargetType(declaring);
        var match = targetDefinition?.Fields.FirstOrDefault(x => x.Name == source.Name && SameType(x.FieldType, source.FieldType));
        return source.DeclaringType is not TypeSpecification && match is not null
            ? match
            : new FieldReference(source.Name, ImportType(source.FieldType), declaring);
    }

    private MethodReference ImportMethod(MethodReference source)
    {
        if (source is GenericInstanceMethod generic)
        {
            var element = ImportMethod(generic.ElementMethod);
            var instance = new GenericInstanceMethod(element);
            foreach (var argument in generic.GenericArguments)
                instance.GenericArguments.Add(ImportType(argument));
            return instance;
        }

        if (importedMethods.TryGetValue(source, out var cached))
            return cached;

        var resolved = ResolveMethod(source);
        if (source.DeclaringType is not TypeSpecification && resolved is not null && methods.TryGetValue(resolved, out var mapped))
            return mapped;
        var declaring = ImportType(source.DeclaringType);
        var targetDefinition = ResolveTargetType(declaring);
        var match = targetDefinition?.Methods.FirstOrDefault(x => SameMethod(x, source));
        if (source.DeclaringType is not TypeSpecification && match is not null)
            return match;

        // 簽章可能包含此方法的 !!n，必須先建立泛型參數。
        var result = new MethodReference(source.Name, targetModule.TypeSystem.Void, declaring)
        {
            HasThis = source.HasThis,
            ExplicitThis = source.ExplicitThis,
            CallingConvention = source.CallingConvention,
        };
        importedMethods.Add(source, result);
        foreach (var parameter in source.GenericParameters)
        {
            var clone = new GenericParameter(parameter.Name, result) { Attributes = parameter.Attributes };
            result.GenericParameters.Add(clone);
            genericParameters[parameter] = clone;
        }
        result.ReturnType = ImportType(source.ReturnType);
        foreach (var parameter in source.Parameters)
            result.Parameters.Add(new ParameterDefinition(ImportType(parameter.ParameterType)));
        return result;
    }

    private CallSite ImportCallSite(CallSite source)
    {
        var result = new CallSite(ImportType(source.ReturnType)) { CallingConvention = source.CallingConvention, HasThis = source.HasThis, ExplicitThis = source.ExplicitThis };
        foreach (var parameter in source.Parameters)
            result.Parameters.Add(new ParameterDefinition(ImportType(parameter.ParameterType)));
        return result;
    }

    private void CopyGenericParameters(IGenericParameterProvider source, IGenericParameterProvider target)
    {
        target.GenericParameters.Clear();
        foreach (var parameter in source.GenericParameters)
        {
            var clone = new GenericParameter(parameter.Name, target) { Attributes = parameter.Attributes };
            target.GenericParameters.Add(clone);
            genericParameters[parameter] = clone;
        }
        foreach (var parameter in source.GenericParameters)
        {
            var clone = genericParameters[parameter];
            foreach (var constraint in parameter.Constraints)
                clone.Constraints.Add(new GenericParameterConstraint(ImportType(constraint.ConstraintType)));
            CopyCustomAttributes(parameter, clone);
        }
    }

    private void CopyPropertiesAndEvents(TypeDefinition source, TypeDefinition target)
    {
        target.Properties.Clear();
        foreach (var property in source.Properties)
        {
            var clone = new PropertyDefinition(property.Name, property.Attributes, ImportType(property.PropertyType))
            {
                GetMethod = property.GetMethod is null ? null : methods[property.GetMethod],
                SetMethod = property.SetMethod is null ? null : methods[property.SetMethod],
            };
            foreach (var parameter in property.Parameters)
                clone.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, ImportType(parameter.ParameterType)));
            CopyCustomAttributes(property, clone);
            target.Properties.Add(clone);
        }

        target.Events.Clear();
        foreach (var evt in source.Events)
        {
            var clone = new EventDefinition(evt.Name, evt.Attributes, ImportType(evt.EventType))
            {
                AddMethod = evt.AddMethod is null ? null : methods[evt.AddMethod],
                RemoveMethod = evt.RemoveMethod is null ? null : methods[evt.RemoveMethod],
                InvokeMethod = evt.InvokeMethod is null ? null : methods[evt.InvokeMethod],
            };
            CopyCustomAttributes(evt, clone);
            target.Events.Add(clone);
        }
    }

    private void CopyCustomAttributes(ICustomAttributeProvider source, ICustomAttributeProvider target)
    {
        target.CustomAttributes.Clear();
        foreach (var attribute in source.CustomAttributes)
        {
            var clone = new CustomAttribute(ImportMethod(attribute.Constructor));
            foreach (var argument in attribute.ConstructorArguments)
                clone.ConstructorArguments.Add(ImportAttributeArgument(argument));
            foreach (var field in attribute.Fields)
                clone.Fields.Add(new CustomAttributeNamedArgument(field.Name, ImportAttributeArgument(field.Argument)));
            foreach (var property in attribute.Properties)
                clone.Properties.Add(new CustomAttributeNamedArgument(property.Name, ImportAttributeArgument(property.Argument)));
            target.CustomAttributes.Add(clone);
        }
    }

    private CustomAttributeArgument ImportAttributeArgument(CustomAttributeArgument source)
    {
        object? value = source.Value;
        if (value is TypeReference type)
            value = ImportType(type);
        else if (value is CustomAttributeArgument[] array)
            value = array.Select(ImportAttributeArgument).ToArray();
        else if (value is CustomAttributeArgument argument)
            value = ImportAttributeArgument(argument);
        return new CustomAttributeArgument(ImportType(source.Type), value);
    }

    private AssemblyNameReference EnsureReference(AssemblyNameReference source)
    {
        var existing = targetModule.AssemblyReferences.FirstOrDefault(x => x.FullName == source.FullName);
        if (existing is not null)
            return existing;
        var clone = new AssemblyNameReference(source.Name, source.Version)
        {
            Culture = source.Culture,
            HashAlgorithm = source.HashAlgorithm,
            IsRetargetable = source.IsRetargetable,
            PublicKeyToken = source.PublicKeyToken,
        };
        targetModule.AssemblyReferences.Add(clone);
        return clone;
    }

    private IMetadataScope ImportScope(IMetadataScope? scope) => scope switch
    {
        null => targetModule,
        ModuleDefinition module when module == sourceModule => sourceSelf,
        AssemblyNameReference assembly when assembly.FullName == sourceModule.Assembly.Name.FullName => sourceSelf,
        AssemblyNameReference assembly when assembly.Name == targetModule.Assembly.Name.Name => targetModule,
        AssemblyNameReference assembly => EnsureReference(assembly),
        ModuleReference module => ImportModuleReference(module),
        _ => throw new NotSupportedException($"不支援中繼資料範圍 '{scope.GetType().Name}'。"),
    };

    private ModuleReference ImportModuleReference(ModuleReference source)
    {
        var existing = targetModule.ModuleReferences.FirstOrDefault(x => x.Name == source.Name);
        if (existing is not null)
            return existing;
        var clone = new ModuleReference(source.Name);
        targetModule.ModuleReferences.Add(clone);
        return clone;
    }

    private void SynchronizeExternalAssemblyReferences()
    {
        foreach (var source in sourceModule.AssemblyReferences)
        {
            if (source.Name == targetModule.Assembly.Name.Name)
                continue;
            var target = targetModule.AssemblyReferences.FirstOrDefault(x => x.Name == source.Name);
            if (target is null)
            {
                targetModule.AssemblyReferences.Add(CloneAssemblyReference(source));
                continue;
            }

            // 未修改的中繼資料仍參照此物件，因此必須原地更新組件版本。
            target.Version = source.Version;
            target.Culture = source.Culture;
            target.HashAlgorithm = source.HashAlgorithm;
            target.IsRetargetable = source.IsRetargetable;
            target.PublicKeyToken = source.PublicKeyToken;
        }
    }

    private static AssemblyNameReference CloneAssemblyReference(AssemblyNameReference source) => new(source.Name, source.Version)
    {
        Culture = source.Culture,
        HashAlgorithm = source.HashAlgorithm,
        IsRetargetable = source.IsRetargetable,
        PublicKeyToken = source.PublicKeyToken,
    };

    private TypeDefinition? ResolveTargetType(TypeReference reference)
    {
        var root = reference;
        while (root.DeclaringType is not null)
            root = root.DeclaringType;
        var target = FindTopLevelType(targetModule, root.FullName);
        if (target is null)
            return null;
        var nested = reference.DeclaringType is null ? target : FindNested(reference, target);
        return nested;
    }

    private TypeDefinition? FindTargetNested(TypeDefinition source)
    {
        var root = source;
        while (root.DeclaringType is not null)
            root = root.DeclaringType;
        var target = FindTopLevelType(targetModule, root.FullName);
        return target is null ? null : FindNested(source, target);
    }

    private static TypeDefinition? FindNested(TypeReference source, TypeDefinition root)
    {
        var names = new Stack<string>();
        for (var current = source; current.DeclaringType is not null; current = current.DeclaringType)
            names.Push(current.Name);
        var target = root;
        while (names.Count > 0)
        {
            var nestedName = names.Pop();
            target = target.NestedTypes.FirstOrDefault(x => x.Name == nestedName)!;
            if (target is null)
                return null;
        }
        return target;
    }

    private static TypeDefinition? ResolveType(TypeReference type)
    {
        try { return type.Resolve(); }
        catch (AssemblyResolutionException) { return null; }
    }

    private static FieldDefinition? ResolveField(FieldReference field)
    {
        try { return field.Resolve(); }
        catch (AssemblyResolutionException) { return null; }
    }

    private static MethodDefinition? ResolveMethod(MethodReference method)
    {
        try { return method.Resolve(); }
        catch (AssemblyResolutionException) { return null; }
    }

    private static bool SameMethod(MethodReference target, MethodReference source) =>
        target.Name == source.Name && target.GenericParameters.Count == source.GenericParameters.Count &&
        SameType(target.ReturnType, source.ReturnType) && target.Parameters.Count == source.Parameters.Count &&
        target.Parameters.Zip(source.Parameters).All(x => SameType(x.First.ParameterType, x.Second.ParameterType));

    private static bool SameType(TypeReference first, TypeReference second) => first.FullName == second.FullName;

    private static object? CloneConstant(object? constant) => constant is byte[] bytes ? bytes.ToArray() : constant;
}
