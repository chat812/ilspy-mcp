using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using DotNetDecompilerMcp.Services;

namespace DotNetDecompilerMcp.Tools;

[McpServerToolType]
public sealed class IndexTools(DecompilerService svc, DatabaseService db)
{
    /// <summary>
    /// Build or rebuild the persistent SQLite index for an assembly. Stores all types,
    /// members, and pre-computed reference data so subsequent search_types, search_members,
    /// list_types, list_members, and find_references calls return instantly from the DB
    /// rather than scanning IL on every call. Called automatically on load_assembly, but
    /// can be re-run here to force a refresh after the file changes.
    /// </summary>
    [McpServerTool(Name = "index_assembly")]
    [Description("Build or rebuild the SQLite index for an assembly. Enables fast search and find_references. Runs automatically on load_assembly.")]
    public string IndexAssembly(
        [Description("Path to the .NET assembly to index.")] string assemblyPath)
    {
        try
        {
            var absPath = Path.GetFullPath(assemblyPath);
            var cached  = svc.LoadAssembly(absPath);
            var sw      = System.Diagnostics.Stopwatch.StartNew();
            var (types, members, refs) = db.IndexAssembly(absPath, cached);
            sw.Stop();

            return JsonSerializer.Serialize(new
            {
                success     = true,
                assemblyPath = absPath,
                types,
                members,
                refs,
                elapsedMs   = sw.ElapsedMilliseconds,
                message     = $"Indexed {types} types, {members} members, {refs} references in {sw.ElapsedMilliseconds} ms."
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }
}
