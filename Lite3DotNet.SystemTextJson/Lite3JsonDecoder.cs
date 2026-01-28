using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Lite3DotNet.SystemTextJson;

public static class Lite3JsonDecoder
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
    /// <param name="arrayPool">
    ///     Optional. The array pool for internal use; defaults to <see cref="ArrayPool{byte}.Shared" />.
    /// </param>
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
        
        var initialBuffer = arrayPool.Rent(Lite3Buffer.MinBufferSize);
        var (buffer, position) = DecodeImpl(initialBuffer, isRentedBuffer: true, growBuffer: true, utf8Json, arrayPool);
        
        return new DecodeResult(buffer, position, arrayPool: arrayPool);
    }
    
    /// <summary>
    ///     Convert from JSON asynchronously and write to a provided destination message buffer.
    /// </summary>
    /// <param name="pipeReader">The source pipe reader.</param>
    /// <param name="buffer">The destination message buffer.</param>
    /// <param name="minReadSize">Optional. The minimum source read size.</param>
    /// <param name="arrayPool">
    ///     Optional. The array pool for internal use; defaults to <see cref="ArrayPool{byte}.Shared" />.
    /// </param>
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
    /// <param name="arrayPool">
    ///     Optional. The array pool for resizing the output buffer; defaults to
    ///     <see cref="ArrayPool{byte}.Shared" />.
    /// </param>
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
        ArrayPool<byte>? arrayPool = null,
        CancellationToken cancellationToken = default)
    {
        arrayPool ??= ArrayPool<byte>.Shared;
        
        var initialBuffer = arrayPool.Rent(Lite3Buffer.MinBufferSize);
        
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
        ReadOnlySpan<byte> utf8Json,
        ArrayPool<byte> arrayPool)
    {
        var position = 0;
        var frames = ArrayPool<Frame>.Shared.Rent(FrameStackSize);
        var decodeState = new DecodeState(frames);

        try
        {
            var jsonReader = new Utf8JsonReader(utf8Json, isFinalBlock: true, new JsonReaderState());

            var status = DecodeCore(
                arrayPool,
                ref decodeState,
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
            if (decodeState.PendingKey is { } pendingKey)
                arrayPool.Return(pendingKey);
            
            if (isRentedBuffer)
                arrayPool.Return(buffer);
            throw;
        }
        finally
        {
            ArrayPool<Frame>.Shared.Return(frames);
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
        var frames = ArrayPool<Frame>.Shared.Rent(FrameStackSize);
        var decodeState = new DecodeState(frames);

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

                var status = DecodeCore(
                    arrayPool,
                    ref decodeState,
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
                pipeReader.AdvanceTo(readerBuffer.GetPosition(jsonReader.BytesConsumed));
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
    
    private static Lite3Core.Status DecodeCore(
        ArrayPool<byte> arrayPool,
        ref DecodeState state,
        ref byte[] buffer,
        bool isRentedBuffer,
        bool growBuffer,
        ref int position,
        ref Utf8JsonReader reader)
    {
        Lite3Core.Status status;
        
        if (position == 0)
        {
            if ((status = DecodeDocument(ref state, buffer, ref position, ref reader)) < 0)
            {
                // Note: resize and/or underflow should never happen on the first token
                throw status.AsException();
            }
        }
        
        var replayToken = false;

        do
        {
            ref var frame = ref state.Stack.PeekRef();

            switch (frame.Kind)
            {
                case FrameKind.Object:
                    status = DecodeObject(ref state, replayToken, ref reader);
                    break;
                
                case FrameKind.ObjectSwitch:
                    var rentedKey = default(byte[]?);
                    var key =
                        state.PendingKey != null ? state.PendingKey.AsSpan(0, state.PendingKeyLength) :
                        ReadUtf8(arrayPool, ref reader, out rentedKey);
                    
                    status = DecodeObjectSwitch(arrayPool, ref state, buffer, ref position, replayToken, key, ref reader);

                    if (status >= 0)
                    {
                        if (rentedKey != null)
                        {
                            arrayPool.Return(rentedKey);
                            break;
                        }

                        if (state.PendingKey != null)
                        {
                            arrayPool.Return(state.PendingKey);
                            state.PendingKey = null;
                        }
                        break;
                    }

                    if (status == Lite3Core.Status.NeedsMoreData && state.PendingKey == null)
                    {
                        if (rentedKey != null)
                        {
                            state.PendingKey = rentedKey;
                            state.PendingKeyLength = key.Length;
                            break;
                        }

                        state.PendingKey = arrayPool.Rent(key.Length);
                        state.PendingKeyLength = key.Length;
                        key.CopyTo(state.PendingKey);
                    }
                    break;
                
                case FrameKind.Array:
                    status = DecodeArray(ref state, replayToken, ref reader);
                    break;
                
                case FrameKind.ArraySwitch:
                    status = DecodeArraySwitch(arrayPool, ref state, buffer, ref position, ref reader);
                    break;
                
                default:
                    throw new InvalidDataException("Unknown frame kind.");
            }

            replayToken = false;

            if (status == Lite3Core.Status.InsufficientBuffer && growBuffer)
            {
                status = Lite3Buffer.Grow(arrayPool, isRentedBuffer, buffer, out buffer);
                isRentedBuffer = true;
                replayToken = true;
            }
            
            if (status < 0)
                return status;
        } while (!state.Stack.IsEmpty());

        return 0;
    }

    private static Lite3Core.Status DecodeDocument(
        ref DecodeState state,
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
                if ((status = Lite3Core.InitializeObject(buffer, out position)) < 0)
                    return status;

                return state.Stack.Push(new Frame(FrameKind.Object, 0));
            case JsonTokenType.StartArray:
                if ((status = Lite3Core.InitializeArray(buffer, out position)) < 0)
                    return status;

                return state.Stack.Push(new Frame(FrameKind.Array, 0));
            default:
                return Lite3Core.Status.ExpectedJsonArrayOrObject;
        }
    }
    
    private static Lite3Core.Status DecodeObject(
        ref DecodeState state,
        bool replayToken,
        ref Utf8JsonReader reader)
    {
        if (reader.CurrentDepth > JsonConstants.NestingDepthMax)
            return Lite3Core.Status.JsonNestingDepthExceededMax;
        
        ref var frame = ref state.Stack.PeekRef();
        var offset = frame.Offset;
        
        Debug.Assert(frame.Kind == FrameKind.Object);
        
        if (replayToken || reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                state.Stack.Pop();
                return 0;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
                return Lite3Core.Status.ExpectedJsonProperty;

            return state.Stack.Push(new Frame(FrameKind.ObjectSwitch, offset));
        }

        return Lite3Core.Status.NeedsMoreData;
    }
    
    private static Lite3Core.Status DecodeObjectSwitch(
        ArrayPool<byte> arrayPool,
        ref DecodeState state,
        byte[] buffer,
        ref int position,
        bool replayToken,
        scoped ReadOnlySpan<byte> key,
        ref Utf8JsonReader reader)
    {
        Lite3Core.Status status;
        
        ref var frame =  ref state.Stack.PeekRef();
        var offset = frame.Offset;
        
        var keyData = Lite3Core.GetKeyData(key);
        
        Debug.Assert(frame.Kind == FrameKind.ObjectSwitch);

        if (!replayToken && !reader.Read())
            return Lite3Core.Status.NeedsMoreData;
        
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
                var value = ReadUtf8(arrayPool, ref reader, out var rentedBuffer);
                status = Lite3Core.SetString(buffer, ref position, offset, key, keyData, value);
                if (rentedBuffer != null)
                    arrayPool.Return(rentedBuffer);
                break;
            }
            case JsonTokenType.StartObject:
            {
                if ((status = Lite3Core.SetObject(buffer, ref position, offset, key, keyData, out var objectOffset)) >= 0)
                {
                    state.Stack.Pop();
                    return state.Stack.Push(new Frame(FrameKind.Object, objectOffset));
                }

                return status;
            }
            case JsonTokenType.StartArray:
            {
                if ((status = Lite3Core.SetArray(buffer, ref position, offset, key, keyData, out var arrayOffset)) >= 0)
                {
                    state.Stack.Pop();
                    return state.Stack.Push(new Frame(FrameKind.Array, arrayOffset));
                }

                return status;
            }
            default:
                status = Lite3Core.Status.ExpectedJsonValue;
                break;
        }

        if (status < 0)
            return status;
        
        state.Stack.Pop();
        
        return status;
    }
    
    private static Lite3Core.Status DecodeArray(
        ref DecodeState state,
        bool replayToken,
        ref Utf8JsonReader reader)
    {
        if (reader.CurrentDepth > JsonConstants.NestingDepthMax)
            return Lite3Core.Status.JsonNestingDepthExceededMax;
        
        ref var frame = ref state.Stack.PeekRef();
        var offset = frame.Offset;
        
        Debug.Assert(frame.Kind == FrameKind.Array);
        
        if (replayToken || reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                state.Stack.Pop();
                return 0;
            }

            return state.Stack.Push(new Frame(FrameKind.ArraySwitch, offset));
        }
        
        return Lite3Core.Status.NeedsMoreData;
    }
    
    private static Lite3Core.Status DecodeArraySwitch(
        ArrayPool<byte> arrayPool,
        ref DecodeState state,
        byte[] buffer,
        ref int position,
        ref Utf8JsonReader reader)
    {
        Lite3Core.Status status;
        
        ref var frame =  ref state.Stack.PeekRef();
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
                var value = ReadUtf8(arrayPool, ref reader, out var rentedBuffer);
                status = Lite3Core.ArrayAppendString(buffer, ref position, offset, value);
                if (rentedBuffer != null)
                    arrayPool.Return(rentedBuffer);
                break;
            }
            case JsonTokenType.StartObject:
            {
                if ((status = Lite3Core.ArrayAppendObject(buffer, ref position, offset, out var objectOffset)) >= 0)
                {
                    state.Stack.Pop();
                    return state.Stack.Push(new Frame(FrameKind.Object, objectOffset));
                }

                return status;
            }
            case JsonTokenType.StartArray:
            {
                if ((status = Lite3Core.ArrayAppendArray(buffer, ref position, offset, out var arrayOffset)) >= 0)
                {
                    state.Stack.Pop();
                    return state.Stack.Push(new Frame(FrameKind.Array, arrayOffset));
                }

                return status;
            }
            default:
                status = Lite3Core.Status.ExpectedJsonValue;
                break;
        }
        
        state.Stack.Pop();

        return status;
    }
    
    private static int ReadUtf8Pooled(ref Utf8JsonReader reader, ArrayPool<byte> arrayPool, out byte[] buffer)
    {
        var length = reader.HasValueSequence
            ? checked((int)reader.ValueSequence.Length)
            : reader.ValueSpan.Length;
            
        buffer = arrayPool.Rent(length);
        return reader.CopyString(buffer);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> ReadUtf8(
        ArrayPool<byte> arrayPool,
        ref Utf8JsonReader reader,
        out byte[]? rentedBuffer)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped)
        {
            var length = ReadUtf8Pooled(ref reader, arrayPool, out rentedBuffer);
            return rentedBuffer.AsSpan(0, length);
        }

        rentedBuffer = null;
        return reader.ValueSpan;
    }

    private struct DecodeState(Frame[] frames)
    {
        public FrameStack Stack = new(frames);
        public byte[]? PendingKey;
        public int PendingKeyLength;
    }

    private struct FrameStack(Frame[] frames)
    {
        private Frame[] _frames = frames;
        private int _index = -1;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEmpty() => _index < 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Frame PeekRef() => ref _frames[_index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Lite3Core.Status Push(in Frame frame)
        {
            if (_index >= _frames.Length - 1) return Lite3Core.Status.JsonNestingDepthExceededMax;
            _frames[++_index] = frame;
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Pop() => _frames[_index--] = default;
    }

    private enum FrameKind : byte
    {
        Object,
        ObjectSwitch,
        Array,
        ArraySwitch
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct Frame(FrameKind kind, int offset)
    {
        public FrameKind Kind = kind;
        public int Offset = offset; 
    }

    public sealed class DecodeResult(byte[] buffer, int position, ArrayPool<byte> arrayPool)
        : IDisposable
    {
        public readonly byte[] Buffer = buffer;
        public readonly int Position = position;
        public readonly ArrayPool<byte> ArrayPool = arrayPool;

        public void Dispose() => ArrayPool.Return(Buffer);
    }
}