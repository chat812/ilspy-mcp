using System.ComponentModel;
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
