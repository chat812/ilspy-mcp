using System.Reflection.Metadata;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.Data.Sqlite;

namespace DotNetDecompilerMcp.Services;

public record TypeRecord(string Namespace, string Name, string FullName, string Kind);
public record MemberRecord(string TypeName, string MemberType, string Name, string Signature, string Access, bool IsStatic);
public record ReferenceRecord(string ContainingType, string Method);

public sealed class DatabaseService : IDisposable
{
    private readonly string _connectionString;

    public DatabaseService()
    {
        // Each project gets its own DB in its working directory.
        // The MCP server is launched from the project root by Claude Code,
        // so Directory.GetCurrentDirectory() is the project directory.
        var dir = Path.Combine(Directory.GetCurrentDirectory(), ".dotnet-decompiler");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "index.db");
        _connectionString = $"Data Source={dbPath}";
        EnsureSchema();
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous  = NORMAL;

            CREATE TABLE IF NOT EXISTS assemblies (
                id         INTEGER PRIMARY KEY,
                path       TEXT    NOT NULL UNIQUE,
                name       TEXT    NOT NULL,
                version    TEXT,
                framework  TEXT,
                file_mtime INTEGER NOT NULL,
                indexed_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS types (
                id          INTEGER PRIMARY KEY,
                assembly_id INTEGER NOT NULL,
                namespace   TEXT    NOT NULL DEFAULT '',
                name        TEXT    NOT NULL,
                full_name   TEXT    NOT NULL,
                kind        TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_types_assembly  ON types(assembly_id);
            CREATE INDEX IF NOT EXISTS idx_types_name      ON types(name      COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_types_full_name ON types(full_name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS members (
                id          INTEGER PRIMARY KEY,
                assembly_id INTEGER NOT NULL,
                type_id     INTEGER NOT NULL,
                member_type TEXT    NOT NULL,
                name        TEXT    NOT NULL,
                signature   TEXT,
                access      TEXT,
                is_static   INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_members_assembly ON members(assembly_id);
            CREATE INDEX IF NOT EXISTS idx_members_name     ON members(name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS refs (
                id            INTEGER PRIMARY KEY,
                assembly_id   INTEGER NOT NULL,
                caller_type   TEXT    NOT NULL,
                caller_method TEXT    NOT NULL,
                target_type   TEXT    NOT NULL,
                target_member TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_refs_target        ON refs(assembly_id, target_type);
            CREATE INDEX IF NOT EXISTS idx_refs_target_member ON refs(assembly_id, target_type, target_member);
            """;
        cmd.ExecuteNonQuery();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsUpToDate(string assemblyPath)
    {
        if (!File.Exists(assemblyPath)) return false;
        var mtime = ToUnix(File.GetLastWriteTimeUtc(assemblyPath));
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_mtime FROM assemblies WHERE path = @p";
        cmd.Parameters.AddWithValue("@p", assemblyPath);
        return cmd.ExecuteScalar() is long stored && stored == mtime;
    }

    /// <summary>Index if stale; no-op if already up to date.</summary>
    public bool EnsureIndexed(string assemblyPath, CachedAssembly cached)
    {
        if (IsUpToDate(assemblyPath)) return false;
        IndexAssembly(assemblyPath, cached);
        return true;
    }

    public (int types, int members, int refs) IndexAssembly(string assemblyPath, CachedAssembly cached)
    {
        var meta = cached.PEFile.Metadata;
        var ts   = cached.TypeSystem;
        var mtime = ToUnix(File.GetLastWriteTimeUtc(assemblyPath));
        var now   = ToUnix(DateTime.UtcNow);

        var asmDef  = meta.GetAssemblyDefinition();
        var asmName = meta.GetString(asmDef.Name);
        var version = asmDef.Version.ToString();
        var framework = cached.PEFile.DetectTargetFrameworkId() ?? "unknown";

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // ── Remove stale data ────────────────────────────────────────────────
        long asmId;
        {
            using var del = Cmd(conn, tx,
                "DELETE FROM refs    WHERE assembly_id = (SELECT id FROM assemblies WHERE path = @p);" +
                "DELETE FROM members WHERE assembly_id = (SELECT id FROM assemblies WHERE path = @p);" +
                "DELETE FROM types   WHERE assembly_id = (SELECT id FROM assemblies WHERE path = @p);" +
                "DELETE FROM assemblies WHERE path = @p;");
            del.Parameters.AddWithValue("@p", assemblyPath);
            del.ExecuteNonQuery();

            using var ins = Cmd(conn, tx, """
                INSERT INTO assemblies (path, name, version, framework, file_mtime, indexed_at)
                VALUES (@p, @n, @v, @f, @m, @now);
                SELECT last_insert_rowid();
                """);
            ins.Parameters.AddWithValue("@p", assemblyPath);
            ins.Parameters.AddWithValue("@n", asmName);
            ins.Parameters.AddWithValue("@v", version);
            ins.Parameters.AddWithValue("@f", framework);
            ins.Parameters.AddWithValue("@m", mtime);
            ins.Parameters.AddWithValue("@now", now);
            asmId = (long)ins.ExecuteScalar()!;
        }

        // ── Types + members ──────────────────────────────────────────────────
        int typeCount = 0, memberCount = 0;
        var typeIdMap = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var tdHandle in meta.TypeDefinitions)
        {
            var td   = meta.GetTypeDefinition(tdHandle);
            var ns   = meta.GetString(td.Namespace);
            var name = meta.GetString(td.Name);
            if (name.Contains('<') || name.Contains('>')) continue;

            var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

            ITypeDefinition? typeDef = null;
            try { typeDef = ts.FindType(new FullTypeName(fullName)).GetDefinition(); } catch { }

            var kind = typeDef?.Kind switch
            {
                TypeKind.Struct    => "struct",
                TypeKind.Enum      => "enum",
                TypeKind.Interface => "interface",
                TypeKind.Delegate  => "delegate",
                _                  => "class"
            };

            long typeId;
            using (var ins = Cmd(conn, tx, """
                INSERT INTO types (assembly_id, namespace, name, full_name, kind)
                VALUES (@a, @ns, @n, @fn, @k);
                SELECT last_insert_rowid();
                """))
            {
                ins.Parameters.AddWithValue("@a",  asmId);
                ins.Parameters.AddWithValue("@ns", ns);
                ins.Parameters.AddWithValue("@n",  name);
                ins.Parameters.AddWithValue("@fn", fullName);
                ins.Parameters.AddWithValue("@k",  kind);
                typeId = (long)ins.ExecuteScalar()!;
            }
            typeIdMap[fullName] = typeId;
            typeCount++;

            if (typeDef == null) continue;

            foreach (var m in typeDef.Methods)
            {
                if (m.Name.Contains('<') || m.Name.Contains('>')) continue;
                InsertMember(conn, tx, asmId, typeId, "method", m.Name,
                    m.ToString(), Access(m.Accessibility), m.IsStatic);
                memberCount++;
            }
            foreach (var p in typeDef.Properties)
            {
                InsertMember(conn, tx, asmId, typeId, "property", p.Name,
                    $"{p.ReturnType.Name} {p.Name}", Access(p.Accessibility), p.IsStatic);
                memberCount++;
            }
            foreach (var f in typeDef.Fields)
            {
                if (f.Name.Contains('<') || f.Name.Contains('>')) continue;
                InsertMember(conn, tx, asmId, typeId, "field", f.Name,
                    $"{f.Type.Name} {f.Name}", Access(f.Accessibility), f.IsStatic);
                memberCount++;
            }
            foreach (var e in typeDef.Events)
            {
                InsertMember(conn, tx, asmId, typeId, "event", e.Name,
                    $"event {e.ReturnType.Name} {e.Name}", Access(e.Accessibility), e.IsStatic);
                memberCount++;
            }
        }

        // ── References (IL scan) ─────────────────────────────────────────────
        int refCount = IndexRefs(conn, tx, asmId, cached, meta);

        tx.Commit();
        return (typeCount, memberCount, refCount);
    }

    // ── Query methods ─────────────────────────────────────────────────────────

    public (int total, List<TypeRecord> page) GetTypes(
        string assemblyPath, string? nsFilter, int skip, int take)
    {
        using var conn = Open();
        var asmId = AssemblyId(conn, assemblyPath);
        if (asmId == null) return (0, []);

        var nsClause = nsFilter != null ? "AND namespace LIKE @ns ESCAPE '\\'" : "";

        using var cntCmd = Cmd(conn, null,
            $"SELECT COUNT(*) FROM types WHERE assembly_id = @a {nsClause}");
        cntCmd.Parameters.AddWithValue("@a", asmId);
        if (nsFilter != null) cntCmd.Parameters.AddWithValue("@ns", Like(nsFilter) + "%");
        var total = (int)(long)cntCmd.ExecuteScalar()!;

        using var cmd = Cmd(conn, null, $"""
            SELECT namespace, name, full_name, kind
            FROM types WHERE assembly_id = @a {nsClause}
            ORDER BY full_name LIMIT @take OFFSET @skip
            """);
        cmd.Parameters.AddWithValue("@a",    asmId);
        cmd.Parameters.AddWithValue("@take", take);
        cmd.Parameters.AddWithValue("@skip", skip);
        if (nsFilter != null) cmd.Parameters.AddWithValue("@ns", Like(nsFilter) + "%");

        var page = new List<TypeRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            page.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return (total, page);
    }

    public List<string> GetNamespaces(string assemblyPath)
    {
        using var conn = Open();
        var asmId = AssemblyId(conn, assemblyPath);
        if (asmId == null) return [];
        using var cmd = Cmd(conn, null,
            "SELECT DISTINCT namespace FROM types WHERE assembly_id = @a AND namespace != '' ORDER BY namespace");
        cmd.Parameters.AddWithValue("@a", asmId);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public List<MemberRecord> GetMembers(string assemblyPath, string typeName)
    {
        using var conn = Open();
        var asmId = AssemblyId(conn, assemblyPath);
        if (asmId == null) return [];
        using var cmd = Cmd(conn, null, """
            SELECT t.full_name, m.member_type, m.name, m.signature, m.access, m.is_static
            FROM members m JOIN types t ON t.id = m.type_id
            WHERE m.assembly_id = @a AND t.full_name = @tn COLLATE NOCASE
            ORDER BY m.member_type, m.name
            """);
        cmd.Parameters.AddWithValue("@a",  asmId);
        cmd.Parameters.AddWithValue("@tn", typeName);
        return ReadMembers(cmd);
    }

    public List<TypeRecord> SearchTypes(string assemblyPath, string query, int max)
    {
        using var conn = Open();
        var asmId = AssemblyId(conn, assemblyPath);
        if (asmId == null) return [];
        using var cmd = Cmd(conn, null, """
            SELECT namespace, name, full_name, kind
            FROM types
            WHERE assembly_id = @a AND (name LIKE @q ESCAPE '\' OR full_name LIKE @q ESCAPE '\')
            ORDER BY full_name LIMIT @max
            """);
        cmd.Parameters.AddWithValue("@a",   asmId);
        cmd.Parameters.AddWithValue("@q",   "%" + Like(query) + "%");
        cmd.Parameters.AddWithValue("@max", max);
        var list = new List<TypeRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public List<MemberRecord> SearchMembers(string assemblyPath, string query, int max)
    {
        using var conn = Open();
        var asmId = AssemblyId(conn, assemblyPath);
        if (asmId == null) return [];
        using var cmd = Cmd(conn, null, """
            SELECT t.full_name, m.member_type, m.name, m.signature, m.access, m.is_static
            FROM members m JOIN types t ON t.id = m.type_id
            WHERE m.assembly_id = @a AND m.name LIKE @q ESCAPE '\'
            ORDER BY t.full_name, m.name LIMIT @max
            """);
        cmd.Parameters.AddWithValue("@a",   asmId);
        cmd.Parameters.AddWithValue("@q",   "%" + Like(query) + "%");
        cmd.Parameters.AddWithValue("@max", max);
        return ReadMembers(cmd);
    }

    public List<ReferenceRecord> FindReferences(
        string assemblyPath, string targetType, string? memberName)
    {
        using var conn = Open();
        var asmId = AssemblyId(conn, assemblyPath);
        if (asmId == null) return [];

        var sql = memberName == null
            ? "SELECT DISTINCT caller_type, caller_method FROM refs WHERE assembly_id = @a AND target_type = @tt ORDER BY caller_type, caller_method"
            : "SELECT DISTINCT caller_type, caller_method FROM refs WHERE assembly_id = @a AND target_type = @tt AND target_member = @tm ORDER BY caller_type, caller_method";

        using var cmd = Cmd(conn, null, sql);
        cmd.Parameters.AddWithValue("@a",  asmId);
        cmd.Parameters.AddWithValue("@tt", targetType);
        if (memberName != null) cmd.Parameters.AddWithValue("@tm", memberName);

        var list = new List<ReferenceRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1)));
        return list;
    }

    // ── IL reference indexer ──────────────────────────────────────────────────

    private static int IndexRefs(
        SqliteConnection conn, SqliteTransaction tx,
        long asmId, CachedAssembly cached, MetadataReader meta)
    {
        // Build token → (targetType, targetMember?) maps using row-number arithmetic
        // (avoids MetadataTokens which is inaccessible in this build).
        var mrMap = new Dictionary<int, (string Type, string Member)>();
        var trMap = new Dictionary<int, string>();

        int row = 0;
        foreach (var mrh in meta.MemberReferences)
        {
            row++;
            try
            {
                var mr = meta.GetMemberReference(mrh);
                if (mr.Parent.Kind != HandleKind.TypeReference) continue;
                var tr = meta.GetTypeReference((TypeReferenceHandle)mr.Parent);
                var ns   = meta.GetString(tr.Namespace);
                var name = meta.GetString(tr.Name);
                var typeName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                mrMap[unchecked((int)(0x0A000000u | (uint)row))] =
                    (typeName, meta.GetString(mr.Name));
            }
            catch { }
        }

        row = 0;
        foreach (var trh in meta.TypeReferences)
        {
            row++;
            try
            {
                var tr   = meta.GetTypeReference(trh);
                var ns   = meta.GetString(tr.Namespace);
                var name = meta.GetString(tr.Name);
                trMap[unchecked((int)(0x01000000u | (uint)row))] =
                    string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
            catch { }
        }

        int refCount = 0;
        var seen = new HashSet<(string, string, string, string?)>();

        foreach (var tdHandle in meta.TypeDefinitions)
        {
            var td       = meta.GetTypeDefinition(tdHandle);
            var ns       = meta.GetString(td.Namespace);
            var typeName = meta.GetString(td.Name);
            if (typeName.Contains('<') || typeName.Contains('>')) continue;
            var callerType = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";

            foreach (var mhHandle in td.GetMethods())
            {
                var methodDef  = meta.GetMethodDefinition(mhHandle);
                var methodName = meta.GetString(methodDef.Name);
                if (methodName.Contains('<') || methodName.Contains('>')) continue;
                if (methodDef.RelativeVirtualAddress == 0) continue;

                var found = new HashSet<(string, string?)>();
                try
                {
                    var body      = cached.PEFile.Reader.GetMethodBody(methodDef.RelativeVirtualAddress);
                    var ilReader  = body.GetILReader();

                    while (ilReader.RemainingBytes > 0)
                    {
                        var op = ilReader.ReadByte();

                        if (op == 0xFE) // two-byte prefix
                        {
                            if (ilReader.RemainingBytes == 0) break;
                            var op2 = ilReader.ReadByte();
                            // constrained (0x16) and initobj (0x15) carry a type token
                            if ((op2 == 0x15 || op2 == 0x16) && ilReader.RemainingBytes >= 4)
                            {
                                var tok = ilReader.ReadInt32();
                                if (trMap.TryGetValue(tok, out var tname))
                                    found.Add((tname, null));
                            }
                            continue;
                        }

                        if (ILScanner.HasTokenOperand(op))
                        {
                            var tok = ilReader.ReadInt32();
                            if (mrMap.TryGetValue(tok, out var mr))
                                found.Add((mr.Type, mr.Member));
                            else if (trMap.TryGetValue(tok, out var tname))
                                found.Add((tname, null));
                        }
                        else
                        {
                            ILScanner.SkipOperand(ref ilReader, op);
                        }
                    }
                }
                catch { }

                foreach (var (targetType, targetMember) in found)
                {
                    if (!seen.Add((callerType, methodName, targetType, targetMember))) continue;

                    using var ins = Cmd(conn, tx, """
                        INSERT INTO refs (assembly_id, caller_type, caller_method, target_type, target_member)
                        VALUES (@a, @ct, @cm, @tt, @tm)
                        """);
                    ins.Parameters.AddWithValue("@a",  asmId);
                    ins.Parameters.AddWithValue("@ct", callerType);
                    ins.Parameters.AddWithValue("@cm", methodName);
                    ins.Parameters.AddWithValue("@tt", targetType);
                    ins.Parameters.AddWithValue("@tm", (object?)targetMember ?? DBNull.Value);
                    ins.ExecuteNonQuery();
                    refCount++;
                }
            }
        }

        return refCount;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static SqliteCommand Cmd(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (tx != null) cmd.Transaction = tx;
        return cmd;
    }

    private static long? AssemblyId(SqliteConnection conn, string path)
    {
        using var cmd = Cmd(conn, null, "SELECT id FROM assemblies WHERE path = @p");
        cmd.Parameters.AddWithValue("@p", path);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    private static void InsertMember(SqliteConnection conn, SqliteTransaction tx,
        long asmId, long typeId, string mtype, string name, string? sig, string access, bool isStatic)
    {
        using var cmd = Cmd(conn, tx, """
            INSERT INTO members (assembly_id, type_id, member_type, name, signature, access, is_static)
            VALUES (@a, @t, @mt, @n, @s, @acc, @st)
            """);
        cmd.Parameters.AddWithValue("@a",   asmId);
        cmd.Parameters.AddWithValue("@t",   typeId);
        cmd.Parameters.AddWithValue("@mt",  mtype);
        cmd.Parameters.AddWithValue("@n",   name);
        cmd.Parameters.AddWithValue("@s",   (object?)sig ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@acc", access);
        cmd.Parameters.AddWithValue("@st",  isStatic ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static List<MemberRecord> ReadMembers(SqliteCommand cmd)
    {
        var list = new List<MemberRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new(
                r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? "" : r.GetString(4),
                r.GetInt32(5) != 0));
        return list;
    }

    private static string Access(ICSharpCode.Decompiler.TypeSystem.Accessibility a) => a switch
    {
        ICSharpCode.Decompiler.TypeSystem.Accessibility.Public             => "public",
        ICSharpCode.Decompiler.TypeSystem.Accessibility.Protected          => "protected",
        ICSharpCode.Decompiler.TypeSystem.Accessibility.Internal           => "internal",
        ICSharpCode.Decompiler.TypeSystem.Accessibility.ProtectedOrInternal  => "protected internal",
        ICSharpCode.Decompiler.TypeSystem.Accessibility.ProtectedAndInternal => "private protected",
        _                                                                   => "private"
    };

    private static string Like(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

    public void Dispose() { }
}
