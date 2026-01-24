using System.Buffers;
using System.Numerics;

namespace Lite3DotNet;

public ref struct Lite3Context
{
    /// <summary>
    ///     <para>Create a context, optionally with a custom size.</para>
    ///     <para>
    ///         If you know that you will be storing a large message, it is more efficient to allocate a large context up
    ///         front.
    ///         Otherwise, a small default context will copy and relocate data several times.
    ///     </para>
    /// </summary>
    /// <param name="initialCapacity">Optional. The initial buffer capacity.</param>
    /// <param name="arrayPool">Optional. The array pool to use when resizing the buffer; defaults to <see cref="ArrayPool{byte}.Shared" />.</param>
    /// <returns>The context.</returns>
    /// <remarks>
    ///     <em>Ported from C <c>lite3_ctx_create_with_size</c> and <c>lite3_ctx_create</c></em>
    /// </remarks>
    public static Lite3Context Create(int initialCapacity = Lite3Buffer.MinBufferSize, ArrayPool<byte>? arrayPool = null)
    {
        arrayPool ??= ArrayPool<byte>.Shared;
        
        var buffer = arrayPool.Rent(initialCapacity);
        
        return new  Lite3Context(buffer, position: 0, isRentedBuffer: true, arrayPool);
    }

    /// <summary>
    ///     <para>Create context by copying from a buffer.</para>
    ///     <para>This function will copy data into a newly allocated context. The original data is not affected.</para>
    /// </summary>
    /// <param name="buffer">The source buffer.</param>
    /// <param name="position">The position of written data in the source buffer.</param>
    /// <param name="arrayPool">Optional. The array pool to use when resizing the buffer; defaults to <see cref="ArrayPool{byte}.Shared" />.</param>
    /// <returns>The context.</returns>
    /// <exception cref="ArgumentException">If the buffer is too large for the initial resize.</exception>
    /// <remarks>
    ///     <em>Ported from C <c>lite3_ctx_create_from_buf</c>.</em>
    /// </remarks>
    public static Lite3Context CreateFrom(ReadOnlySpan<byte> buffer, int position, ArrayPool<byte>? arrayPool = null)
    {
        arrayPool ??= ArrayPool<byte>.Shared;
        
        if (buffer.Length == 0)
            throw new ArgumentException("Buffer is empty.", nameof(buffer));

        var neededSize = checked((uint)buffer.Length + Lite3Core.NodeAlignmentMask);
        var newSize = BitOperations.RoundUpToPowerOf2(neededSize);
        newSize = Math.Clamp(newSize, Lite3Buffer.MinBufferSize, Lite3Buffer.MaxBufferSize);
        
        if (buffer.Length > newSize - Lite3Core.NodeAlignmentMask)
            throw new ArgumentException("Buffer is too large.", nameof(buffer));
        
        var newBuffer = arrayPool.Rent((int)newSize);
        
        buffer.CopyTo(newBuffer);
        
        return new Lite3Context(newBuffer, position, isRentedBuffer: true, arrayPool: arrayPool);
    }

    /// <summary>
    ///     <para>Create context by taking ownership of a buffer.</para>
    ///     <para>
    ///         When you have an existing allocation containing a Lite³ message,
    ///         you might want a context to take ownership over it rather than copying all the data.
    ///     </para>
    ///     <para>The passed buffer will be returned when the scope is disposed, or if the buffer grows.</para>
    /// </summary>
    /// <param name="buffer">The buffer to use.</param>
    /// <param name="position">The source position of written data in the buffer.</param>
    /// <param name="arrayPool">
    ///     Optional. The array pool to use when resizing the buffer; defaults to
    ///     <see cref="ArrayPool{byte}.Shared" />.
    /// </param>
    /// <returns>The context.</returns>
    /// <remarks>
    ///     <para><b>Warning</b>: the array pool needs to be what was used to obtain the original buffer.</para>
    ///     <para>Ported from C <em>lite3_ctx_create_take_ownership</em>.</para>
    /// </remarks>
    public static Lite3Context CreateFromOwned(byte[] buffer, int position, ArrayPool<byte>? arrayPool = null)
    {
        arrayPool ??= ArrayPool<byte>.Shared;
        return new Lite3Context(buffer, position, isRentedBuffer: true, arrayPool);
    }

    private readonly Scope _scope;
    
    public Span<byte> Buffer;
    public int Position;

    private Lite3Context(byte[] buffer, int position, bool isRentedBuffer, ArrayPool<byte> arrayPool)
    {
        _scope = new Scope(buffer, isRentedBuffer, arrayPool);
        
        Buffer = buffer;
        Position = position;
    }

    public Scope BeginScope() => _scope;
    
    public Span<byte> WrittenBuffer => Buffer[..Position];

    internal Lite3Core.Status Grow()
    {
        var status = Lite3Buffer.Grow(_scope.ArrayPool, _scope.IsRentedBuffer, _scope.Buffer, out _scope.Buffer);
        
        _scope.IsRentedBuffer = true;
        
        return status;
    }

    public sealed class Scope(byte[] buffer, bool isRentedBuffer, ArrayPool<byte> arrayPool)
        : IDisposable
    {
        internal readonly ArrayPool<byte> ArrayPool = arrayPool;
        internal byte[] Buffer = buffer;
        internal bool IsRentedBuffer = isRentedBuffer;
        
        public void Dispose()
        {
            if (IsRentedBuffer)
                ArrayPool<byte>.Shared.Return(Buffer);
        }
    }
}