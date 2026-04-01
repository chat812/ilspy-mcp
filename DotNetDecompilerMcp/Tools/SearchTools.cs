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

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { success = false, error = message });
}

internal static class SearchMethodExtensions
{
    public static bool IsCompilerGenerated(this IMethod method) =>
        method.Name.StartsWith('<') || method.Name.Contains('>') || !method.HasBody;
}
