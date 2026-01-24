using System.Runtime.CompilerServices;

namespace Lite3DotNet;

public static unsafe partial class Lite3Core
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Node* GetNodePtr(ReadOnlySpan<byte> buffer, int offset)
    {
        fixed (byte* p = buffer)
            return (Node*)(p + offset);
    }
}