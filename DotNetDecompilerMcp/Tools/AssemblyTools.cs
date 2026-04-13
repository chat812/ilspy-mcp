using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;
using DotNetDecompilerMcp.Services;

namespace DotNetDecompilerMcp.Tools;

[McpServerToolType]
public sealed class AssemblyTools(DecompilerService svc, DatabaseService db)
{
    /// <summary>
    /// Load a .NET assembly (DLL or EXE) into the decompiler cache. Returns assembly name,
    /// version, target framework, and type count. Assemblies stay cached for the session.
    /// </summary>
    [McpServerTool(Name = "load_assembly")]
    [Description("Load a .NET assembly (DLL/EXE) by file path. Returns name, version, target framework, type count.")]
    public string LoadAssembly(
        [Description("Absolute or relative path to the .NET DLL or EXE.")] string assemblyPath)
    {
        try
        {
            var absPath = Path.GetFullPath(assemblyPath);
            var cached  = svc.LoadAssembly(absPath);
            var meta    = cached.PEFile.Metadata;
            var asmDef  = meta.GetAssemblyDefinition();

            var name      = meta.GetString(asmDef.Name);
            var version   = asmDef.Version.ToString();
            var framework = cached.PEFile.DetectTargetFrameworkId() ?? "unknown";
            var typeCount = meta.TypeDefinitions.Count;

            // Auto-index into SQLite for fast subsequent queries.
            var freshlyIndexed = db.EnsureIndexed(absPath, cached);

            return JsonSerializer.Serialize(new
            {
                success = true,
                assemblyPath = absPath,
                name,
                version,
                targetFramework = framework,
                typeCount,
                indexed = freshlyIndexed ? "built" : "cached",
            });
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Returns assembly-level metadata: entry point, target framework, strong name status,
    /// module info, and all custom attributes declared on the assembly.
    /// </summary>
    [McpServerTool(Name = "get_assembly_info")]
    [Description("Get assembly-level metadata: entry point, target framework, strong name, module info, custom attributes.")]
    public string GetAssemblyInfo(
        [Description("Path to the .NET assembly.")] string assemblyPath)
    {
        try
        {
            var cached = svc.LoadAssembly(assemblyPath);
            var pefile = cached.PEFile;
            var meta = pefile.Metadata;
            var asmDef = meta.GetAssemblyDefinition();

            var name = meta.GetString(asmDef.Name);
            var version = asmDef.Version.ToString();
            var framework = pefile.DetectTargetFrameworkId() ?? "unknown";
            var hasStrongName = (asmDef.Flags & System.Reflection.AssemblyFlags.PublicKey) != 0;

            // Entry point
            string? entryPoint = null;
            var corHeader = pefile.Reader.PEHeaders.CorHeader;
            if (corHeader != null && corHeader.EntryPointTokenOrRelativeVirtualAddress != 0)
            {
                var epToken = corHeader.EntryPointTokenOrRelativeVirtualAddress;
                // If it's a MethodDef token (0x06xxxxxx)
                if ((epToken >> 24) == 0x06)
                {
                    try
                    {
                        // MetadataTokens unavailable — resolve handle by iterating the
                        // MethodDefinitions table (row numbers are 1-based sequential).
                        var targetRow = epToken & 0x00FFFFFF;
                        int row = 0;
                        foreach (var mh in meta.MethodDefinitions)
                        {
                            row++;
                            if (row != targetRow) continue;
                            var methodDef = meta.GetMethodDefinition(mh);
                            var methodName = meta.GetString(methodDef.Name);
                            var declType = meta.GetTypeDefinition(methodDef.GetDeclaringType());
                            var tn = meta.GetString(declType.Name);
                            var tns = meta.GetString(declType.Namespace);
                            entryPoint = string.IsNullOrEmpty(tns) ? $"{tn}.{methodName}" : $"{tns}.{tn}.{methodName}";
                            break;
                        }
                    }
                    catch { /* non-critical — ignore malformed EP token */ }
                }
            }

            // Module info
            var moduleDef = meta.GetModuleDefinition();
            var moduleName = meta.GetString(moduleDef.Name);

            // Custom attributes on assembly
            var customAttrs = new List<string>();
            foreach (var attrHandle in asmDef.GetCustomAttributes())
            {
                var attr = meta.GetCustomAttribute(attrHandle);
                string attrTypeName;
                if (attr.Constructor.Kind == HandleKind.MemberReference)
                {
                    var memberRef = meta.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                    if (memberRef.Parent.Kind == HandleKind.TypeReference)
                    {
                        var typeRef = meta.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                        attrTypeName = meta.GetString(typeRef.Name);
                    }
                    else attrTypeName = "UnknownAttribute";
                }
                else if (attr.Constructor.Kind == HandleKind.MethodDefinition)
                {
                    var methodDef = meta.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
                    var typeDef = meta.GetTypeDefinition(methodDef.GetDeclaringType());
                    attrTypeName = meta.GetString(typeDef.Name);
                }
                else attrTypeName = "UnknownAttribute";

                customAttrs.Add(attrTypeName);
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                assemblyPath = Path.GetFullPath(assemblyPath),
                name,
                version,
                targetFramework = framework,
                hasStrongName,
                entryPoint,
                moduleName,
                customAttributes = customAttrs,
            });
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// List all assemblies referenced by this assembly (its direct dependencies),
    /// including name and version for each.
    /// </summary>
    [McpServerTool(Name = "list_dependencies")]
    [Description("List all assemblies referenced by this assembly. Returns name and version for each.")]
    public string ListDependencies(
        [Description("Path to the .NET assembly.")] string assemblyPath)
    {
        try
        {
            var cached = svc.LoadAssembly(assemblyPath);
            var meta = cached.PEFile.Metadata;

            var refs = meta.AssemblyReferences
                .Select(h =>
                {
                    var r = meta.GetAssemblyReference(h);
                    return new
                    {
                        name = meta.GetString(r.Name),
                        version = r.Version.ToString(),
                    };
                })
                .OrderBy(r => r.name)
                .ToList();

            return JsonSerializer.Serialize(new { success = true, dependencies = refs });
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// List all embedded resources in the assembly. Returns resource name and size in bytes.
    /// </summary>
    [McpServerTool(Name = "list_resources")]
    [Description("List all embedded resources in the assembly. Returns name and size for each.")]
    public string ListResources(
        [Description("Path to the .NET assembly.")] string assemblyPath)
    {
        try
        {
            var cached = svc.LoadAssembly(assemblyPath);
            var meta = cached.PEFile.Metadata;

            var resources = new List<object>();
            foreach (var rHandle in meta.ManifestResources)
            {
                var r = meta.GetManifestResource(rHandle);
                var rName = meta.GetString(r.Name);
                // Size is approximate — we'd need to read the blob to get exact size
                resources.Add(new { name = rName, offset = r.Offset });
            }

            return JsonSerializer.Serialize(new { success = true, resources });
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Extract an embedded resource by name. Returns UTF-8 text if the content appears
    /// to be text, otherwise returns base64-encoded binary content.
    /// </summary>
    [McpServerTool(Name = "get_resource")]
    [Description("Extract an embedded resource by name. Returns UTF-8 text or base64 for binary content.")]
    public string GetResource(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Exact name of the embedded resource.")] string resourceName)
    {
        try
        {
            var cached = svc.LoadAssembly(assemblyPath);
            var pefile = cached.PEFile;
            var meta = pefile.Metadata;

            ManifestResource? found = null;
            foreach (var rHandle in meta.ManifestResources)
            {
                var r = meta.GetManifestResource(rHandle);
                if (string.Equals(meta.GetString(r.Name), resourceName, StringComparison.OrdinalIgnoreCase))
                {
                    found = r;
                    break;
                }
            }

            if (found == null)
                return Error($"Resource '{resourceName}' not found.");

            // Read resource data via the underlying PEReader
            var peCorHeader = pefile.Reader.PEHeaders.CorHeader;
            if (peCorHeader == null)
                return Error("Assembly has no CLR header.");

            var resourcesRva = peCorHeader.ResourcesDirectory.RelativeVirtualAddress;
            var sectionData = pefile.Reader.GetSectionData(resourcesRva);
            if (sectionData.Length == 0)
                return Error("Could not read resource section.");

            var resourceReader = sectionData.GetReader();
            resourceReader.Offset = (int)found.Value.Offset;
            var length = resourceReader.ReadInt32();
            var bytes = resourceReader.ReadBytes(length);

            // Detect if text
            bool isText = bytes.Length > 0 && IsUtf8Text(bytes);
            if (isText)
            {
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                return JsonSerializer.Serialize(new { success = true, resourceName, encoding = "utf-8", content = DecompilerService.Truncate(text) });
            }
            else
            {
                var b64 = Convert.ToBase64String(bytes);
                return JsonSerializer.Serialize(new { success = true, resourceName, encoding = "base64", content = DecompilerService.Truncate(b64) });
            }
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Decompile types in the assembly to C#. Limited to maxTypes (max 10) for performance.
    /// Use namespaceFilter to scope to a specific namespace.
    /// </summary>
    [McpServerTool(Name = "decompile_assembly")]
    [Description("Decompile types in the assembly to C#. Limited to 10 types per call. Use namespaceFilter to scope.")]
    public string DecompileAssembly(
        [Description("Path to the .NET assembly.")] string assemblyPath,
        [Description("Optional namespace prefix to filter types.")] string? namespaceFilter = null,
        [Description("Maximum number of types to decompile (default 10, max 10).")] int maxTypes = 10)
    {
        try
        {
            maxTypes = Math.Clamp(maxTypes, 1, 10);
            var cached = svc.LoadAssembly(assemblyPath);
            var types  = svc.EnumerateTypes(cached, namespaceFilter).Take(maxTypes).ToList();

            var results = new List<object>();
            foreach (var (ns, name, kind) in types)
            {
                var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                var typeDef  = svc.FindType(cached, fullName);
                if (typeDef == null) continue;
                var source = svc.DecompileType(cached, typeDef, 200);
                results.Add(new { fullName, kind, source });
            }

            return JsonSerializer.Serialize(new
            {
                success          = true,
                namespaceFilter,
                totalDecompiled  = results.Count,
                types            = results,
            });
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    /// <summary>
    /// Get PE file metadata: MVID, CLR runtime version, PE sections, image characteristics.
    /// </summary>
    [McpServerTool(Name = "get_metadata")]
    [Description("Get PE metadata: MVID, CLR runtime version, PE sections, image base, architecture.")]
    public string GetMetadata(
        [Description("Path to the .NET assembly.")] string assemblyPath)
    {
        try
        {
            var cached  = svc.LoadAssembly(assemblyPath);
            var pefile  = cached.PEFile;
            var meta    = pefile.Metadata;
            var headers = pefile.Reader.PEHeaders;

            // MVID from module definition
            var moduleDef = meta.GetModuleDefinition();
            var mvid      = meta.GetGuid(moduleDef.Mvid).ToString("D").ToUpperInvariant();

            // CLR runtime version
            var corHeader      = headers.CorHeader;
            var runtimeVersion = corHeader != null
                ? $"{corHeader.MajorRuntimeVersion}.{corHeader.MinorRuntimeVersion}"
                : "unknown";

            // PE sections
            var sections = headers.SectionHeaders.Select(s => new
            {
                name           = s.Name,
                virtualAddress = s.VirtualAddress,
                virtualSize    = s.VirtualSize,
                rawDataOffset  = s.PointerToRawData,
                rawDataSize    = s.SizeOfRawData,
            }).ToList();

            // Architecture and image properties
            var coffHeader = headers.CoffHeader;
            var peHeader   = headers.PEHeader;
            var isDll      = coffHeader != null &&
                             (coffHeader.Characteristics & Characteristics.Dll) != 0;
            var is64Bit    = peHeader?.Magic == PEMagic.PE32Plus;

            return JsonSerializer.Serialize(new
            {
                success        = true,
                assemblyPath   = Path.GetFullPath(assemblyPath),
                mvid,
                runtimeVersion,
                imageBase      = peHeader?.ImageBase,
                subsystem      = peHeader?.Subsystem.ToString(),
                isDll,
                is64Bit,
                sectionCount   = sections.Count,
                sections,
            });
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    private static bool IsUtf8Text(byte[] bytes)
    {
        // Reject if >30% non-printable non-whitespace bytes
        int suspicious = 0;
        foreach (var b in bytes.Take(512))
        {
            if (b < 0x09 || (b >= 0x0E && b < 0x20 && b != 0x1B))
                suspicious++;
        }
        return suspicious < bytes.Take(512).Count() * 0.3;
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { success = false, error = message });
}
