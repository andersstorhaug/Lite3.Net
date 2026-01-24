using System.Buffers;
using System.Runtime.CompilerServices;

namespace TronDotNet;

public static class TronBuffer
{
    public const int MaxBufferSize = int.MaxValue;
    public const int MinBufferSize = 1024;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Lite3.Status Grow(
        ArrayPool<byte> arrayPool,
        bool isRentedBuffer,
        byte[] current,
        out byte[] next)
    {
        next = current;

        // Increase size by 4X up to MaxBufferSize
        var newSize = current.Length < MaxBufferSize / 4 ? current.Length << 2 : MaxBufferSize;
        newSize = Math.Clamp(newSize, MinBufferSize, MaxBufferSize);
        
        if (current.Length > newSize - Lite3.NodeAlignmentMask)
            return Lite3.Status.InsufficientBuffer;
        
        next = arrayPool.Rent(newSize);
        
        current.CopyTo(next);
        
        if (isRentedBuffer)
            arrayPool.Return(current);

        return Lite3.Status.GrewBuffer;
    }
}