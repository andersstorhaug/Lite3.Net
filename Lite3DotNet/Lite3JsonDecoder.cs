using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Lite3DotNet;

public static class Lite3JsonDecoder
{
    private const int MinReadSize = 1024;
    private const int MaxReadBufferSize = 65536;
    private const int ReadSlack = 64;
    private const int FrameStackSize = JsonConstants.NestingDepthMax * 2 + 1;
    private const int StackallocStringLength = 256;

    /// <summary>
    ///     Convert from JSON and write to a provided destination message buffer.
    /// </summary>
    /// <param name="utf8Json">The source JSON in UTF-8.</param>
    /// <param name="buffer">The destination message buffer.</param>
    /// <param name="arrayPool">Optional. The array pool for internal use.</param>
    /// <returns>The resulting written position of the message buffer.</returns>
    public static int Decode(ReadOnlySpan<byte> utf8Json, byte[] buffer, ArrayPool<byte>? arrayPool = null)
    {
        arrayPool ??= ArrayPool<byte>.Shared;
        
        var (_, position) = DecodeImpl(buffer, isRentedBuffer: false, growBuffer: false, utf8Json, arrayPool);
        
        return position;
    }

    /// <summary>
    ///     Convert from JSON and write to a growable destination message buffer.
    /// </summary>
    /// <param name="utf8Json">The source JSON in UTF-8.</param>
    /// <param name="initialCapacity">Optional. The initial capacity for the message buffer.</param>
    /// <param name="arrayPool">Optional. The array pool for resizing the output buffer and internal use.</param>
    /// <returns>A disposable <see cref="DecodeResult" /> containing the resulting message buffer, written position, and buffer pool.</returns>
    /// <remarks>
    ///     <b>Warning</b>: The caller must dispose the returned <see cref="DecodeResult" /> for the buffer to return to the pool.
    /// </remarks>
    public static DecodeResult Decode(
        ReadOnlySpan<byte> utf8Json,
        int initialCapacity = Lite3Buffer.MinBufferSize,
        ArrayPool<byte>? arrayPool = null)
    {
        arrayPool ??= ArrayPool<byte>.Shared;
        
        var initialBuffer = arrayPool.Rent(initialCapacity);
        var (buffer, position) = DecodeImpl(initialBuffer, isRentedBuffer: true, growBuffer: true, utf8Json, arrayPool);
        
        return new DecodeResult(buffer, position, arrayPool: arrayPool);
    }
    
    /// <summary>
    ///     Convert from JSON asynchronously and write to a provided destination message buffer.
    /// </summary>
    /// <param name="pipeReader">The source pipe reader.</param>
    /// <param name="buffer">The destination message buffer.</param>
    /// <param name="minReadSize">Optional. The minimum source read size.</param>
    /// <param name="arrayPool">Optional. The array pool for internal use.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resulting written position of the message buffer.</returns>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<int> DecodeAsync(
        PipeReader pipeReader,
        byte[] buffer,
        int minReadSize = MinReadSize,
        ArrayPool<byte>? arrayPool = null,
        CancellationToken cancellationToken = default)
    {
        var (_, position) = await DecodeAsyncImpl(
            buffer,
            isRentedBuffer: false,
            growBuffer: false,
            pipeReader,
            minReadSize,
            arrayPool ?? ArrayPool<byte>.Shared,
            cancellationToken).ConfigureAwait(false);
        
        return position;
    }

    /// <summary>
    ///     Convert from JSON asynchronously and write to a growable destination buffer.
    /// </summary>
    /// <param name="pipeReader">The source pipe reader.</param>
    /// <param name="minReadSize">Optional. The minimum source read size.</param>
    /// <param name="initialCapacity">Optional. The initial capacity for the message buffer and internal use.</param>
    /// <param name="arrayPool">Optional. The array pool for resizing the output buffer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A disposable <see cref="DecodeResult" /> containing the resulting message buffer, written position, and buffer pool.</returns>
    /// <remarks>
    ///     <b>Warning</b>: The caller must dispose the returned <see cref="DecodeResult" /> for the buffer to return to the pool.
    /// </remarks>
    /// 
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<DecodeResult> DecodeAsync(
        PipeReader pipeReader,
        int minReadSize = MinReadSize,
        int initialCapacity = Lite3Buffer.MinBufferSize,
        ArrayPool<byte>? arrayPool = null,
        CancellationToken cancellationToken = default)
    {
        arrayPool ??= ArrayPool<byte>.Shared;
        
        var initialBuffer = arrayPool.Rent(initialCapacity);
        
        var (buffer, position) = await DecodeAsyncImpl(
            initialBuffer,
            isRentedBuffer: true,
            growBuffer: true,
            pipeReader,
            minReadSize,
            arrayPool,
            cancellationToken).ConfigureAwait(false);
        
        return new DecodeResult(buffer, position, arrayPool);
    }
    
    private static (byte[] Buffer, int Position) DecodeImpl(
        byte[] buffer,
        bool isRentedBuffer,
        bool growBuffer,
        scoped ReadOnlySpan<byte> utf8Json,
        ArrayPool<byte> arrayPool)
    {
        var position = 0;
        Span<Frame> frames = stackalloc Frame[FrameStackSize];
        var stack = new FrameStack(frames);
        var decodeState = new DecodeState();

        try
        {
            var jsonReader = new Utf8JsonReader(utf8Json, isFinalBlock: true, new JsonReaderState());

            var status = DecodeCore(
                arrayPool,
                ref decodeState,
                ref stack,
                ref buffer,
                isRentedBuffer,
                growBuffer,
                ref position,
                ref jsonReader);

            if (status < 0)
                throw status.AsException();

            if (jsonReader.BytesConsumed != utf8Json.Length)
                throw new JsonException("Trailing data after JSON payload.");
        }
        catch
        {
            if (isRentedBuffer)
                arrayPool.Return(buffer);
            throw;
        }
        finally
        {
            if (decodeState.PendingKey is { } pendingKey)
                arrayPool.Return(pendingKey);
        }

        return (buffer, position);
    }
    
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<(byte[] Buffer, int Position)> DecodeAsyncImpl(
        byte[] buffer,
        bool isRentedBuffer,
        bool growBuffer,
        PipeReader pipeReader,
        int minReadSize,
        ArrayPool<byte> arrayPool,
        CancellationToken cancellationToken)
    {
        var readerState = default(JsonReaderState);
        var targetReadSize = minReadSize;
        var position = 0;
        var decodeState = new DecodeState();
        var frames = ArrayPool<Frame>.Shared.Rent(FrameStackSize);
        var stackIndex = -1;

        try
        {
            while (true)
            {
                var readSize = targetReadSize > MinReadSize
                    ? Math.Min(targetReadSize + ReadSlack, MaxReadBufferSize)
                    : MinReadSize;

                var result = await pipeReader.ReadAtLeastAsync(readSize, cancellationToken).ConfigureAwait(false);

                var isCompleted = result.IsCompleted;
                var readerBuffer = result.Buffer;

                if (isCompleted && readerBuffer.IsEmpty)
                {
                    pipeReader.AdvanceTo(readerBuffer.End);
                    break;
                }

                var jsonReader = new Utf8JsonReader(readerBuffer, isCompleted, readerState);
                var stack = new FrameStack(frames, stackIndex);

                var status = DecodeCore(
                    arrayPool,
                    ref decodeState,
                    ref stack,
                    ref buffer,
                    isRentedBuffer,
                    growBuffer,
                    ref position,
                    ref jsonReader);

                switch (status)
                {
                    case Lite3Core.Status.NeedsMoreData when !isCompleted:
                        if (jsonReader.BytesConsumed > 0)
                            break;
                        
                        if (targetReadSize >= MaxReadBufferSize)
                            throw new InvalidDataException("Input requires buffering more than the maximum allowed size.");
                        
                        targetReadSize = targetReadSize >= MaxReadBufferSize / 2
                            ? MaxReadBufferSize
                            : targetReadSize * 2;
                        break;

                    case < 0:
                        throw status.AsException();

                    default:
                        targetReadSize = minReadSize;
                        break;
                }

                if (isCompleted)
                {
                    if (readerBuffer.Length != jsonReader.BytesConsumed)
                        throw new JsonException("Trailing data after JSON payload.");

                    pipeReader.AdvanceTo(readerBuffer.End);
                    break;
                }

                readerState = jsonReader.CurrentState;
                stackIndex = stack.Index;
                pipeReader.AdvanceTo(readerBuffer.GetPosition(jsonReader.BytesConsumed), readerBuffer.End);
            }
        }
        catch
        {
            if (isRentedBuffer)
                arrayPool.Return(buffer);
            throw;
        }
        finally
        {
            if (decodeState.PendingKey is { } pendingKey)
                arrayPool.Return(pendingKey);
            
            ArrayPool<Frame>.Shared.Return(frames);
        }

        return (buffer, position);
    }

    /// <summary>
    ///     <para>
    ///         The core decode implementation, which is designed to support:
    ///         <list type="bullet">
    ///             <item>
    ///                 Synchronous use with stackalloc buffers <em>or</em> asynchronous use which may require use of pooled
    ///                 buffers. Needs to yield upon underflow, and subsequently resume.
    ///             </item>
    ///             <item>
    ///                 Lite3 buffers that are fixed <em>or</em> resizable. Needs to resize the Lite3 buffer without advancing
    ///                 the JSON reader.
    ///             </item>
    ///         </list>
    ///     </para>
    ///     <para>Based on the C recursive implementation, however, stack-based primarily to support yield and resume.</para>
    ///     <para>
    ///         Lite3 operations require both the key and the value. In general, keys and string values are read and copied in
    ///         the most efficient way possible. Underflow may also happen between reading the key and the value, in which
    ///         case, the pending key must copied into an owned buffer before yield.
    ///     </para>
    /// </summary>
    private static Lite3Core.Status DecodeCore(
        ArrayPool<byte> arrayPool,
        ref DecodeState state,
        ref FrameStack stack,
        ref byte[] buffer,
        bool isRentedBuffer,
        bool growBuffer,
        ref int position,
        ref Utf8JsonReader reader)
    {
        Lite3Core.Status status;
        
        if (position == 0)
        {
            if ((status = DecodeDocument(ref stack, buffer, ref position, ref reader)) < 0)
            {
                // Note: resize and/or underflow should never happen on the first token.
                throw status.AsException();
            }
        }

        // Used to replay the current JSON token upon Lite3 buffer resize.
        var replayToken = false;

        do
        {
            Process(ref stack, ref reader, ref state, ref buffer, ref position);
            
            // Yield back to the caller upon error. This may be resumable due to underflow.
            if (status < 0)
                return status;
        } while (!stack.IsEmpty());

        return 0;

        // Local function for the loop to allow for `stackalloc`.
        void Process(
            ref FrameStack stack,
            ref Utf8JsonReader reader,
            ref DecodeState state,
            ref byte[] buffer,
            ref int position)
        {
            ref var frame = ref stack.PeekRef();

            switch (frame.Kind)
            {
                case FrameKind.Object:
                    status = DecodeObject(ref stack, replayToken, ref reader);
                    break;
                
                case FrameKind.ObjectSwitch:
                    var readMethod = GetStringReadMethod(ref reader, out var bufferLength);
                    
                    var rentedBuffer = default(byte[]?);
                    
                    var spanBuffer = readMethod is > ReadMethod.StackallocMarker and < ReadMethod.PooledMarker
                        ? stackalloc byte[StackallocStringLength]
                        : default;
                    
                    var key =
                        state.PendingKey != null ? state.PendingKey.AsSpan(0, state.PendingKeyLength) :
                        readMethod == ReadMethod.FromSpan ? reader.ValueSpan :
                        readMethod < ReadMethod.PooledMarker ? spanBuffer[..CopyString(readMethod, ref reader, spanBuffer, bufferLength)] :
                        RentString(readMethod, ref reader, bufferLength, arrayPool, out rentedBuffer);

                    try
                    {
                        status = DecodeObjectSwitch(arrayPool, ref stack, buffer, ref position, replayToken, key, ref reader);
                    }
                    catch
                    {
                        if (rentedBuffer != null)
                            arrayPool.Return(rentedBuffer);
                        
                        throw;
                    }

                    // Ensure that buffers are returned upon success.
                    if (status >= 0)
                    {
                        if (rentedBuffer != null)
                        {
                            arrayPool.Return(rentedBuffer);
                            break;
                        }

                        if (state.PendingKey != null)
                        {
                            arrayPool.Return(state.PendingKey);
                            state.PendingKey = null;
                        }
                        break;
                    }

                    // Upon underflow, store the pending key bytes in an owned buffer.
                    // For synchronous use this is unnecessary, as an exception will result; but that distinction is not made here.
                    if (status == Lite3Core.Status.NeedsMoreData && state.PendingKey == null)
                    {
                        // Keep the key as-is if it has already been pooled.
                        if (rentedBuffer != null)
                        {
                            state.PendingKey = rentedBuffer;
                            state.PendingKeyLength = key.Length;
                            break;
                        }

                        state.PendingKey = arrayPool.Rent(key.Length);
                        state.PendingKeyLength = key.Length;
                        key.CopyTo(state.PendingKey);
                    }
                    break;
                
                case FrameKind.Array:
                    status = DecodeArray(ref stack, replayToken, ref reader);
                    break;
                
                case FrameKind.ArraySwitch:
                    status = DecodeArraySwitch(arrayPool, ref stack, buffer, ref position, ref reader);
                    break;
                
                default:
                    throw new InvalidDataException("Unknown frame kind.");
            }

            replayToken = false;

            // If the buffer is not resizable, let the caller handle the exceptional path.
            if (status != Lite3Core.Status.InsufficientBuffer || !growBuffer) 
                return;
            
            // Resize the buffer and replay.
            if ((status = Lite3Buffer.Grow(arrayPool, isRentedBuffer, buffer, out buffer)) < 0)
                return;
            
            isRentedBuffer = true;
            replayToken = true;
        }
    }

    private static Lite3Core.Status DecodeDocument(
        ref FrameStack stack,
        byte[] buffer,
        ref int position,
        ref Utf8JsonReader reader)
    {
        Lite3Core.Status status;

        if (!reader.Read())
            return Lite3Core.Status.NeedsMoreData;

        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                return (status = Lite3Core.InitializeObject(buffer, out position)) >= 0
                    ? stack.Push(new Frame(FrameKind.Object, 0))
                    : status;

            case JsonTokenType.StartArray:
                return (status = Lite3Core.InitializeArray(buffer, out position)) >= 0
                    ? stack.Push(new Frame(FrameKind.Array, 0))
                    : status;

            default:
                return Lite3Core.Status.ExpectedJsonArrayOrObject;
        }
    }
    
    private static Lite3Core.Status DecodeObject(
        ref FrameStack stack,
        bool replayToken,
        ref Utf8JsonReader reader)
    {
        if (reader.CurrentDepth > JsonConstants.NestingDepthMax)
            return Lite3Core.Status.JsonNestingDepthExceededMax;
        
        ref var frame = ref stack.PeekRef();
        var offset = frame.Offset;
        
        Debug.Assert(frame.Kind == FrameKind.Object);

        if (!replayToken && !reader.Read())
            return Lite3Core.Status.NeedsMoreData;
        
        if (reader.TokenType == JsonTokenType.EndObject)
            return stack.Pop();

        return reader.TokenType == JsonTokenType.PropertyName
            ? stack.Push(new Frame(FrameKind.ObjectSwitch, offset))
            : Lite3Core.Status.ExpectedJsonProperty;
    }
    
    private static Lite3Core.Status DecodeObjectSwitch(
        ArrayPool<byte> arrayPool,
        ref FrameStack stack,
        byte[] buffer,
        ref int position,
        bool replayToken,
        scoped ReadOnlySpan<byte> key,
        ref Utf8JsonReader reader)
    {
        Lite3Core.Status status;

        if (key.IsEmpty)
            return Lite3Core.Status.ExpectedNonEmptyKey;
        
        ref var frame =  ref stack.PeekRef();
        var offset = frame.Offset;

        Debug.Assert(frame.Kind == FrameKind.ObjectSwitch);

        if (!replayToken && !reader.Read())
            return Lite3Core.Status.NeedsMoreData;

        var keyData = Lite3Core.GetKeyData(key);
        
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                status = Lite3Core.SetNull(buffer, ref position, offset, key, keyData);
                break;
            case JsonTokenType.False:
                status = Lite3Core.SetBool(buffer, ref position, offset, key, keyData, false);
                break;
            case JsonTokenType.True:
                status = Lite3Core.SetBool(buffer, ref position, offset, key, keyData, true);
                break;
            case JsonTokenType.Number:
            {
                status = reader.TryGetInt64(out var value)
                    ? Lite3Core.SetLong(buffer, ref position, offset, key, keyData, value)
                    : Lite3Core.SetDouble(buffer, ref position, offset, key, keyData, reader.GetDouble());
                break;
            }
            case JsonTokenType.String:
            {
                var readMethod = GetStringReadMethod(ref reader, out var bufferLength);

                var spanBuffer = readMethod is > ReadMethod.StackallocMarker and < ReadMethod.PooledMarker
                    ? stackalloc byte[StackallocStringLength]
                    : default;

                var rentedBuffer = default(byte[]?);
                
                var value =
                    readMethod == ReadMethod.FromSpan ? reader.ValueSpan :
                    readMethod < ReadMethod.PooledMarker ? spanBuffer[..CopyString(readMethod, ref reader, spanBuffer, bufferLength)] :
                    RentString(readMethod, ref reader, bufferLength, arrayPool, out rentedBuffer);

                try
                {
                    status = Lite3Core.SetString(buffer, ref position, offset, key, keyData, value);
                }
                finally
                {
                    if (rentedBuffer != null)
                        arrayPool.Return(rentedBuffer);
                }
                break;
            }
            case JsonTokenType.StartObject:
            {
                if ((status = Lite3Core.SetObject(buffer, ref position, offset, key, keyData, out var objectOffset)) < 0)
                    return status;
                
                stack.Pop();
                return stack.Push(new Frame(FrameKind.Object, objectOffset));

            }
            case JsonTokenType.StartArray:
            {
                if ((status = Lite3Core.SetArray(buffer, ref position, offset, key, keyData, out var arrayOffset)) < 0)
                    return status;
                
                stack.Pop();
                return stack.Push(new Frame(FrameKind.Array, arrayOffset));
            }
            default:
                status = Lite3Core.Status.ExpectedJsonValue;
                break;
        }

        return status >= 0 ? stack.Pop() : status;
    }
        
    private static Lite3Core.Status DecodeArray(ref FrameStack stack, bool replayToken, ref Utf8JsonReader reader)
    {
        if (reader.CurrentDepth > JsonConstants.NestingDepthMax)
            return Lite3Core.Status.JsonNestingDepthExceededMax;
        
        ref var frame = ref stack.PeekRef();
        var offset = frame.Offset;
        
        Debug.Assert(frame.Kind == FrameKind.Array);

        if (!replayToken && !reader.Read())
            return Lite3Core.Status.NeedsMoreData;

        return reader.TokenType != JsonTokenType.EndArray
            ? stack.Push(new Frame(FrameKind.ArraySwitch, offset))
            : stack.Pop();
    }
    
    private static Lite3Core.Status DecodeArraySwitch(
        ArrayPool<byte> arrayPool,
        ref FrameStack stack,
        byte[] buffer,
        ref int position,
        ref Utf8JsonReader reader)
    {
        Lite3Core.Status status;
        
        ref var frame =  ref stack.PeekRef();
        var offset = frame.Offset;
        
        Debug.Assert(frame.Kind == FrameKind.ArraySwitch);
        
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                status = Lite3Core.ArrayAppendNull(buffer, ref position, offset);
                break;
            case JsonTokenType.False:
                status = Lite3Core.ArrayAppendBool(buffer, ref position, offset, false);
                break;
            case JsonTokenType.True:
                status = Lite3Core.ArrayAppendBool(buffer, ref position, offset, true);
                break;
            case JsonTokenType.Number:
            {
                status = reader.TryGetInt64(out var value)
                    ? Lite3Core.ArrayAppendLong(buffer, ref position, offset, value)
                    : Lite3Core.ArrayAppendDouble(buffer, ref position, offset, reader.GetDouble());
                break;
            }
            case JsonTokenType.String:
            {
                var readMethod = GetStringReadMethod(ref reader, out var bufferLength);

                var spanBuffer = readMethod is > ReadMethod.StackallocMarker and < ReadMethod.PooledMarker
                    ? stackalloc byte[StackallocStringLength]
                    : default;

                var rentedBuffer = default(byte[]?);
                
                var value =
                    readMethod == ReadMethod.FromSpan ? reader.ValueSpan :
                    readMethod < ReadMethod.PooledMarker ? spanBuffer[..CopyString(readMethod, ref reader, spanBuffer, bufferLength)] :
                    RentString(readMethod, ref reader, bufferLength, arrayPool, out rentedBuffer);

                try
                {
                    status = Lite3Core.ArrayAppendString(buffer, ref position, offset, value);
                }
                finally
                {
                    if (rentedBuffer != null)
                        arrayPool.Return(rentedBuffer);
                }
                break;
            }
            case JsonTokenType.StartObject:
            {
                if ((status = Lite3Core.ArrayAppendObject(buffer, ref position, offset, out var objectOffset)) < 0)
                    return status;
                
                stack.Pop();
                return stack.Push(new Frame(FrameKind.Object, objectOffset));
            }
            case JsonTokenType.StartArray:
            {
                if ((status = Lite3Core.ArrayAppendArray(buffer, ref position, offset, out var arrayOffset)) < 0)
                    return status;
                
                stack.Pop();
                return stack.Push(new Frame(FrameKind.Array, arrayOffset));
            }
            default:
                status = Lite3Core.Status.ExpectedJsonValue;
                break;
        }
        
        return status >= 0 ? stack.Pop() : status;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CopyString(
        ReadMethod method,
        ref Utf8JsonReader reader,
        scoped Span<byte> buffer,
        int bufferLength)
    {
        Debug.Assert(method is > ReadMethod.StackallocMarker and < ReadMethod.PooledMarker);
        
        switch (method)
        {
            case ReadMethod.StackallocFromSpan:
                reader.ValueSpan.CopyTo(buffer);
                return bufferLength;
            case ReadMethod.StackallocFromSequence:
                reader.ValueSequence.CopyTo(buffer);
                return bufferLength;
            case ReadMethod.StackallocFromCopyString:
                return reader.CopyString(buffer);
            default:
                throw new InvalidOperationException("Invalid string read method.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> RentString(
        ReadMethod method,
        ref Utf8JsonReader reader,
        int bufferLength,
        ArrayPool<byte> arrayPool,
        out byte[] buffer)
    {
        Debug.Assert(method > ReadMethod.PooledMarker);
        
        buffer = arrayPool.Rent(bufferLength);
        
        var length = bufferLength;

        try
        {
            switch (method)
            {
                case ReadMethod.PooledFromSpan:
                    reader.ValueSpan.CopyTo(buffer);
                    break;
                case ReadMethod.PooledFromSequence:
                    reader.ValueSequence.CopyTo(buffer);
                    break;
                case ReadMethod.PooledFromCopyString:
                    length = reader.CopyString(buffer);
                    break;
                default:
                    throw new InvalidOperationException("Invalid string read method.");
            }

            return buffer.AsSpan(0, length);
        }
        catch
        {
            arrayPool.Return(buffer);
            throw;
        }
    }

    /// <summary>
    ///     <para>Determine the best method to read a string from the JSON reader, in terms of memory use and performance.</para>
    ///     <para>Factored out so that callers can <c>stackalloc</c> when appropriate.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadMethod GetStringReadMethod(ref Utf8JsonReader reader, out int bufferLength)
    {
        bufferLength = 0;
        
        var hasSequence = reader.HasValueSequence;
        var isEscaped = reader.ValueIsEscaped;

        if (!hasSequence && !isEscaped)
            return ReadMethod.FromSpan;
        
        bufferLength = hasSequence
            ? checked((int)reader.ValueSequence.Length)
            : reader.ValueSpan.Length;
        
        if (bufferLength <= StackallocStringLength)
        {
            return isEscaped ? ReadMethod.StackallocFromCopyString : ReadMethod.StackallocFromSequence;
        }

        if (!isEscaped)
            return hasSequence ? ReadMethod.PooledFromSequence : ReadMethod.PooledFromSpan;

        return ReadMethod.PooledFromCopyString;
    }

    private enum ReadMethod
    {
        FromSpan = 1,
        
        StackallocMarker,
        StackallocFromSpan,
        StackallocFromSequence,
        StackallocFromCopyString,
        
        PooledMarker,
        PooledFromSpan,
        PooledFromSequence,
        PooledFromCopyString
    }

    private struct DecodeState
    {
        public byte[]? PendingKey;
        public int PendingKeyLength;
    }

    /// <remarks>
    ///     Fixed-sized. Allows the caller to either <c>stackalloc</c> or rent frames.
    /// </remarks>
    private ref struct FrameStack(Span<Frame> frames, int index = -1)
    {
        private readonly Span<Frame> _frames = frames;
        private int _index = index;
        
        public int Index => _index;
        public bool IsEmpty() => _index < 0;
        public ref Frame PeekRef() => ref _frames[_index];
        
        public Lite3Core.Status Push(in Frame frame)
        {
            Debug.Assert(_index + 1 < _frames.Length);
            _frames[++_index] = frame;
            return 0;
        }

        public Lite3Core.Status Pop()
        {
            Debug.Assert(_index >= 0);
            _frames[_index--] = default;
            return 0;
        }
    }

    private enum FrameKind
    {
        Object,
        ObjectSwitch,
        Array,
        ArraySwitch
    }
    
    private readonly struct Frame(FrameKind kind, int offset)
    {
        public readonly FrameKind Kind = kind;
        public readonly int Offset = offset; 
    }

    public readonly struct DecodeResult(byte[] buffer, int position, ArrayPool<byte> arrayPool)
        : IDisposable
    {
        public readonly byte[] Buffer = buffer;
        public readonly int Position = position;
        public readonly ArrayPool<byte> ArrayPool = arrayPool;

        public void Dispose() => ArrayPool.Return(Buffer);
    }
}