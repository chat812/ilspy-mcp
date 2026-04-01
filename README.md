# DotNetDecompilerMcp

An MCP server that lets Claude Code inspect and decompile .NET assemblies (DLLs/EXEs) using ICSharpCode.Decompiler (ILSpy's engine).

## Claude Code Integration

Add to your `.claude/settings.json` (or `settings.local.json`):

```json
{
  "mcpServers": {
    "dotnet-decompiler": {
      "command": "D:\\vibecode\\ilspy-mcp\\publish\\DotNetDecompilerMcp.exe"
    }
  }
}
```

## Tools

### Assembly
| Tool | Description |
|---|---|
| `load_assembly` | Load a DLL/EXE, return name/version/framework/type count |
| `get_assembly_info` | Assembly metadata: entry point, attributes, module info |
| `list_dependencies` | All referenced assemblies with versions |
| `list_resources` | Embedded resources list |
| `get_resource` | Extract embedded resource (UTF-8 or base64) |

### Types
| Tool | Description |
|---|---|
| `list_namespaces` | All distinct namespaces in the assembly |
| `list_types` | All types with optional namespace filter + skip/take pagination |
| `decompile_type` | Full C# decompilation of a type (maxLines param, default 500) |
| `get_type_hierarchy` | Base classes and implemented interfaces |
| `get_type_dependencies` | Types a given type depends on |

### Members
| Tool | Description |
|---|---|
| `list_members` | All members of a type (no body decompilation) |
| `decompile_method` | Decompile a single method to C#; use parameterTypes for overloads |
| `get_il` | Raw IL disassembly of a method |

### Search & Navigation
| Tool | Description |
|---|---|
| `search_types` | Substring search across type names |
| `search_members` | Substring search across all member names |
| `find_references` | Find which methods use a given type or member |

## Rebuild

If you modify the source:

```bash
cd DotNetDecompilerMcp
dotnet publish -c Release -o ../publish
```
