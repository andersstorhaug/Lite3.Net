using System.Runtime.CompilerServices;

namespace TronDotNet;

public static unsafe partial class Lite3
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Node* GetNodePtr(ReadOnlySpan<byte> buffer, int offset)
    {
        fixed (byte* p = buffer)
            return (Node*)(p + offset);
    }
}