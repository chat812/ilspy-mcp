#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/DotNetDecompilerMcp/DotNetDecompilerMcp.csproj"
OUT="$SCRIPT_DIR/publish"

CONFIGURATION="Release"
RID="linux-x64"
CLEAN=false

usage() {
    echo "Usage: $0 [options]"
    echo ""
    echo "Options:"
    echo "  -c, --clean         Clean before build"
    echo "  -d, --debug         Build Debug instead of Release"
    echo "  -r, --rid <rid>     Runtime identifier (default: linux-x64)"
    echo "                      Examples: linux-x64, linux-arm64, osx-x64, osx-arm64"
    echo "  -o, --output <dir>  Output directory (default: ./publish)"
    echo "  -h, --help          Show this help"
    exit 0
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--clean)    CLEAN=true; shift ;;
        -d|--debug)    CONFIGURATION="Debug"; shift ;;
        -r|--rid)      RID="$2"; shift 2 ;;
        -o|--output)   OUT="$2"; shift 2 ;;
        -h|--help)     usage ;;
        *) echo "Unknown option: $1"; usage ;;
    esac
done

echo "Configuration : $CONFIGURATION"
echo "Runtime ID    : $RID"
echo "Output        : $OUT"

if $CLEAN; then
    echo "Cleaning..."
    dotnet clean "$PROJECT" -c "$CONFIGURATION" -r "$RID"
fi

echo "Publishing..."
dotnet publish "$PROJECT" \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained false \
    -o "$OUT" \
    -p:UseAppHost=true

echo ""
echo "Done. Binary: $OUT/DotNetDecompilerMcp"
