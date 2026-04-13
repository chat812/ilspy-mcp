using System.Reflection.Metadata;

namespace DotNetDecompilerMcp.Services;

/// <summary>
/// Shared IL opcode helpers for scanning method bodies. Used by both the live
/// FindReferences tool and the DatabaseService indexer so the logic stays in sync.
/// </summary>
internal static class ILScanner
{
    /// <summary>Returns true for single-byte opcodes whose 4-byte operand is a metadata token.</summary>
    public static bool HasTokenOperand(byte opcode) => opcode switch
    {
        // call, calli, callvirt, newobj, castclass, isinst, unbox, unbox.any
        0x27 or 0x28 or 0x29 or 0x6F or 0x73 or 0x74 or 0x75 or 0x79 or 0xA5 or
        // stobj, box
        0x81 or 0x82 or
        // ldfld, ldflda, stfld, ldsfld, ldsflda, stsfld
        0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80 or
        // newarr, ldelema, sizeof, ldtoken
        0x8D or 0xA3 or 0x8C or 0xD0 or
        // ldstr (UserString token 0x70xxxxxx)
        0x72 => true,
        _ => false,
    };

    public static void SkipOperand(ref BlobReader reader, byte opcode)
    {
        switch (opcode)
        {
            case 0x20: reader.ReadInt32(); break;           // ldc.i4
            case 0x21: reader.ReadInt64(); break;           // ldc.i8
            case 0x22: reader.ReadSingle(); break;          // ldc.r4
            case 0x23: reader.ReadDouble(); break;          // ldc.r8
            case 0x11 or 0x12 or 0x13
              or 0x0E or 0x0F or 0x10: reader.ReadByte(); break;  // s-variants
            case 0x2C or 0x2D
              or 0x2E or 0x2F or 0x30 or 0x31 or 0x32
              or 0x33 or 0x34 or 0x35 or 0x36 or 0x37: reader.ReadSByte(); break; // short branches
            case 0x38 or 0x39 or 0x3A
              or 0x3B or 0x3C or 0x3D or 0x3E or 0x3F
              or 0x40 or 0x41 or 0x42 or 0x43 or 0x44: reader.ReadInt32(); break; // long branches
            case 0x45: // switch — 4-byte count + count×4-byte offsets
                var n = reader.ReadUInt32();
                for (var i = 0u; i < n && reader.RemainingBytes >= 4; i++) reader.ReadInt32();
                break;
            // everything else has no operand (or is unknown — stay put and hope for the best)
        }
    }
}
