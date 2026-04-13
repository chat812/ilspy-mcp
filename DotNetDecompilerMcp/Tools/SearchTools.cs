using System.ComponentModel;
using System.Reflection.Metadata;
using System.Text.Json;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;
using DotNetDecompilerMcp.Services;

namespace DotNetDecompilerMcp.Tools;

[McpServerToolType]
public sealed class SearchTools(DecompilerService svc, DatabaseService db)
{
    /// <summary>
    /// Case-insensitive substring search across all type names in the assembly.
    /// Queries the pre-built SQLite index — fast even on large assemblies.
    /// </summary>
    [McpServerTool(Name = "search_types")]
    [Description("Case-insensitive substring search across all type names. Queries the SQLite index.")]
    public string SearchTypes(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Substring to search for in type names.")] string query,
        [Description("Maximum number of results to return (default 20).")] int maxResults = 20)
    {
        try
        {
            var absPath = Path.GetFullPath(assemblyPath);
            var cached  = svc.LoadAssembly(absPath);
            db.EnsureIndexed(absPath, cached);

            var rows = db.SearchTypes(absPath, query, maxResults);
            return JsonSerializer.Serialize(new
            {
                success = true,
                query,
                results = rows.Select(t => new { @namespace = t.Namespace, name = t.Name, kind = t.Kind }).ToList()
            });
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    /// <summary>
    /// Case-insensitive substring search across all member names (methods, properties,
    /// fields, events) in all types. Queries the pre-built SQLite index.
    /// </summary>
    [McpServerTool(Name = "search_members")]
    [Description("Search member names across all types. Queries the SQLite index.")]
    public string SearchMembers(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Substring to search for in member names.")] string query,
        [Description("Maximum number of results to return (default 20).")] int maxResults = 20)
    {
        try
        {
            var absPath = Path.GetFullPath(assemblyPath);
            var cached  = svc.LoadAssembly(absPath);
            db.EnsureIndexed(absPath, cached);

            var rows = db.SearchMembers(absPath, query, maxResults);
            return JsonSerializer.Serialize(new
            {
                success = true,
                query,
                results = rows.Select(m => new
                {
                    typeName   = m.TypeName,
                    memberType = m.MemberType,
                    memberName = m.Name,
                    signature  = m.Signature,
                }).ToList()
            });
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    /// <summary>
    /// Find all methods that reference a type or member. Results come from the pre-built
    /// SQLite index (populated via IL scanning during load_assembly / index_assembly),
    /// so this is an instant lookup rather than a full assembly scan.
    /// Omit memberName to find all references to the type itself.
    /// </summary>
    [McpServerTool(Name = "find_references")]
    [Description("Find all methods that use a given type or member. Uses the SQLite index for instant results.")]
    public string FindReferences(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Fully-qualified type name to search for.")] string typeName,
        [Description("Optional member name to narrow to a specific member.")] string? memberName = null)
    {
        try
        {
            var absPath = Path.GetFullPath(assemblyPath);
            var cached  = svc.LoadAssembly(absPath);
            db.EnsureIndexed(absPath, cached);

            // Resolve to canonical full name (handles case-insensitive input)
            var targetType = svc.FindType(cached, typeName);
            if (targetType == null)
                return Error($"Type '{typeName}' not found.");

            var rows = db.FindReferences(absPath, targetType.FullName, memberName);
            return JsonSerializer.Serialize(new
            {
                success        = true,
                typeName       = targetType.FullName,
                memberName,
                referenceCount = rows.Count,
                references     = rows.Select(r => new { containingType = r.ContainingType, method = r.Method }).ToList()
            });
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    /// <summary>
    /// Search specifically for methods by name, optionally scoped to a type.
    /// Unlike search_members (which searches all member kinds), this only returns methods.
    /// </summary>
    [McpServerTool(Name = "search_methods")]
    [Description("Search method names across all types, optionally scoped to a specific type. Queries the SQLite index.")]
    public string SearchMethods(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Substring to search for in method names.")] string query,
        [Description("Optional type name to scope the search to.")] string? typeName = null,
        [Description("Maximum number of results to return (default 20).")] int maxResults = 20)
    {
        try
        {
            var absPath = Path.GetFullPath(assemblyPath);
            var cached  = svc.LoadAssembly(absPath);
            db.EnsureIndexed(absPath, cached);

            var rows = db.SearchMethods(absPath, query, typeName, maxResults);
            return JsonSerializer.Serialize(new
            {
                success = true,
                query,
                typeName,
                results = rows.Select(m => new
                {
                    typeName   = m.TypeName,
                    methodName = m.Name,
                    signature  = m.Signature,
                }).ToList()
            });
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    /// <summary>
    /// Search for string literals used in method bodies across the assembly.
    /// Queries the pre-built SQLite index (populated during load_assembly/index_assembly).
    /// </summary>
    [McpServerTool(Name = "search_strings")]
    [Description("Search string literals used in method bodies. Queries the SQLite index.")]
    public string SearchStrings(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Substring to search for in string literal values.")] string query,
        [Description("Maximum number of results to return (default 20).")] int maxResults = 20)
    {
        try
        {
            var absPath = Path.GetFullPath(assemblyPath);
            var cached  = svc.LoadAssembly(absPath);
            db.EnsureIndexed(absPath, cached);

            var rows = db.SearchStrings(absPath, query, maxResults);
            return JsonSerializer.Serialize(new
            {
                success = true,
                query,
                results = rows.Select(s => new
                {
                    typeName = s.TypeName,
                    method   = s.MethodName,
                    value    = s.Value,
                }).ToList()
            });
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    /// <summary>
    /// Multi-scope search: simultaneously searches type names, member names, and string
    /// literals in one call. Returns up to maxPerScope results from each category.
    /// </summary>
    [McpServerTool(Name = "grep")]
    [Description("Search across type names, member names, and string literals simultaneously.")]
    public string Grep(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Substring to search for.")] string query,
        [Description("Maximum results per scope (types, members, strings). Default 10 each.")] int maxPerScope = 10)
    {
        try
        {
            var absPath = Path.GetFullPath(assemblyPath);
            var cached  = svc.LoadAssembly(absPath);
            db.EnsureIndexed(absPath, cached);

            var types   = db.SearchTypes(absPath, query, maxPerScope);
            var members = db.SearchMembers(absPath, query, maxPerScope);
            var strings = db.SearchStrings(absPath, query, maxPerScope);

            return JsonSerializer.Serialize(new
            {
                success = true,
                query,
                types   = types.Select(t => new { @namespace = t.Namespace, name = t.Name, kind = t.Kind }).ToList(),
                members = members.Select(m => new { typeName = m.TypeName, memberType = m.MemberType, name = m.Name, signature = m.Signature }).ToList(),
                strings = strings.Select(s => new { typeName = s.TypeName, method = s.MethodName, value = s.Value }).ToList(),
            });
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { success = false, error = message });
}

internal static class SearchMethodExtensions
{
    public static bool IsCompilerGenerated(this IMethod method) =>
        method.Name.StartsWith('<') || method.Name.Contains('>') || !method.HasBody;
}
