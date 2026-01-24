using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TronDotNet.SystemTextJson;

public static class TronJsonDecoder
{
    private const int MinReadSize = 1024;
    private const int MaxReadBufferSize = 65536;
    private const int ReadSlack = 64;
    private const int FrameStackSize = JsonConstants.NestingDepthMax * 2 + 1;

    /// <summary>
    ///     Convert from JSON and write to a provided destination message buffer.
    /// </summary>
    /// <param name="utf8Json">The source JSON in UTF-8.</param>
    /// <param name="buffer">The destination message buffer.</param>
    /// <returns>The resulting written position of the message buffer.</returns>
    public static int Decode(ReadOnlySpan<byte> utf8Json, byte[] buffer)
    {
        var (_, position) = DecodeImpl(buffer, utf8Json, arrayPool: null);
        
        return position;
    }

    /// <summary>
    ///     Convert from JSON and write to a growable destination message buffer.
    /// </summary>
    /// <param name="utf8Json">The source JSON in UTF-8.</param>
    /// <param name="arrayPool">
    ///     Optional. The array pool for resizing the output buffer; defaults to
    ///     <see cref="ArrayPool{byte}.Shared" />.
    /// </param>
    /// <returns>A disposable <see cref="DecodeResult" /> containing the resulting message buffer, written position, and buffer pool.</returns>
    /// <remarks>
    ///     <b>Warning</b>: The caller must dispose the returned <see cref="DecodeResult" /> for the buffer to return to the pool.
    /// </remarks>
    public static DecodeResult Decode(ReadOnlySpan<byte> utf8Json, ArrayPool<byte>? arrayPool = null)
    {
        arrayPool ??= ArrayPool<byte>.Shared;
        
        var (buffer, position) = DecodeImpl(buffer: null, utf8Json, arrayPool);
        
        return new DecodeResult(buffer, position, isRentedBuffer: true, arrayPool);
    }
    
    /// <summary>
    ///     Convert from JSON asynchronously and write to a provided destination message buffer.
    /// </summary>
    /// <param name="pipeReader">The source pipe reader.</param>
    /// <param name="buffer">The destination message buffer.</param>
    /// <param name="minReadSize">Optional. The minimum source read size.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resulting written position of the message buffer.</returns>
    public static async Task<int> DecodeAsync(
        PipeReader pipeReader,
        byte[] buffer,
        int minReadSize = MinReadSize,
        CancellationToken cancellationToken = default)
    {
        var (_, position, _) = await DecodeAsyncImpl(
            buffer,
            pipeReader,
            minReadSize,
            arrayPool: null,
            cancellationToken);
        
        return position;
    }

    /// <summary>
    ///     Convert from JSON asynchronously and write to a growable destination buffer.
    /// </summary>
    /// <param name="pipeReader">The source pipe reader.</param>
    /// <param name="minReadSize">Optional. The minimum source read size.</param>
    /// <param name="arrayPool">
    ///     Optional. The array pool for resizing the output buffer; defaults to
    ///     <see cref="ArrayPool{byte}.Shared" />.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A disposable <see cref="DecodeResult" /> containing the resulting message buffer, written position, and buffer pool.</returns>
    /// <remarks>
    ///     <b>Warning</b>: The caller must dispose the returned <see cref="DecodeResult" /> for the buffer to return to the pool.
    /// </remarks>
    public static async Task<DecodeResult> DecodeAsync(
        PipeReader pipeReader,
        int minReadSize = MinReadSize,
        ArrayPool<byte>? arrayPool = null,
        CancellationToken cancellationToken = default)
    {
        arrayPool ??= ArrayPool<byte>.Shared;
        
        var (buffer, position, isRentedBuffer) = await DecodeAsyncImpl(
            buffer: null,
            pipeReader,
            minReadSize,
            arrayPool,
            cancellationToken);
        
        return new DecodeResult(buffer, position, isRentedBuffer, arrayPool);
    }
    
    private static async Task<(byte[] Buffer, int Position, bool IsRentedBuffer)> DecodeAsyncImpl(
        byte[]? buffer,
        PipeReader pipeReader,
        int minReadSize,
        ArrayPool<byte>? arrayPool,
        CancellationToken cancellationToken)
    {
        var growBuffer = arrayPool != null;
        var isRentedBuffer = false;
        
        arrayPool ??= ArrayPool<byte>.Shared;
        
        if (buffer == null)
        {
            if (arrayPool == null)
                throw new ArgumentNullException(nameof(arrayPool), "A buffer must be provided.");
            
            buffer = arrayPool.Rent(TronBuffer.MinBufferSize);
            isRentedBuffer = true;
        }
        
        var readerState = default(JsonReaderState);
        var targetReadSize = minReadSize;
        var position = 0;
        var frames = new FrameStack(new Frame[FrameStackSize]);

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

                if (readerBuffer.Length > MaxReadBufferSize)
                    throw new InvalidDataException("JSON token exceeds maximum buffer size.");
                
                var jsonReader = new Utf8JsonReader(readerBuffer, isCompleted, readerState);
                
                var status = DecodeCore(
                    arrayPool,
                    ref frames,
                    ref buffer,
                    growBuffer,
                    ref isRentedBuffer,
                    ref position,
                    ref jsonReader);

                switch (status)
                {
                    case Lite3.Status.NeedsMoreData:
                        if (jsonReader.BytesConsumed == 0)
                            targetReadSize = targetReadSize >= MaxReadBufferSize / 2 ? MaxReadBufferSize : targetReadSize * 2;
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
                pipeReader.AdvanceTo(readerBuffer.GetPosition(jsonReader.BytesConsumed));
            }
        }
        finally
        {
            await pipeReader.CompleteAsync().ConfigureAwait(false);
        }

        return (buffer, position, isRentedBuffer);
    }
    
    private static (byte[] Buffer, int Position) DecodeImpl(
        byte[]? buffer,
        ReadOnlySpan<byte> utf8Json,
        ArrayPool<byte>? arrayPool)
    {
        var growBuffer = arrayPool != null;
        var isRentedBuffer = false;
        
        arrayPool ??= ArrayPool<byte>.Shared;
        
        if (buffer == null)
        {
            if (arrayPool == null)
                throw new ArgumentNullException(nameof(arrayPool), "A buffer must be provided.");
            
            buffer = arrayPool.Rent(TronBuffer.MinBufferSize);
            isRentedBuffer = true;
        }
        
        var position = 0;
        var frames = new FrameStack(new Frame[FrameStackSize]);
        var jsonReader = new Utf8JsonReader(utf8Json, isFinalBlock: true, new JsonReaderState());

        do
        {
            var status = DecodeCore(arrayPool,
                ref frames,
                ref buffer,
                growBuffer,
                ref isRentedBuffer,
                ref position,
                ref jsonReader);
            
            if (status < 0)
                throw status.AsException();
        } while (!frames.IsEmpty());
        
        if (jsonReader.BytesConsumed != utf8Json.Length)
            throw new JsonException("Trailing data after JSON payload.");
        
        return (buffer, position);
    }
    
    private static Lite3.Status DecodeCore(
        ArrayPool<byte> arrayPool,
        ref FrameStack frames,
        ref byte[] buffer,
        bool growBuffer,
        ref bool isRentedBuffer,
        ref int position,
        ref Utf8JsonReader reader)
    {
        var replayToken = false;
                
        do
        {
            if (position == 0)
                return DecodeDocument(ref frames, buffer, ref position, replayToken, ref reader);
            
            ref var frame = ref frames.PeekRef();

            var status = frame.Kind switch
            {
                FrameKind.Object => DecodeObject(arrayPool, ref frames, replayToken, ref reader),
                FrameKind.ObjectSwitch => DecodeObjectSwitch(arrayPool, ref frames, buffer, ref position, replayToken, ref reader),
                FrameKind.Array => DecodeArray(arrayPool, ref frames, replayToken, ref reader),
                FrameKind.ArraySwitch => DecodeArraySwitch(arrayPool, ref frames, buffer, ref position, ref reader),
                _ => throw new InvalidDataException("Unknown frame kind.")
            };

            replayToken = false;

            if (status == Lite3.Status.InsufficientBuffer && growBuffer)
            {
                status = TronBuffer.Grow(arrayPool, isRentedBuffer, buffer, out buffer);
                isRentedBuffer = true;
                replayToken = true;
            }
            
            if (status < 0)
                return status;
        } while (!frames.IsEmpty());

        return 0;
    }

    private static Lite3.Status DecodeDocument(
        ref FrameStack frames,
        byte[] buffer,
        ref int position,
        bool replayToken,
        ref Utf8JsonReader reader)
    {
        Lite3.Status status;

        if (!replayToken && !reader.Read())
            return reader.IsFinalBlock ? Lite3.Status.InsufficientBuffer : Lite3.Status.NeedsMoreData;

        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                if ((status = Lite3.InitializeObject(buffer, out position)) < 0)
                    return status;

                frames.Push(Frame.ForObject(offset: 0));
                return 0;
            case JsonTokenType.StartArray:
                if ((status = Lite3.InitializeArray(buffer, out position)) < 0)
                    return status;
                
                frames.Push(Frame.ForArray(offset: 0));
                return 0;
            default:
                return Lite3.Status.ExpectedJsonArrayOrObject;
        }
    }
    
    private static Lite3.Status DecodeObject(
        ArrayPool<byte> arrayPool,
        ref FrameStack frames,
        bool replayToken,
        ref Utf8JsonReader reader)
    {
        if (reader.CurrentDepth > JsonConstants.NestingDepthMax)
            return Lite3.Status.JsonNestingDepthExceededMax;
        
        ref var frame = ref frames.PeekRef();
        var offset = frame.Offset;
        
        Debug.Assert(frame.Kind == FrameKind.Object);
        
        if (replayToken || reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                frames.Pop(arrayPool);
                return 0;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
                return Lite3.Status.ExpectedJsonProperty;

            frames.Push(Frame.ForObjectSwitch(offset, GetKeyRef(arrayPool, ref reader)));
            return 0;
        }

        return reader.IsFinalBlock ? Lite3.Status.InsufficientBuffer : Lite3.Status.NeedsMoreData;
    }
    
    private static Lite3.Status DecodeObjectSwitch(
        ArrayPool<byte> arrayPool,
        ref FrameStack frames,
        byte[] buffer,
        ref int position,
        bool replayToken,
        ref Utf8JsonReader reader)
    {
        Lite3.Status status;
        
        ref var frame =  ref frames.PeekRef();
        var offset = frame.Offset;
        var keyRef = frame.KeyRef;
        var key = keyRef.AsSpan();
        var keyData = Lite3.GetKeyData(key);
        
        Debug.Assert(frame.Kind == FrameKind.ObjectSwitch);

        if (!replayToken && !reader.Read())
            return Lite3.Status.NeedsMoreData;
        
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                status = Lite3.SetNull(buffer, ref position, offset, key, keyData);
                break;
            case JsonTokenType.False:
                status = Lite3.SetBool(buffer, ref position, offset, key, keyData, false);
                break;
            case JsonTokenType.True:
                status = Lite3.SetBool(buffer, ref position, offset, key, keyData, true);
                break;
            case JsonTokenType.Number:
            {
                status = reader.TryGetInt64(out var value)
                    ? Lite3.SetLong(buffer, ref position, offset, key, keyData, value)
                    : Lite3.SetDouble(buffer, ref position, offset, key, keyData, reader.GetDouble());
                break;
            }
            case JsonTokenType.String:
            {
                var value = ReadUtf8Value(arrayPool, ref reader, out var rentedBuffer);
                status = Lite3.SetString(buffer, ref position, offset, key, keyData, value);
                if (rentedBuffer != null)
                    arrayPool.Return(rentedBuffer);
                break;
            }
            case JsonTokenType.StartObject:
            {
                if ((status = Lite3.SetObject(buffer, ref position, offset, key, keyData, out var objectOffset)) >= 0)
                {
                    frames.Pop(arrayPool);
                    frames.Push(Frame.ForObject(objectOffset));
                }

                return status;
            }
            case JsonTokenType.StartArray:
            {
                if ((status = Lite3.SetArray(buffer, ref position, offset, key, keyData, out var arrayOffset)) >= 0)
                {
                    frames.Pop(arrayPool);
                    frames.Push(Frame.ForArray(arrayOffset));
                }

                return status;
            }
            default:
                status = Lite3.Status.ExpectedJsonValue;
                break;
        }

        if (status < 0)
            return status;
        
        frames.Pop(arrayPool);
        
        return status;
    }
    
    private static Lite3.Status DecodeArray(
        ArrayPool<byte> arrayPool,
        ref FrameStack frames,
        bool replayToken,
        ref Utf8JsonReader reader)
    {
        if (reader.CurrentDepth > JsonConstants.NestingDepthMax)
            return Lite3.Status.JsonNestingDepthExceededMax;
        
        ref var frame = ref frames.PeekRef();
        var offset = frame.Offset;
        
        Debug.Assert(frame.Kind == FrameKind.Array);
        
        if (replayToken || reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                frames.Pop(arrayPool);
                return 0;
            }

            frames.Push(Frame.ForArraySwitch(offset));
            return 0;
        }
        
        return reader.IsFinalBlock ? Lite3.Status.InsufficientBuffer : Lite3.Status.NeedsMoreData;
    }
    
    private static Lite3.Status DecodeArraySwitch(
        ArrayPool<byte> arrayPool,
        ref FrameStack frames,
        byte[] buffer,
        ref int position,
        ref Utf8JsonReader reader)
    {
        Lite3.Status status;
        
        ref var frame =  ref frames.PeekRef();
        var offset = frame.Offset;
        
        Debug.Assert(frame.Kind == FrameKind.ArraySwitch);
        
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                status = Lite3.ArrayAppendNull(buffer, ref position, offset);
                break;
            case JsonTokenType.False:
                status = Lite3.ArrayAppendBool(buffer, ref position, offset, false);
                break;
            case JsonTokenType.True:
                status = Lite3.ArrayAppendBool(buffer, ref position, offset, true);
                break;
            case JsonTokenType.Number:
            {
                status = reader.TryGetInt64(out var value)
                    ? Lite3.ArrayAppendLong(buffer, ref position, offset, value)
                    : Lite3.ArrayAppendDouble(buffer, ref position, offset, reader.GetDouble());
                break;
            }
            case JsonTokenType.String:
            {
                var value = ReadUtf8Value(arrayPool, ref reader, out var rentedBuffer);
                status = Lite3.ArrayAppendString(buffer, ref position, offset, value);
                if (rentedBuffer != null)
                    arrayPool.Return(rentedBuffer);
                break;
            }
            case JsonTokenType.StartObject:
            {
                if ((status = Lite3.ArrayAppendObject(buffer, ref position, offset, out var objectOffset)) >= 0)
                {
                    frames.Pop(arrayPool);
                    frames.Push(Frame.ForObject(objectOffset));
                }

                return status;
            }
            case JsonTokenType.StartArray:
            {
                if ((status = Lite3.ArrayAppendArray(buffer, ref position, offset, out var arrayOffset)) >= 0)
                {
                    frames.Pop(arrayPool);
                    frames.Push(Frame.ForArray(arrayOffset));
                }

                return status;
            }
            default:
                status = Lite3.Status.ExpectedJsonValue;
                break;
        }
        
        frames.Pop(arrayPool);

        return status;
    }

    private static KeyRef GetKeyRef(ArrayPool<byte> arrayPool, ref Utf8JsonReader reader)
    {
        var length = reader.HasValueSequence
            ? checked((int)reader.ValueSequence.Length)
            : reader.ValueSpan.Length;

        if (length <= InlineKey96.Capacity)
        {
            var inlineKey = new InlineKey96();

            length = reader.CopyString(inlineKey.AsSpan());
            inlineKey.SetLength(length);

            return new KeyRef
            {
                Kind = KeyRefKind.Inline,
                InlineKey = inlineKey
            };
        }
        
        var pooledKey = arrayPool.Rent(length);
        
        length = reader.CopyString(pooledKey);

        return new KeyRef
        {
            Kind = KeyRefKind.Pooled,
            PooledKey = pooledKey,
            PooledKeyLength = length
        };
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> ReadUtf8Value(
        ArrayPool<byte> arrayPool,
        ref Utf8JsonReader reader,
        out byte[]? rented)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped)
        {
            var length = reader.HasValueSequence
                ? checked((int)reader.ValueSequence.Length)
                : reader.ValueSpan.Length;
            
            rented = arrayPool.Rent(length);
            length = reader.CopyString(rented);
            
            return rented.AsSpan(0, length);
        }

        rented = null;
        return reader.ValueSpan;
    }

    [method: MethodImpl(MethodImplOptions.AggressiveInlining)]
    private struct FrameStack(Frame[] frames)
    {
        private Frame[] _frames = frames;
        private int _index = -1;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEmpty() => _index < 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Frame PeekRef() => ref _frames[_index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Lite3.Status Push(in Frame frame)
        {
            if (_index > _frames.Length - 1) return Lite3.Status.JsonNestingDepthExceededMax;
            _frames[++_index] = frame;
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Pop(ArrayPool<byte> arrayPool)
        {
            ref var frame = ref _frames[_index];
            
            if (frame is { Kind: FrameKind.ObjectSwitch })
                frame.KeyRef.Return(arrayPool);

            _frames[_index--] = default;
        }
    }

    private enum FrameKind : byte
    {
        Object,
        ObjectSwitch,
        Array,
        ArraySwitch
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct Frame
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Frame ForObject(int offset) => new() { Kind = FrameKind.Object, Offset = offset };
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Frame ForObjectSwitch(int offset, in KeyRef keyRef) => new() { Kind = FrameKind.ObjectSwitch, Offset = offset, KeyRef = keyRef };
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Frame ForArray(int offset) => new() { Kind = FrameKind.Array, Offset = offset };
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Frame ForArraySwitch(int offset) => new() { Kind = FrameKind.ArraySwitch, Offset = offset };
        
        public FrameKind Kind;
        public int Offset; 
        public KeyRef KeyRef;
    }

    private enum KeyRefKind
    {
        Inline,
        Pooled
    }
    
    private struct KeyRef
    {
        public KeyRefKind Kind;
        public InlineKey96 InlineKey;
        public byte[]? PooledKey;
        public int PooledKeyLength;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> AsSpan() => Kind switch
        {
            KeyRefKind.Inline => InlineKey.AsReadOnlySpan(),
            KeyRefKind.Pooled => PooledKey.AsSpan(0, PooledKeyLength),
            _ => throw new InvalidOperationException("No key is available.")
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(ArrayPool<byte> arrayPool)
        {
            if (Kind == KeyRefKind.Pooled && PooledKey != null)
                arrayPool.Return(PooledKey);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InlineKey96
    {
        public const int Capacity = 96;

        private byte _length;
        private ulong _0, _1, _2, _3, _4, _5, _6, _7, _8, _9, _10, _11;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetLength(int length) => _length = checked((byte)length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> AsReadOnlySpan() => AsSpan()[.._length];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> AsSpan() => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref _0, 12));
    }

    public sealed class DecodeResult(byte[] buffer, int position, bool isRentedBuffer, ArrayPool<byte> arrayPool)
        : IDisposable
    {
        public readonly byte[] Buffer = buffer;
        public readonly int Position = position;
        public readonly ArrayPool<byte> ArrayPool = arrayPool;

        public void Dispose()
        {
            if (isRentedBuffer)
                ArrayPool.Return(Buffer);
        }
    }
}