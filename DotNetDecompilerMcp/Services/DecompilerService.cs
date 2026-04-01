using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace DotNetDecompilerMcp.Services;

public sealed class CachedAssembly
{
    public required PEFile PEFile { get; init; }
    public required DecompilerTypeSystem TypeSystem { get; init; }
    public required CSharpDecompiler Decompiler { get; init; }
    public DateTime LoadedAt { get; init; } = DateTime.UtcNow;
}

public sealed class DecompilerService : IDisposable
{
    private const int MaxOutputChars = 8000;
    private const string TruncationSuffix = "\n// ... truncated. Use more specific queries to see the rest.";

    private readonly ConcurrentDictionary<string, CachedAssembly> _cache = new(StringComparer.OrdinalIgnoreCase);

    // ── Assembly loading ─────────────────────────────────────────────────────

    public CachedAssembly LoadAssembly(string assemblyPath)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        return _cache.GetOrAdd(assemblyPath, CreateCachedAssembly);
    }

    private static CachedAssembly CreateCachedAssembly(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException($"Assembly not found: {assemblyPath}");

        var pefile = new PEFile(
            assemblyPath,
            PEStreamOptions.PrefetchMetadata | PEStreamOptions.PrefetchEntireImage);

        var resolver = new UniversalAssemblyResolver(
            assemblyPath,
            throwOnError: false,
            targetFramework: pefile.DetectTargetFrameworkId());

        // Add BCL reference directory
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        if (Directory.Exists(runtimeDir))
            resolver.AddSearchDirectory(runtimeDir);

        // Also add the directory containing the assembly itself
        var assemblyDir = Path.GetDirectoryName(assemblyPath);
        if (!string.IsNullOrEmpty(assemblyDir) && Directory.Exists(assemblyDir))
            resolver.AddSearchDirectory(assemblyDir);

        var settings = new DecompilerSettings
        {
            ThrowOnAssemblyResolveErrors = false,
            LoadInMemory = true,
        };

        var typeSystem = new DecompilerTypeSystem(pefile, resolver, settings);
        var decompiler = new CSharpDecompiler(typeSystem, settings);

        return new CachedAssembly
        {
            PEFile = pefile,
            TypeSystem = typeSystem,
            Decompiler = decompiler,
        };
    }

    public bool IsLoaded(string assemblyPath) =>
        _cache.ContainsKey(Path.GetFullPath(assemblyPath));

    public void EvictAssembly(string assemblyPath)
    {
        assemblyPath = Path.GetFullPath(assemblyPath);
        if (_cache.TryRemove(assemblyPath, out var cached))
            cached.PEFile.Dispose();
    }

    // ── Type resolution helpers ──────────────────────────────────────────────

    public ITypeDefinition? FindType(CachedAssembly cached, string typeName)
    {
        var ts = cached.TypeSystem;

        // Try direct full name lookup first
        try
        {
            var found = ts.FindType(new FullTypeName(typeName)).GetDefinition();
            if (found != null) return found;
        }
        catch { /* invalid FullTypeName format — fall through to scan */ }

        // Case-insensitive full-name scan
        var meta = cached.PEFile.Metadata;
        foreach (var tdHandle in meta.TypeDefinitions)
        {
            var td = meta.GetTypeDefinition(tdHandle);
            var ns = meta.GetString(td.Namespace);
            var name = meta.GetString(td.Name);
            var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

            if (!string.Equals(fullName, typeName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                return ts.FindType(new FullTypeName(fullName)).GetDefinition();
            }
            catch { }
        }

        return null;
    }

    public IMethod? FindMethod(ITypeDefinition typeDef, string methodName, string[]? parameterTypes)
    {
        var candidates = typeDef.Methods
            .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1 || parameterTypes == null || parameterTypes.Length == 0)
            return candidates[0];

        // Overload resolution by parameter count / type names
        var matched = candidates.FirstOrDefault(m =>
            m.Parameters.Count == parameterTypes.Length &&
            m.Parameters.Select(p => p.Type.Name)
             .SequenceEqual(parameterTypes, StringComparer.OrdinalIgnoreCase));

        return matched ?? candidates[0];
    }

    // ── Decompilation helpers ────────────────────────────────────────────────

    public string DecompileType(CachedAssembly cached, ITypeDefinition typeDef, int maxLines = 500)
    {
        var handle = (TypeDefinitionHandle)typeDef.MetadataToken;
        var syntaxTree = cached.Decompiler.Decompile(handle);
        var code = SyntaxTreeToString(syntaxTree);
        return TruncateLines(code, maxLines);
    }

    public string DecompileMethod(CachedAssembly cached, IMethod method)
    {
        var handle = (MethodDefinitionHandle)method.MetadataToken;
        var syntaxTree = cached.Decompiler.Decompile(handle);
        return Truncate(SyntaxTreeToString(syntaxTree));
    }

    private static string SyntaxTreeToString(ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree syntaxTree)
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        syntaxTree.AcceptVisitor(new CSharpOutputVisitor(writer, FormattingOptionsFactory.CreateAllman()));
        return sb.ToString();
    }

    public string GetIL(CachedAssembly cached, IMethod method)
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        var output = new PlainTextOutput(writer);
        var disassembler = new ReflectionDisassembler(output, CancellationToken.None);
        var handle = (MethodDefinitionHandle)method.MetadataToken;
        disassembler.DisassembleMethod(cached.PEFile, handle);
        return Truncate(sb.ToString());
    }

    // ── Type enumeration ─────────────────────────────────────────────────────

    public IEnumerable<(string Namespace, string Name, string Kind)> EnumerateTypes(CachedAssembly cached, string? nsFilter)
    {
        var meta = cached.PEFile.Metadata;
        var ts = cached.TypeSystem;

        foreach (var tdHandle in meta.TypeDefinitions)
        {
            var td = meta.GetTypeDefinition(tdHandle);
            var ns = meta.GetString(td.Namespace);
            var name = meta.GetString(td.Name);

            if (!string.IsNullOrEmpty(nsFilter) &&
                !ns.StartsWith(nsFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip compiler-generated types
            if (name.Contains('<') || name.Contains('>')) continue;

            var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            ITypeDefinition? typeDef = null;
            try { typeDef = ts.FindType(new FullTypeName(fullName)).GetDefinition(); } catch { }

            var kind = typeDef?.Kind switch
            {
                TypeKind.Struct => "struct",
                TypeKind.Enum => "enum",
                TypeKind.Interface => "interface",
                TypeKind.Delegate => "delegate",
                _ => "class"
            };

            yield return (ns, name, kind);
        }
    }

    // ── Truncation ───────────────────────────────────────────────────────────

    public static string TruncateLines(string text, int maxLines)
    {
        var lines = text.Split('\n');
        if (lines.Length <= maxLines) return Truncate(text);
        var truncated = string.Join('\n', lines.Take(maxLines));
        return Truncate(truncated + TruncationSuffix);
    }

    public static string Truncate(string text)
    {
        if (text.Length <= MaxOutputChars) return text;
        return text[..MaxOutputChars] + TruncationSuffix;
    }

    public void Dispose()
    {
        foreach (var cached in _cache.Values)
            cached.PEFile.Dispose();
        _cache.Clear();
    }
}
