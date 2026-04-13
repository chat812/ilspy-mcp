using System.ComponentModel;
using System.Reflection.Metadata;
using System.Text.Json;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;
using DotNetDecompilerMcp.Services;

namespace DotNetDecompilerMcp.Tools;

[McpServerToolType]
public sealed class MemberTools(DecompilerService svc, DatabaseService db)
{
    /// <summary>
    /// List all members (methods, properties, fields, events) of a type without
    /// decompiling their bodies. Useful for quickly understanding a type's API surface.
    /// </summary>
    [McpServerTool(Name = "list_members")]
    [Description("List all members of a type (methods, properties, fields, events) without decompiling bodies.")]
    public string ListMembers(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Fully-qualified type name.")] string typeName)
    {
        try
        {
            var absPath = Path.GetFullPath(assemblyPath);
            var cached  = svc.LoadAssembly(absPath);
            db.EnsureIndexed(absPath, cached);

            var rows = db.GetMembers(absPath, typeName);
            if (rows.Count == 0 && svc.FindType(cached, typeName) == null)
                return Error($"Type '{typeName}' not found.");

            var members = rows.Select(m => new
            {
                memberType     = m.MemberType,
                name           = m.Name,
                signature      = m.Signature,
                accessModifier = m.Access,
                isStatic       = m.IsStatic,
            }).ToList<object>();

            return JsonSerializer.Serialize(new { success = true, typeName, members });
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Decompile a single method to C# source. Supports overload resolution via
    /// parameterTypes when multiple overloads exist with the same name.
    /// </summary>
    [McpServerTool(Name = "decompile_method")]
    [Description("Decompile a single method to C# source. Use parameterTypes for overload disambiguation.")]
    public string DecompileMethod(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Fully-qualified type name.")] string typeName,
        [Description("Method name to decompile.")] string methodName,
        [Description("Optional parameter type names for overload resolution, e.g. ['String', 'Int32'].")] string[]? parameterTypes = null)
    {
        try
        {
            var cached = svc.LoadAssembly(assemblyPath);
            var typeDef = svc.FindType(cached, typeName);
            if (typeDef == null)
                return Error($"Type '{typeName}' not found.");

            var method = svc.FindMethod(typeDef, methodName, parameterTypes);
            if (method == null)
                return Error($"Method '{methodName}' not found on '{typeName}'.");

            var source = svc.DecompileMethod(cached, method);
            return JsonSerializer.Serialize(new
            {
                success = true,
                typeName,
                methodName = method.Name,
                signature = method.ToString(),
                source
            });
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Get raw IL (Intermediate Language) disassembly for a method. Useful for
    /// understanding exact runtime behavior, inspecting optimizations, or when
    /// C# decompilation is ambiguous.
    /// </summary>
    [McpServerTool(Name = "get_il")]
    [Description("Get raw IL disassembly for a method. Use parameterTypes for overload disambiguation.")]
    public string GetIL(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Fully-qualified type name.")] string typeName,
        [Description("Method name.")] string methodName,
        [Description("Optional parameter type names for overload resolution.")] string[]? parameterTypes = null)
    {
        try
        {
            var cached = svc.LoadAssembly(assemblyPath);
            var typeDef = svc.FindType(cached, typeName);
            if (typeDef == null)
                return Error($"Type '{typeName}' not found.");

            var method = svc.FindMethod(typeDef, methodName, parameterTypes);
            if (method == null)
                return Error($"Method '{methodName}' not found on '{typeName}'.");

            var il = svc.GetIL(cached, method);
            return JsonSerializer.Serialize(new
            {
                success = true,
                typeName,
                methodName = method.Name,
                il
            });
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Get detailed signatures for all (or a specific) method(s) on a type:
    /// parameter names/types, return type, generic type parameters, and modifier flags.
    /// </summary>
    [McpServerTool(Name = "get_method_signatures")]
    [Description("Get detailed method signatures for a type: parameters, return type, generic params, flags. Leave methodName empty to list all methods.")]
    public string GetMethodSignatures(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Fully-qualified type name.")] string typeName,
        [Description("Method name to query. Leave empty to return all methods on the type.")] string methodName = "")
    {
        try
        {
            var cached = svc.LoadAssembly(assemblyPath);
            var typeDef = svc.FindType(cached, typeName);
            if (typeDef == null)
                return Error($"Type '{typeName}' not found.");

            var methods = string.IsNullOrEmpty(methodName)
                ? typeDef.Methods.Where(m => !m.Name.Contains('<')).ToList()
                : typeDef.Methods.Where(m =>
                    string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) &&
                    !m.Name.Contains('<')).ToList();

            if (methods.Count == 0 && !string.IsNullOrEmpty(methodName))
                return Error($"Method '{methodName}' not found on '{typeName}'.");

            var result = methods.Select(m => new
            {
                name           = m.Name,
                returnType     = m.ReturnType.FullName,
                parameters     = m.Parameters.Select(p => new
                {
                    name       = p.Name,
                    type       = p.Type.FullName,
                    isOptional = p.IsOptional,
                    isParams   = p.IsParams,
                }).ToList(),
                typeParameters = m.TypeParameters.Select(tp => tp.Name).ToList(),
                accessibility  = m.Accessibility.ToString().ToLowerInvariant(),
                isStatic       = m.IsStatic,
                isVirtual      = m.IsVirtual,
                isAbstract     = m.IsAbstract,
                isOverride     = m.IsOverride,
                isSealed       = m.IsSealed,
                isConstructor  = m.IsConstructor,
                hasBody        = m.HasBody,
            }).ToList<object>();

            return JsonSerializer.Serialize(new
            {
                success     = true,
                typeName,
                methodCount = result.Count,
                methods     = result
            });
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    /// <summary>
    /// Get raw method body information: MaxStack, InitLocals, IL byte count,
    /// local variable count, and exception handler regions.
    /// </summary>
    [McpServerTool(Name = "get_method_body")]
    [Description("Get method body details: MaxStack, InitLocals, IL byte count, local count, exception handlers.")]
    public string GetMethodBody(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Fully-qualified type name.")] string typeName,
        [Description("Method name.")] string methodName,
        [Description("Optional parameter types for overload resolution.")] string[]? parameterTypes = null)
    {
        try
        {
            var cached = svc.LoadAssembly(assemblyPath);
            var typeDef = svc.FindType(cached, typeName);
            if (typeDef == null)
                return Error($"Type '{typeName}' not found.");

            var method = svc.FindMethod(typeDef, methodName, parameterTypes);
            if (method == null)
                return Error($"Method '{methodName}' not found on '{typeName}'.");

            var meta   = cached.PEFile.Metadata;
            var handle = (MethodDefinitionHandle)method.MetadataToken;
            var md     = meta.GetMethodDefinition(handle);

            if (md.RelativeVirtualAddress == 0)
                return JsonSerializer.Serialize(new
                {
                    success    = true,
                    typeName,
                    methodName = method.Name,
                    hasBody    = false
                });

            var body    = cached.PEFile.Reader.GetMethodBody(md.RelativeVirtualAddress);
            var ilBytes = body.GetILReader().Length;

            // Decode local variable count from the LocalVarSig blob
            int localCount = 0;
            if (!body.LocalSignature.IsNil)
            {
                try
                {
                    var sig    = meta.GetStandaloneSignature(body.LocalSignature);
                    var reader = meta.GetBlobReader(sig.Signature);
                    if (reader.RemainingBytes > 0 && reader.ReadByte() == 0x07) // LOCAL_SIG
                        localCount = reader.ReadCompressedInteger();
                }
                catch { }
            }

            var exHandlers = body.ExceptionRegions.Select(r => new
            {
                kind          = r.Kind.ToString(),
                tryOffset     = r.TryOffset,
                tryLength     = r.TryLength,
                handlerOffset = r.HandlerOffset,
                handlerLength = r.HandlerLength,
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                success               = true,
                typeName,
                methodName            = method.Name,
                hasBody               = true,
                maxStack              = body.MaxStack,
                initLocals            = body.LocalVariablesInitialized,
                localCount,
                ilByteCount           = ilBytes,
                exceptionHandlerCount = exHandlers.Count,
                exceptionHandlers     = exHandlers,
            });
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    /// <summary>
    /// Get the IL disassembly as a structured list of opcodes with their byte offsets.
    /// More machine-friendly than get_il's plain text output.
    /// </summary>
    [McpServerTool(Name = "get_il_opcodes_formatted")]
    [Description("Get IL as a structured list: [{offset, opcode, operand}]. Use parameterTypes for overload resolution.")]
    public string GetILOpcodesFormatted(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Fully-qualified type name.")] string typeName,
        [Description("Method name.")] string methodName,
        [Description("Optional parameter types for overload resolution.")] string[]? parameterTypes = null)
    {
        try
        {
            var cached = svc.LoadAssembly(assemblyPath);
            var typeDef = svc.FindType(cached, typeName);
            if (typeDef == null)
                return Error($"Type '{typeName}' not found.");

            var method = svc.FindMethod(typeDef, methodName, parameterTypes);
            if (method == null)
                return Error($"Method '{methodName}' not found on '{typeName}'.");

            // Use the text disassembler output and parse it into structured form
            var ilText = svc.GetIL(cached, method);
            var opcodes = new List<object>();

            foreach (var line in ilText.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("IL_")) continue;

                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx < 0) continue;

                var offsetStr = trimmed[3..colonIdx];
                if (!int.TryParse(offsetStr, System.Globalization.NumberStyles.HexNumber, null, out var offset))
                    continue;

                var rest     = trimmed[(colonIdx + 1)..].Trim();
                var spaceIdx = rest.IndexOf(' ');
                var opcode   = spaceIdx < 0 ? rest : rest[..spaceIdx];
                var operand  = spaceIdx < 0 ? null : rest[(spaceIdx + 1)..].Trim();
                if (string.IsNullOrEmpty(operand)) operand = null;

                opcodes.Add(new { offset, opcode, operand });
            }

            return JsonSerializer.Serialize(new
            {
                success    = true,
                typeName,
                methodName = method.Name,
                count      = opcodes.Count,
                opcodes,
            });
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    /// <summary>
    /// Find all methods and fields called by a specific method. Results come from
    /// the pre-built IL reference index populated during load_assembly/index_assembly.
    /// This is the inverse of find_references.
    /// </summary>
    [McpServerTool(Name = "get_callees")]
    [Description("Find all methods and fields called by a specific method. Inverse of find_references. Uses the SQLite index.")]
    public string GetCallees(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Fully-qualified type name.")] string typeName,
        [Description("Method name.")] string methodName)
    {
        try
        {
            var absPath = Path.GetFullPath(assemblyPath);
            var cached  = svc.LoadAssembly(absPath);
            db.EnsureIndexed(absPath, cached);

            var typeDef = svc.FindType(cached, typeName);
            if (typeDef == null)
                return Error($"Type '{typeName}' not found.");

            var method = svc.FindMethod(typeDef, methodName, null);
            if (method == null)
                return Error($"Method '{methodName}' not found on '{typeName}'.");

            var rows = db.GetCallees(absPath, typeDef.FullName, method.Name);
            return JsonSerializer.Serialize(new
            {
                success     = true,
                typeName    = typeDef.FullName,
                methodName  = method.Name,
                calleeCount = rows.Count,
                callees     = rows.Select(c => new { targetType = c.TargetType, member = c.TargetMember }).ToList(),
            });
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    private static string GetAccess(Accessibility a) => a switch
    {
        Accessibility.Public => "public",
        Accessibility.Protected => "protected",
        Accessibility.Private => "private",
        Accessibility.Internal => "internal",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "private"
    };

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { success = false, error = message });
}

internal static class MemberExtensions
{
    public static bool IsCompilerGenerated(this IMember member) =>
        member.Name.StartsWith('<') || member.Name.Contains('>');
}
