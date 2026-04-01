using System.ComponentModel;
using System.Text.Json;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;
using DotNetDecompilerMcp.Services;

namespace DotNetDecompilerMcp.Tools;

[McpServerToolType]
public sealed class TypeTools(DecompilerService svc, DatabaseService db)
{
    /// <summary>
    /// List all distinct namespaces declared in the assembly.
    /// </summary>
    [McpServerTool(Name = "list_namespaces")]
    [Description("List all distinct namespaces in the assembly.")]
    public string ListNamespaces(
        [Description("Path to the .NET assembly.")] string assemblyPath)
    {
        try
        {
            var absPath = Path.GetFullPath(assemblyPath);
            var cached  = svc.LoadAssembly(absPath);
            db.EnsureIndexed(absPath, cached);

            var namespaces = db.GetNamespaces(absPath);
            return JsonSerializer.Serialize(new { success = true, namespaces });
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// List all types in the assembly. Supports optional namespace filtering and skip/take
    /// pagination for large assemblies.
    /// </summary>
    [McpServerTool(Name = "list_types")]
    [Description("List all types in the assembly. Supports namespace filter and skip/take pagination.")]
    public string ListTypes(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Optional namespace prefix to filter by.")] string? namespaceFilter = null,
        [Description("Number of items to skip (for pagination).")] int skip = 0,
        [Description("Maximum number of items to return (for pagination).")] int take = 200)
    {
        try
        {
            var absPath = Path.GetFullPath(assemblyPath);
            var cached  = svc.LoadAssembly(absPath);
            db.EnsureIndexed(absPath, cached);

            var (total, page) = db.GetTypes(absPath, namespaceFilter, skip, take);
            return JsonSerializer.Serialize(new
            {
                success = true,
                total,
                skip,
                take,
                types = page.Select(t => new { @namespace = t.Namespace, name = t.Name, kind = t.Kind }).ToList()
            });
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Decompile a full type to C# source. If the output exceeds maxLines lines,
    /// it will be truncated with a warning. Use decompile_method for individual methods
    /// on large types.
    /// </summary>
    [McpServerTool(Name = "decompile_type")]
    [Description("Decompile a full type by fully-qualified name. Returns C# source. Use maxLines to limit output size.")]
    public string DecompileType(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Fully-qualified type name, e.g. 'MyNamespace.MyClass'.")] string typeName,
        [Description("Maximum lines to return before truncating (default 500).")] int maxLines = 500)
    {
        try
        {
            var cached = svc.LoadAssembly(assemblyPath);
            var typeDef = svc.FindType(cached, typeName);
            if (typeDef == null)
                return Error($"Type '{typeName}' not found.");

            var source = svc.DecompileType(cached, typeDef, maxLines);
            return JsonSerializer.Serialize(new { success = true, typeName, source });
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Get the type hierarchy for a type: its base class chain and all implemented interfaces.
    /// </summary>
    [McpServerTool(Name = "get_type_hierarchy")]
    [Description("Get base classes and implemented interfaces for a type.")]
    public string GetTypeHierarchy(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Fully-qualified type name.")] string typeName)
    {
        try
        {
            var cached = svc.LoadAssembly(assemblyPath);
            var typeDef = svc.FindType(cached, typeName);
            if (typeDef == null)
                return Error($"Type '{typeName}' not found.");

            var baseTypes = new List<string>();
            // DirectBaseTypes includes both base class and interfaces; filter to class only
            var current = typeDef.DirectBaseTypes.FirstOrDefault(t => t.Kind != TypeKind.Interface);
            while (current != null && current.FullName != "System.Object")
            {
                baseTypes.Add(current.FullName);
                current = current.GetDefinition()?.DirectBaseTypes.FirstOrDefault(t => t.Kind != TypeKind.Interface);
            }

            var interfaces = typeDef.DirectBaseTypes
                .Where(t => t.Kind == TypeKind.Interface)
                .Select(t => t.FullName)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                success = true,
                typeName,
                baseTypes,
                interfaces
            });
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Show all types that a given type directly depends on: field types, base types,
    /// method parameter and return types. Useful for understanding coupling.
    /// </summary>
    [McpServerTool(Name = "get_type_dependencies")]
    [Description("Show types a given type depends on (field types, base types, method param/return types).")]
    public string GetTypeDependencies(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Fully-qualified type name.")] string typeName)
    {
        try
        {
            var cached = svc.LoadAssembly(assemblyPath);
            var typeDef = svc.FindType(cached, typeName);
            if (typeDef == null)
                return Error($"Type '{typeName}' not found.");

            var deps = new HashSet<string>(StringComparer.Ordinal);

            // Base types and interfaces
            foreach (var bt in typeDef.DirectBaseTypes)
                deps.Add(bt.FullName);

            // Field types
            foreach (var field in typeDef.Fields)
                deps.Add(field.Type.FullName);

            // Method signatures
            foreach (var method in typeDef.Methods)
            {
                deps.Add(method.ReturnType.FullName);
                foreach (var param in method.Parameters)
                    deps.Add(param.Type.FullName);
            }

            // Property types
            foreach (var prop in typeDef.Properties)
                deps.Add(prop.ReturnType.FullName);

            // Remove self and primitives
            deps.Remove(typeDef.FullName);
            deps.RemoveWhere(d => d == "System.Void" || d == "System.Object");

            return JsonSerializer.Serialize(new
            {
                success = true,
                typeName,
                dependencies = deps.OrderBy(d => d).ToList()
            });
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { success = false, error = message });
}
