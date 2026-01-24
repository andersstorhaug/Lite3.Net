using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Lite3DotNet.Generators;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lite3DotNet;

public static unsafe partial class Lite3Core
{
    #region Logging
    private static ILogger _logger = NullLogger.Instance;
    
    public static void SetLogger(ILogger logger) => _logger = logger;
    
    [LoggerMessage(LogLevel.Debug, "{Message}"), Conditional("DEBUG")]
    private static partial void LogProbe(this ILogger logger, string message);
    
    [LoggerMessage(LogLevel.Error, "INVALID ARGUMENT: ARRAY INDEX {Index} OUT OF BOUNDS (size == {Size})")]
    private static partial void LogArrayIndexOutOfBounds(this ILogger logger, uint index, uint size);
    #endregion
    
    private const int NodeAlignment = 4;
    public const int NodeAlignmentMask = NodeAlignment - 1;
    
    public const int NodeSize = 96;
    private const int TreeHeightMax = 9;
    private const int NodeSizeKcOffset = 32;
    
    private const int NodeSizeShift = 6;
    private const uint NodeSizeMask = ~((1u << 6) - 1u);
    
    private const uint HashProbeMax = 128;

    public ref struct KeyData
    {
        public uint Hash;
        public uint Size;
    }
    
    /// <remarks>From <c>lite3_get_key_data</c></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Lite3Api(IsTryPattern = false)]
    public static KeyData GetKeyData(ReadOnlySpan<byte> key)
    {
        var hash = 5381u;
        foreach (var b in key)
            hash = (hash << 5) + hash + b;

        return new KeyData
        {
            Hash = hash,
            Size = (uint)key.Length + 1
        };
    }

    /// <summary>
    ///     Custom types of the library. All values are prefixed with a 1-byte type tag, similar to tagged unions.
    /// </summary>
    /// <remarks><em>Ported from C <c>lite3_types</c>.</em></remarks>
    public enum ValueKind : byte
    {
        /// <summary>Maps to JSON <c>null</c>.</summary>
        Null,
        
        /// <summary>Maps to JSON <c>boolean</c>.</summary>
        Bool,
        
        /// <summary>Maps to JSON <c>number</c>; the underlying datatype is <c>long</c>.</summary>
        I64,
        
        /// <summary>Maps to JSON <c>number</c>; the underlying datatype is <c>double</c>.</summary>
        F64,
        
        /// <summary>Converted to a Base64 string in JSON.</summary>
        Bytes,
        
        /// <summary>Maps to JSON <c>string</c>.</summary>
        String,
        
        /// <summary>Maps to JSON <c>object</c>.</summary>
        Object,
        
        /// <summary>Maps to JSON <c>array</c>.</summary>
        Array,
        
        /// <summary>Any type value equal or grater to this is considered invalid.</summary>
        Invalid,
        
        /// <summary>Not an actual type, only used as a marker.</summary>
        InvalidMarker
    }
    
    /// <summary>
    ///     <para>Represents a value inside a message buffer.</para>
    ///     <para>All values are prefixed with a 1-byte type tag, similar to tagged unions.</para>
    ///     <para>To discover types inside a message, compare against <see cref="ValueEntry.Type" /></para>
    /// </summary>
    /// <remarks><em>Ported from C <c>lite3_val</c>.</em></remarks>
    public readonly ref struct ReadOnlyValueEntry(ReadOnlySpan<byte> buffer, int offset)
    {
        private readonly ReadOnlySpan<byte> _buffer = buffer;
        internal ValueKind Type => (ValueKind)_buffer[Offset];
        public readonly int Offset = offset;
        internal readonly int ValueOffset = offset + ValueHeaderSize;
        internal ReadOnlySpan<byte> Value => _buffer[ValueOffset..];
    }
    
    /// <remarks><em>Ported from C <c>lite3_val</c>.</em></remarks>
    internal ref struct ValueEntry(Span<byte> buffer, int startOffset)
    {
        private Span<byte> _buffer = buffer;
        public ref byte Type => ref _buffer[startOffset];
        public Span<byte> Value => _buffer[(startOffset + ValueHeaderSize)..];
    }
    
    internal const int ValueHeaderSize = 1;
    
    /// <remarks><em>Ported from C <c>lite3_type_sizes</c>.</em></remarks>
    internal static readonly int[] ValueKindSizes =
    [
        0, // Null
        1, // Bool
        8, // I64
        8, // F64
        BytesLengthSize, // Bytes
        StringLengthSize, // String
        NodeSize - ValueHeaderSize, // Object
        NodeSize - ValueHeaderSize, // Array
        0 // Invalid
    ];

    static Lite3Core()
    {
        if (ValueKindSizes.Length != (int)ValueKind.InvalidMarker)
            throw new InvalidOperationException("lite3_type_sizes[] element count != LITE3_TYPE_COUNT");
    }

    /// <summary>
    ///     <para>Holds a reference to a bytes value inside a message buffer.</para>
    ///     <para>Buffers store an internal "generation count"; any mutations to the buffer will increment the count.</para>
    /// </summary>
    /// <remarks>
    ///     <para>Never use the offset against the buffer directly! Always use <c>GetBytes</c> for safe access!</para>
    ///     <para><em>Ported from C <c>lite3_bytes</c>.</em></para>
    /// </remarks>
    public ref struct BytesEntry(uint gen, int length, int offset)
    {
        /// <summary>Generation of the buffer when this was returned.</summary>
        public readonly uint Gen = gen;
        
        /// <summary>Byte array length in bytes.</summary>
        public readonly int Length = length;
        
        /// <summary>Byte array offset inside the message buffer.</summary>
        public readonly int Offset = offset;
    }

    internal const int BytesLengthSize = sizeof(int);
    
    /// <summary>
    ///     <para>Holds a reference to a string value inside a message buffer.</para>
    ///     <para>Returned by <see cref="Lite3Core.GetString" />.</para>
    ///     <para>Buffers store an internal "generation count"; any mutations to the buffer will increment the count.</para>
    /// </summary>
    /// <remarks>
    ///     <para>Never use the offset against the buffer directly! Always use <see cref="Lite3Core.GetUtf8Value" /> for safe access!</para>
    ///     <para><em>Ported from C <c>lite3_str</c>.</em></para>
    /// </remarks>
    public struct StringEntry(uint gen, int length, int offset)
    {
        /// <summary>Generation of the buffer when this was returned.</summary>
        public uint Gen = gen;
        
        /// <summary>String length in bytes, including a null-terminator.</summary>
        public int Length = length;
        
        /// <summary>String offset inside the message buffer.</summary>
        public int Offset = offset;
    }
    
    internal const int StringLengthSize = sizeof(int);
    
    /// <summary>
    ///     <para>Generational safe access wrapper.</para>
    ///     <para>
    ///         Every message buffer stores a generation count which is incremented on every mutation.
    ///         <see cref="BytesEntry" /> accessed through <c>GetBytes</c> will return a buffer if
    ///         <see cref="BytesEntry.Gen" /> matches the generation count of the buffer.
    ///         Otherwise, it returns NULL.
    ///     </para>
    ///     <para>
    ///         When a buffer structure is modified, offsets to data could be moved or deleted;
    ///         therefore it is no longer safe to use obtained offsets.
    ///     </para>
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="value">The <see cref="BytesEntry"/> entry.</param>
    /// <param name="result">On success, the value bytes; otherwise empty.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>LITE3_BYTES</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(result))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status GetBytes(ReadOnlySpan<byte> buffer, BytesEntry value, out ReadOnlySpan<byte> result)
    {
        if (value.Gen == BinaryPrimitives.ReadUInt32LittleEndian(buffer))
        {
            result = buffer.Slice(value.Offset, value.Length);
            return 0;
        }
        
        result = default;
        return Status.MutatedBuffer;
    }
    
    /// <summary>
    ///     <para>Generational safe access wrapper.</para>
    ///     <para>
    ///         Every message buffer stores a generation count which is incremented on every mutation.
    ///         <see cref="StringEntry" /> accessed through <c>GetString</c> will return a buffer if
    ///         <see cref="StringEntry.Gen" /> matches the generation count of the buffer.
    ///         Otherwise, it returns NULL.
    ///     </para>
    ///     <para>
    ///         When a buffer structure is modified, offsets to data could be moved or deleted;
    ///         therefore it is no longer safe to use obtained offsets.
    ///     </para>
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="value">The <see cref="BytesEntry"/> entry.</param>
    /// <param name="result">On success, the UTF-8 string bytes; otherwise empty.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>LITE3_STR</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(result))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status GetUtf8Value(ReadOnlySpan<byte> buffer, StringEntry value, out ReadOnlySpan<byte> result)
    {
        if (value.Gen == BinaryPrimitives.ReadUInt32LittleEndian(buffer))
        {
            result = buffer.Slice(value.Offset, value.Length);
            return 0;
        }
        
        result = default;
        return Status.MutatedBuffer;
    }

    /// <remarks><em>Ported from C <c>_lite3_verify_set</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Status VerifySet(ReadOnlySpan<byte> buffer, in int position, int offset)
    {
        if (position > buffer.Length)
        {
            _logger.LogError("INVALID ARGUMENT: position > buffer.Length");
            return Status.InsufficientBuffer;
        }
        if (NodeSize > position || offset > position - NodeSize)
        {
            _logger.LogError("INVALID ARGUMENT: START OFFSET OUT OF BOUNDS");
            return Status.StartOffsetOutOfBounds;
        }
        return 0;
    }
    
    /// <remarks><em>Ported from C <c>_lite3_verify_obj_set</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Status VerifyObjectSet(ReadOnlySpan<byte> buffer, in int position, int offset)
    {
        Status status;
        
        if ((status = VerifySet(buffer, position, offset)) < 0)
            return status;
        
        if (buffer[offset] != (byte)ValueKind.Object)
        {
            _logger.LogError("INVALID ARGUMENT: EXPECTING OBJECT TYPE");
            return Status.ExpectedObject;
        }
        
        return 0;
    }
    
    /// <remarks><em>Ported from C <c>_lite3_verify_arr_set</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Status VerifyArraySet(ReadOnlySpan<byte> buffer, in int position, int offset)
    {
        Status status;
        if ((status = VerifySet(buffer, position, offset)) < 0)
            return status;
        
        if (buffer[offset] != (byte)ValueKind.Array)
        {
            _logger.LogError("INVALID ARGUMENT: EXPECTING OBJECT TYPE");
            return Status.ExpectedObject;
        }
        
        return 0;
    }
    
    /// <summary>
    ///     Set null in object.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8 string.</param>
    /// <param name="keyData">The key data.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_set_null</c>.</em></remarks>
    [Lite3Api(KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status SetNull(Span<byte> buffer, ref int position, int offset, ReadOnlySpan<byte> key, KeyData keyData)
    {
        Status status;
        
        if ((status = VerifyObjectSet(buffer, position, offset)) < 0)
            return status;
        
        if ((status = SetImpl(buffer, ref position, offset, key, keyData, ValueKindSizes[(int)ValueKind.Null], out _, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.Null;
        return 0;
    }

    /// <summary>
    ///     Set boolean in object.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8 string.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="value">The value to set.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_set_bool</c>.</em></remarks>
    [Lite3Api(KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status SetBool(Span<byte> buffer, ref int position, int offset, ReadOnlySpan<byte> key, KeyData keyData, bool value)
    {
        Status status;
        
        if ((status = VerifyObjectSet(buffer, position, offset)) < 0)
            return status;
        
        if ((status = SetImpl(buffer, ref position, offset, key, keyData, ValueKindSizes[(int)ValueKind.Bool], out _, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.Bool;
        entry.Value[0] = value ? (byte)1 : (byte)0;
        return 0;
    }
    
    /// <summary>
    ///     Set integer in object.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8 string.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="value">The value to set.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_set_i64</c>.</em></remarks>
    [Lite3Api(KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status SetLong(Span<byte> buffer, ref int position, int offset, ReadOnlySpan<byte> key, KeyData keyData, long value)
    {
        Status status;
        
        if ((status = VerifyObjectSet(buffer, position, offset)) < 0)
            return status;
        
        if ((status = SetImpl(buffer, ref position, offset, key, keyData, ValueKindSizes[(int)ValueKind.I64], out _, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.I64;
        BinaryPrimitives.WriteInt64LittleEndian(entry.Value, value);
        return 0;
    }

    /// <summary>
    ///     Set floating point in object.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8 string.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="value">The value to set.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_set_f64</c>.</em></remarks>
    [Lite3Api(KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status SetDouble(Span<byte> buffer, ref int position, int offset, ReadOnlySpan<byte> key, KeyData keyData, double value)
    {
        Status status;
        
        if ((status = VerifyObjectSet(buffer, position, offset)) < 0)
            return status;
        
        if ((status = SetImpl(buffer, ref position, offset, key, keyData, ValueKindSizes[(int)ValueKind.F64], out _, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.F64;
        BinaryPrimitives.WriteDoubleLittleEndian(entry.Value, value);
        return 0;
    }

    /// <summary>
    ///     Set bytes in object.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8 string.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="value">The value UTF8 string.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_set_bytes</c>.</em></remarks>
    [Lite3Api(KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status SetBytes(Span<byte> buffer, ref int position, int offset, ReadOnlySpan<byte> key, KeyData keyData, ReadOnlySpan<byte> value)
    {
        Status status;
        
        if ((status = VerifyObjectSet(buffer, position, offset)) < 0)
            return status;
        
        if ((status = SetImpl(buffer, ref position, offset, key, keyData, ValueKindSizes[(int)ValueKind.Bytes] + value.Length, out _, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.Bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(entry.Value, (uint)value.Length);
        value.CopyTo(entry.Value.Slice(ValueKindSizes[(int)ValueKind.Bytes], value.Length));
        return 0;
    }
    
    /// <summary>
    ///     Set string in object.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8 string.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="value">The value to set.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_set_str</c>.</em></remarks>
    [Lite3Api(KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status SetString(Span<byte> buffer, ref int position, int offset, ReadOnlySpan<byte> key, in KeyData keyData, ReadOnlySpan<byte> value)
    {
        Status status;
        
        var stringSize = value.Length + 1;
        
        if ((status = VerifyObjectSet(buffer, position, offset)) < 0)
            return status;
        
        if ((status = SetImpl(buffer, ref position, offset, key, keyData, ValueKindSizes[(int)ValueKind.String] + stringSize, out _, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.String;
        BinaryPrimitives.WriteUInt32LittleEndian(entry.Value, (uint)stringSize);
        value.CopyTo(entry.Value.Slice(ValueKindSizes[(int)ValueKind.String], value.Length));
        
        // Insert NULL-terminator
        entry.Value[ValueKindSizes[(int)ValueKind.String] + value.Length] = 0x00;
        return 0;
    }

    /// <summary>
    ///     Set object in object.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8 string.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="objectOffset">The new object start offset.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_set_obj</c>.</em></remarks>
    [Lite3Api(KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status SetObject(Span<byte> buffer, ref int position, int offset, ReadOnlySpan<byte> key, KeyData keyData, out int objectOffset)
    {
        Status status;
        objectOffset = 0;
        
        if ((status = VerifyObjectSet(buffer, position, offset)) < 0)
            return status;
        
        return SetObjectImpl(buffer, ref position, offset, key, keyData, out objectOffset);
    }
    
    /// <summary>
    ///     Set array in object.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8 string.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="arrayOffset">The new object start offset.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_set_arr</c>.</em></remarks>
    [Lite3Api(KeyDataArg = nameof(keyData), ReturnArg = nameof(arrayOffset))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status SetArray(Span<byte> buffer, ref int position, int offset, ReadOnlySpan<byte> key, KeyData keyData, out int arrayOffset)
    {
        Status status;
        arrayOffset = 0;
        
        if ((status = VerifyObjectSet(buffer, position, offset)) < 0)
            return status;
        
        return SetArrayImpl(buffer, ref position, offset, key, keyData, out arrayOffset);
    }
    
    /// <remarks><em>Ported from C <c>_lite3_set_by_index</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Status SetByIndex(Span<byte> buffer, ref int position, int offset, uint index, int valueLength, out ValueEntry value)
    {
        Status status;
        value = default;
        
        if ((status = VerifyArraySet(buffer, position, offset)) < 0)
            return status;
        
        var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + NodeSizeKcOffset)..]) >> NodeSizeShift;
        
        if (index > size)
        {
            _logger.LogArrayIndexOutOfBounds(index, size);
            return Status.ArrayIndexOutOfBounds;
        }
        
        var keyData = new KeyData
        {
            Hash = index,
            Size = 0
        };
        
        return SetImpl(buffer, ref position, offset, key: default, keyData, valueLength, out _, out value);
    }
    
    /// <remarks><em>Ported from C <c>_lite3_set_by_append</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Status SetByAppend(Span<byte> buffer, ref int position, int offset, int valueLength, out ValueEntry value)
    {
        Status status;
        value = default;
        
        if ((status = VerifyArraySet(buffer, position, offset)) < 0)
            return status;
        
        var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + NodeSizeKcOffset)..]) >> NodeSizeShift;
        
        var keyData = new KeyData
        {
            Hash = size,
            Size = 0
        };
        
        return SetImpl(buffer, ref position, offset, key: default, keyData, valueLength, out _, out value);
    }
    
    /// <summary>
    ///     Append null to array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_append_null</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayAppendNull(Span<byte> buffer, ref int position, int offset)
    {
        Status status;
        
        var valueLength = ValueKindSizes[(int)ValueKind.Null];
        
        if ((status = SetByAppend(buffer, ref position, offset, valueLength, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.Null;
        return 0;
    }
    
    /// <summary>
    ///     Append boolean to array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="value">The value to append.</param>
    /// <returns><c>true</c> on success; otherwise <c>false</c>.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_append_bool</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayAppendBool(Span<byte> buffer, ref int position, int offset, bool value)
    {
        Status status;
        
        var valueLength = ValueKindSizes[(int)ValueKind.Bool];
        
        if ((status = SetByAppend(buffer, ref position, offset, valueLength, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.Bool;
        entry.Value[0] = value ? (byte)1 : (byte)0;
        return 0;
    }
    
    /// <summary>
    ///     Append integer to array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="value">The value to append.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_append_i64</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayAppendLong(Span<byte> buffer, ref int position, int offset, long value)
    {
        Status status;
        
        var valueLength = ValueKindSizes[(int)ValueKind.I64];
        
        if ((status = SetByAppend(buffer, ref position, offset, valueLength, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.I64;
        BinaryPrimitives.WriteInt64LittleEndian(entry.Value, value);
        return 0;
    }
    
    /// <summary>
    ///     Append floating point to array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="value">The value to append.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_append_f64</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayAppendDouble(Span<byte> buffer, ref int position, int offset, double value)
    {
        Status status;
        
        var valueLength = ValueKindSizes[(int)ValueKind.F64];
        
        if ((status = SetByAppend(buffer, ref position, offset, valueLength, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.F64;
        BinaryPrimitives.WriteDoubleLittleEndian(entry.Value, value);
        return 0;
    }
    
    /// <summary>
    ///     Append bytes to array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="value">The value to append.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_append_bytes</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayAppendBytes(Span<byte> buffer, ref int position, int offset, ReadOnlySpan<byte> value)
    {
        Status status;
        
        if ((status = SetByAppend(buffer, ref position, offset, ValueKindSizes[(int)ValueKind.Bytes] + value.Length, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.Bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(entry.Value, (uint)value.Length);
        value.CopyTo(entry.Value.Slice(ValueKindSizes[(int)ValueKind.Bytes], value.Length));
        return 0;
    }
    
    /// <summary>
    ///     Append string to array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="value">The value to append.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_append_str_n</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayAppendString(Span<byte> buffer, ref int position, int offset, ReadOnlySpan<byte> value)
    {
        Status status;
        
        var stringSize = value.Length + 1;
        
        if ((status = SetByAppend(buffer, ref position, offset, ValueKindSizes[(int)ValueKind.String] + stringSize, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.String;
        BinaryPrimitives.WriteUInt32LittleEndian(entry.Value, (uint)stringSize);
        value.CopyTo(entry.Value.Slice(ValueKindSizes[(int)ValueKind.String], value.Length));
        entry.Value[ValueKindSizes[(int)ValueKind.String] + value.Length] = 0x00;
        return 0;
    }
    
    /// <summary>
    ///     Append object to array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="objectOffset">The new object start offset.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_append_obj</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayAppendObject(Span<byte> buffer, ref int position, int offset, out int objectOffset)
    {
        Status status;
        objectOffset = 0;
        
        if ((status = VerifyArraySet(buffer, in position, offset)) < 0)
            return status;
        
        return ArrayAppendObjectImpl(buffer, ref position, offset, out objectOffset);
    }
    
    /// <summary>
    ///     Append array to array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="arrayOffset">The new array start offset.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_append_arr</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayAppendArray(Span<byte> buffer, ref int position, int offset, out int arrayOffset)
    {
        Status status;
        arrayOffset = 0;
        
        if ((status = VerifyArraySet(buffer, in position, offset)) < 0)
            return status;
        
        return ArrayAppendArrayImpl(buffer, ref position, offset, out arrayOffset);
    }
    
    /// <summary>
    ///     Set null in array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_set_null</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArraySetNull(Span<byte> buffer, ref int position, int offset, uint index)
    {
        Status status;
        
        if ((status = SetByIndex(buffer, ref position, offset, index, ValueKindSizes[(int)ValueKind.Null], out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.Null;
        return 0;
    }

    /// <summary>
    ///     Set boolean in array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="value">The value to set.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_set_bool</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArraySetBool(Span<byte> buffer, ref int position, int offset, uint index, bool value)
    {
        Status status;
        
        if ((status = SetByIndex(buffer, ref position, offset, index, ValueKindSizes[(int)ValueKind.Bool], out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.Bool;
        entry.Value[0] = value ? (byte)1 : (byte)0;
        return 0;
    }
    
    /// <summary>
    ///     Set integer in array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="value">The value to set.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_set_i64</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArraySetLong(Span<byte> buffer, ref int position, int offset, uint index, long value)
    {
        Status status;
        
        if ((status = SetByIndex(buffer, ref position, offset, index, ValueKindSizes[(int)ValueKind.I64], out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.I64;
        BinaryPrimitives.WriteInt64LittleEndian(entry.Value, value);
        return 0;
    }
    
    /// <summary>
    ///     Set floating point in array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="value">The value to set.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_set_f64</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArraySetDouble(Span<byte> buffer, ref int position, int offset, uint index, double value)
    {
        Status status;
        
        if ((status = SetByIndex(buffer, ref position, offset, index, ValueKindSizes[(int)ValueKind.F64], out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.F64;
        BinaryPrimitives.WriteDoubleLittleEndian(entry.Value, value);
        return 0;
    }

    /// <summary>
    ///     Set bytes in array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="value">The value to set.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_set_bytes</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArraySetBytes(Span<byte> buffer, ref int position, int offset, uint index, ReadOnlySpan<byte> value)
    {
        Status status;
        
        if ((status = SetByIndex(buffer, ref position, offset, index, ValueKindSizes[(int)ValueKind.Bytes] + value.Length, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.Bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(entry.Value, (uint)value.Length);
        value.CopyTo(entry.Value.Slice(ValueKindSizes[(int)ValueKind.Bytes], value.Length));
        return 0;
    }
    
    /// <summary>
    ///     Set string in array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="value">The value to set.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_set_str_n</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArraySetString(Span<byte> buffer, ref int position, int offset, uint index, ReadOnlySpan<byte> value)
    {
        Status status;
        
        var stringSize = value.Length + 1;
        
        if ((status = SetByIndex(buffer, ref position, offset,  index, ValueKindSizes[(int)ValueKind.String] + stringSize, out var entry)) < 0)
            return status;
        
        entry.Type = (byte)ValueKind.String;
        BinaryPrimitives.WriteUInt32LittleEndian(entry.Value, (uint)stringSize);
        value.CopyTo(entry.Value.Slice(ValueKindSizes[(int)ValueKind.String], value.Length));
        entry.Value[ValueKindSizes[(int)ValueKind.String] + value.Length] = 0x00; // Insert NULL-terminator
        return 0;
    }
    
    /// <summary>
    ///     Set object in array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="objectOffset">The new object start offset.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_set_obj</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArraySetObject(Span<byte> buffer, ref int position, int offset, uint index, out int objectOffset)
    {
        Status status;
        objectOffset = 0;
        
        if ((status = VerifyArraySet(buffer, in position, offset)) < 0)
            return status;
        
        return ArraySetObjectImpl(buffer, ref position, offset, index, out objectOffset);
    }
    
    /// <summary>
    ///     Set array in array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="arrayOffset">The new array start offset.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_set_arr</c>.</em></remarks>
    [Lite3Api]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArraySetArray(Span<byte> buffer, ref int position, int offset, uint index, out int arrayOffset)
    {
        Status status;
        arrayOffset = 0;
        
        if ((status = VerifyArraySet(buffer, in position, offset)) < 0)
            return status;
        
        return ArraySetArrayImpl(buffer, ref position, offset, index, out arrayOffset);
    }
    
    /// <remarks><em>Ported from C <c>_lite3_verify_get</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status VerifyGet(ReadOnlySpan<byte> buffer, int offset)
    {
        if (NodeSize > buffer.Length || offset > buffer.Length)
        {
            _logger.LogError("INVALID ARGUMENT: START OFFSET OUT OF BOUNDS");
            return Status.StartOffsetOutOfBounds;
        }
        return 0;
    }
    
    /// <remarks><em>Ported from C <c>_lite3_verify_obj_get</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Status VerifyObjectGet(ReadOnlySpan<byte> buffer, int offset)
    {
        Status status;
        
        if ((status = VerifyGet(buffer, offset)) < 0)
            return status;
        
        if (buffer[offset] != (byte)ValueKind.Object)
        {
            _logger.LogError("INVALID ARGUMENT: EXPECTING OBJECT TYPE");
            return Status.ExpectedObject;
        }
        
        return 0;
    }
    
    /// <remarks><em>Ported from C <c>_lite3_verify_arr_get</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Status VerifyArrayGet(ReadOnlySpan<byte> buffer, int offset)
    {
        Status status;
        
        if ((status = VerifyGet(buffer, offset)) < 0)
            return status;
        
        if (buffer[offset] != (byte)ValueKind.Array)
        {
            _logger.LogError("INVALID ARGUMENT: EXPECTING ARRAY TYPE");
            return Status.ExpectedArray;
        }
        
        return 0;
    }
    
    /// <remarks><em>Ported from C <c>_lite3_get_by_index</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Status GetByIndex(ReadOnlySpan<byte> buffer, int offset, uint index, out ReadOnlyValueEntry value)
    {
        Status status;
        value = default;
        
        if ((status = VerifyArrayGet(buffer, offset)) < 0)
            return status;
        
        var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + NodeSizeKcOffset)..]) >> NodeSizeShift;
        
        if (index >= size)
        {
            _logger.LogArrayIndexOutOfBounds(index, size);
            return Status.ArrayIndexOutOfBounds;
        }
        
        var keyData = new KeyData
        {
            Hash = index,
            Size = 0
        };
        
        return GetImpl(buffer, offset, key: default, keyData, out value);
    }

    /// <summary>Find value by key and return value type.</summary>
    /// <returns>The value type on success; otherwise <see cref="ValueKind.Invalid" />.</returns>
    /// <remarks><em>Ported from C <c>lite3_get_type</c>.</em></remarks>
    [Lite3Api(IsTryPattern = false, KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueKind GetValueKind(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData)
    {
        if (VerifyObjectGet(buffer, offset) < 0)
            return ValueKind.Invalid;
        
        if (GetImpl(buffer, offset, key, keyData, out var entry) < 0)
            return ValueKind.Invalid;
        
        return entry.Type;
    }

    /// <summary>
    ///     Find array value by index and return type.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <returns>The value type on success; otherwise <see cref="ValueKind.Invalid" />.</returns>
    /// <remarks><em>Ported from C <c>lite3_ctx_arr_get_type</c>.</em></remarks>
    [Lite3Api(IsTryPattern = false)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueKind ArrayGetValueKind(ReadOnlySpan<byte> buffer, int offset, uint index)
    {
        if (VerifyArrayGet(buffer, offset) < 0)
            return ValueKind.Invalid;

        if (GetByIndex(buffer, offset, index, out var value) < 0)
            return ValueKind.Invalid;
        
        return value.Type;
    }

    /// <summary>
    ///     Find value by key and write back type size
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="value">On return, the type size when successful; otherwise 0.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks>
    ///     <para>
    ///         For variable sized types like <see cref="ValueKind.Bytes" /> or <see cref="ValueKind.String" />, the number of
    ///         bytes (including NULL-terminator for string) are written back.
    ///     </para>
    ///     <para><em>Ported from C <c>lite3_get_type_size</c>.</em></para>
    /// </remarks>
    [Lite3Api(KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status GetValueSize(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData, out uint value)
    {
        Status status;
        value = 0;
        
        if ((status = VerifyObjectGet(buffer, offset)) < 0)
            return status;
        
        if ((GetImpl(buffer, offset, key, keyData, out var entry)) < 0)
            return status;
        
        var valueKind = entry.Type;
        if (valueKind is ValueKind.String or ValueKind.Bytes)
        {
            value = BinaryPrimitives.ReadUInt32LittleEndian(buffer[entry.ValueOffset..]);
            return 0;
        }
        
        value = (uint)ValueKindSizes[(int)valueKind];
        return 0;
    }

    /// <summary>
    ///     Attempt to find a key.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <returns><c>true</c> if successful the key exists or <c>false</c> if not; otherwise <c>false</c>.</returns>
    /// <remarks><em>Ported from C <c>lite3_exists</c>.</em></remarks>
    [Lite3Api(IsTryPattern = false, KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContainsKey(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData)
    {
        if (VerifyObjectGet(buffer, offset) < 0)
            return false;
        
        return GetImpl(buffer, offset, key, keyData, out _) >= 0;
    }

    /// <summary>
    ///     <para>Write back the number of object entries or array elements.</para>
    ///     <para>This function can be called on objects and arrays.</para>
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="value">On return, the count if successful; otherwise 0.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_count</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(value))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status GetCount(ReadOnlySpan<byte> buffer, int offset, out uint value)
    {
        Status status;
        value = 0;
        
        if ((status = VerifyGet(buffer, offset)) < 0)
            return status;
        
        var type = (ValueKind)buffer[offset];
        if (type is not (ValueKind.Object or ValueKind.Array))
        {
            _logger.LogError("INVALID ARGUMENT: EXPECTING ARRAY OR OBJECT TYPE");
            return Status.ExpectedArrayOrObject;
        }
        
        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + NodeSizeKcOffset)..]) >> NodeSizeShift;
        return 0;
    }

    /// <summary>
    ///     Find value by key and test for null type.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <returns><c>true</c> if successful and the value is null; otherwise <c>false</c>.</returns>
    /// <remarks><em>Ported from C <c>lite3_is_null</c>.</em></remarks>
    [Lite3Api(IsTryPattern = false, KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNull(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData)
    {
        if (VerifyObjectGet(buffer, offset) < 0)
            return false;
        
        if (GetImpl(buffer, offset, key, keyData, out var entry) < 0)
            return false;
        
        return entry.Type == ValueKind.Null;
    }

    /// <summary>
    ///     Find value by key and test for bool type.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <returns><c>true</c> if successful and the value type is bool; otherwise <c>false</c>.</returns>
    /// <remarks><em>Ported from C <c>lite3_is_bool</c>.</em></remarks>
    [Lite3Api(IsTryPattern = false, KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsBool(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData)
    {
        if (VerifyObjectGet(buffer, offset) < 0)
            return false;
        
        if (GetImpl(buffer, offset, key, keyData, out var entry) < 0)
            return false;
        
        return entry.Type == ValueKind.Bool;
    }

    /// <summary>
    ///     Find value by key and test for integer type.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <returns><c>true</c> if successful and the value type is integer; otherwise <c>false</c>.</returns>
    /// <remarks><em>Ported from C <c>lite3_is_i64</c>.</em></remarks>
    [Lite3Api(IsTryPattern = false, KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLong(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData)
    {
        if (VerifyObjectGet(buffer, offset) < 0)
            return false;
        
        if (GetImpl(buffer, offset, key, keyData, out var entry) < 0)
            return false;
        
        return entry.Type == ValueKind.I64;
    }

    /// <summary>
    ///     Find value by key and test for floating point type.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <returns><c>true</c> if successful and the value type is floating point; otherwise <c>false</c>.</returns>
    /// <remarks><em>Ported from C <c>lite3_is_f64</c>.</em></remarks>
    [Lite3Api(IsTryPattern = false, KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDouble(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData)
    {
        if (VerifyObjectGet(buffer, offset) < 0)
            return false;
        
        if (GetImpl(buffer, offset, key, keyData, out var entry) < 0)
            return false;
        
        return entry.Type == ValueKind.F64;
    }

    /// <summary>
    ///     Find value by key and test for bytes type.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <returns><c>true</c> if successful and the value type is bytes; otherwise <c>false</c>.</returns>
    /// <remarks><em>Ported from C <c>lite3_is_bytes</c>.</em></remarks>
    [Lite3Api(IsTryPattern = false, KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsBytes(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData)
    {
        if (VerifyObjectGet(buffer, offset) < 0)
            return false;
        
        if (GetImpl(buffer, offset, key, keyData, out var entry) < 0)
            return false;
        
        return entry.Type == ValueKind.Bytes;
    }

    /// <summary>
    ///     Find value by key and test for string type.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <returns><c>true</c> if successful and the value type is string; otherwise <c>false</c>.</returns>
    /// <remarks><em>Ported from C <c>lite3_is_str</c>.</em></remarks>
    [Lite3Api(IsTryPattern = false, KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsString(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData)
    {
        if (VerifyObjectGet(buffer, offset) < 0)
            return false;
        
        if (GetImpl(buffer, offset, key, keyData, out var entry) < 0)
            return false;
        
        return entry.Type == ValueKind.String;
    }

    /// <summary>
    ///     Find value by key and test for object type.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <returns><c>true</c> if successful and the value type is object; otherwise <c>false</c>.</returns>
    /// <remarks><em>Ported from C <c>lite3_is_obj</c>.</em></remarks>
    [Lite3Api(IsTryPattern = false, KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsObject(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData)
    {
        if (VerifyObjectGet(buffer, offset) < 0)
            return false;
        
        if (GetImpl(buffer, offset, key, keyData, out var entry) < 0)
            return false;
        
        return entry.Type == ValueKind.Object;
    }

    /// <summary>
    ///     Find value by key and test for array type.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <returns><c>true</c> if successful and the value type is array; otherwise <c>false</c>.</returns>
    /// <remarks><em>Ported from C <c>lite3_is_arr</c>.</em></remarks>
    [Lite3Api(IsTryPattern = false, KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsArray(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData)
    {
        if (VerifyObjectGet(buffer, offset) < 0)
            return false;
        
        if (GetImpl(buffer, offset, key, keyData, out var entry) < 0)
            return false;
        
        return entry.Type == ValueKind.Array;
    }

    /// <summary>
    ///     Get value from object.
    /// </summary>
    /// <returns><c>true</c> if successful; otherwise <c>false</c>.</returns>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="value">On return, the value entry when successful; otherwise default.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks>
    ///     <para>
    ///         Unlike other `lite3_get_xxx()` functions, this function does not get a specific type.
    ///         Instead, it produces a generic `lite3_val` pointer, which points to a value inside the Lite³ buffer.
    ///         This can be useful in cases where you don't know the exact value type beforehand. See @ref lite3_val_fns.
    ///     </para>
    ///     <para><em>Ported from C <c>lite3_get</c>.</em></para>
    /// </remarks>
    [Lite3Api(ReturnArg = nameof(value), KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status Get(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData, out ReadOnlyValueEntry value)
    {
        Status status;
        value = default;
        
        if ((status = VerifyGet(buffer, offset)) < 0)
            return status;
        
        return GetImpl(buffer, offset, key, keyData, out value);
    }

    /// <summary>
    ///     Get boolean value by key.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="value">On return, the boolean value when successful; otherwise false.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_get_bool</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(value), KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status GetBool(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData, out bool value)
    {
        Status status;
        value = false;
        
        if ((status = VerifyObjectGet(buffer, offset)) < 0)
            return status;
        
        if ((status = GetImpl(buffer, offset, key, keyData, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.Bool)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_BOOL");
            return Status.ValueKindDoesNotMatch;
        }
        
        value = buffer[entry.ValueOffset] == 1;
        return 0;
    }

    /// <summary>
    ///     Get integer value by key.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="value">On return, the integer value when successful; otherwise 0.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_get_i64</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(value), KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status GetLong(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData, out long value)
    {
        Status status;
        value = 0;
        
        if ((status = VerifyObjectGet(buffer, offset)) < 0)
            return status;
        
        if ((status = GetImpl(buffer, offset, key, keyData, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.I64)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_I64");
            return Status.ValueKindDoesNotMatch;
        }
        
        value = BinaryPrimitives.ReadInt64LittleEndian(buffer[entry.ValueOffset..]);
        return 0;
    }

    /// <summary>
    ///     Get floating point value by key.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="value">On return, the floating point value when successful; otherwise 0.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_get_f64</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(value), KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status GetDouble(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData, out double value)
    {
        Status status;
        value = 0;
        
        if ((status = VerifyObjectGet(buffer, offset)) < 0)
            return status;
        
        if ((status = GetImpl(buffer, offset, key, keyData, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.F64)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_F64");
            return Status.ValueKindDoesNotMatch;
        }
        
        value = BinaryPrimitives.ReadDoubleLittleEndian(buffer[entry.ValueOffset..]);
        return 0;
    }

    /// <summary>
    ///     Get bytes value by key.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="value">On return, the bytes value entry when successful; otherwise default.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_get_bytes</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(value), KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Status GetBytes(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData, out BytesEntry value)
    {
        Status status;
        value = default;
        
        if ((status = VerifyObjectGet(buffer, offset)) < 0)
            return status;
        
        if ((status = GetImpl(buffer, offset, key, keyData, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.Bytes)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_BYTES");
            return Status.ValueKindDoesNotMatch;
        }
        
        value = new BytesEntry(
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[entry.Offset..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[entry.ValueOffset..]),
            entry.ValueOffset
        );
        return 0;
    }

    /// <summary>
    ///     Get string value by key.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData"></param>
    /// <param name="value">On return, the string value entry when successful; otherwise empty.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_get_str_n</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(value), KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status GetString(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData, out StringEntry value)
    {
        Status status;
        value = default;
        
        if ((status = VerifyObjectGet(buffer, offset)) < 0)
            return status;
        
        if ((status = GetImpl(buffer, offset, key, keyData, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.String)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_STRING");
            return Status.ValueKindDoesNotMatch;
        }
        
        value = new StringEntry(
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            length: (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[entry.ValueOffset..]),
            entry.ValueOffset + ValueKindSizes[(int)ValueKind.String]);
        
        // Lite³ stores string size including NULL-terminator. Correction required for public API.
        --value.Length;
        return 0;
    }

    /// <summary>
    ///     Get object by key.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="objectOffset">On return, object start offset value when successful; otherwise 0.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_get_obj</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(objectOffset), KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status GetObject(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData, out int objectOffset)
    {
        Status status;
        objectOffset = 0;
        
        if ((status = VerifyObjectGet(buffer, offset)) < 0)
            return status;
        
        if ((status = GetImpl(buffer, offset, key, keyData, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.Object)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_OBJECT");
            return Status.ValueKindDoesNotMatch;
        }
        
        objectOffset = entry.Offset;
        return 0;
    }

    /// <summary>
    ///     Get array by key.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8.</param>
    /// <param name="keyData">The key data.</param>
    /// <param name="arrayOffset">On return, the array start offset when successful; otherwise 0.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_get_arr</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(arrayOffset), KeyDataArg = nameof(keyData))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status GetArray(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> key, KeyData keyData, out int arrayOffset)
    {
        Status status;
        arrayOffset = 0;
        
        if ((status = VerifyObjectGet(buffer, offset)) < 0)
            return status;
        
        if ((status = GetImpl(buffer, offset, key, keyData,  out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.Array)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_ARRAY");
            return Status.ValueKindDoesNotMatch;
        }
        
        arrayOffset = entry.Offset;
        return 0;
    }
    
    /// <summary>
    ///     Get boolean value by index.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="value">On return, the boolean value when successful; otherwise false.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_get_bool</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(value))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayGetBool(ReadOnlySpan<byte> buffer, int offset, uint index, out bool value)
    {
        Status status;
        value = false;
        
        if ((status = GetByIndex(buffer, offset, index, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.Bool)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_BOOL");
            return Status.ValueKindDoesNotMatch;
        }
        
        value = buffer[entry.ValueOffset] == 1;
        return 0;
    }
    
    /// <summary>
    ///     Get integer value by index.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="value">On return, the integer value when successful; otherwise 0.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_get_i64</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(value))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayGetLong(ReadOnlySpan<byte> buffer, int offset, uint index, out long value)
    {
        Status status;
        value = 0;
        
        if ((status = GetByIndex(buffer, offset, index, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.I64)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_I64");
            return Status.ValueKindDoesNotMatch;
        }
        
        value = BinaryPrimitives.ReadInt64LittleEndian(buffer[entry.ValueOffset..]);
        return 0;
    }

    /// <summary>
    ///     Get floating point value by index.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="value">On return, the floating point value when successful; otherwise 0.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_get_f64</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(value))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayGetDouble(ReadOnlySpan<byte> buffer, int offset, uint index, out double value)
    {
        Status status;
        value = 0;
        
        if ((status = GetByIndex(buffer, offset, index, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.F64)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_F64");
            return Status.ValueKindDoesNotMatch;
        }
        
        value = BinaryPrimitives.ReadDoubleLittleEndian(buffer[entry.ValueOffset..]);
        return 0;
    }

    /// <summary>
    ///     Get bytes value by index.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="value">On return, the bytes value entry when successful; otherwise default.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_get_bytes</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(value))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Status ArrayGetBytes(ReadOnlySpan<byte> buffer, int offset, uint index, out BytesEntry value)
    {
        Status status;
        value = default;
        
        if ((status = GetByIndex(buffer, offset, index, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.Bytes)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_BYTES");
            return Status.ValueKindDoesNotMatch;
        }
        
        value = new BytesEntry(
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[entry.ValueOffset..]),
            entry.ValueOffset + ValueKindSizes[(int)ValueKind.Bytes]);
        return 0;
    }

    /// <summary>
    ///     Get string value by index.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="value">On return, the string value entry when successful; otherwise default.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_get_str</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(value))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayGetString(ReadOnlySpan<byte> buffer, int offset, uint index, out StringEntry value)
    {
        Status status;
        value = default;
        
        if ((status = GetByIndex(buffer, offset, index, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.String)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_STRING");
            return Status.ValueKindDoesNotMatch;
        }
        
        value = new StringEntry(
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            length: (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[entry.ValueOffset..]),
            entry.ValueOffset + ValueKindSizes[(int)ValueKind.String]);
        
        // Lite³ stores string size including NULL-terminator. Correction required for public API.
        --value.Length;
        return 0;
    }

    /// <summary>
    ///     Get object by index.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="objectOffset">On return, the object start offset when successful; otherwise 0.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_get_obj</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(objectOffset))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayGetObject(ReadOnlySpan<byte> buffer, int offset, uint index, out int objectOffset)
    {
        Status status;
        objectOffset = 0;
        
        if ((status = GetByIndex(buffer, offset, index, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.Object)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_OBJECT");
            return Status.ValueKindDoesNotMatch;
        }
        
        objectOffset = entry.Offset;
        return 0;
    }

    /// <summary>
    ///     Get array by index.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="index">The array index.</param>
    /// <param name="arrayOffset">On return, the array start offset when successful; otherwise empty.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_arr_get_arr</c>.</em></remarks>
    [Lite3Api(ReturnArg = nameof(arrayOffset))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Status ArrayGetArray(ReadOnlySpan<byte> buffer, int offset, uint index, out int arrayOffset)
    {
        Status status;
        arrayOffset = 0;
        
        if ((status = GetByIndex(buffer, offset, index, out var entry)) < 0)
            return status;
        
        if (entry.Type != ValueKind.Array)
        {
            _logger.LogError("VALUE TYPE != LITE3_TYPE_ARRAY");
            return Status.ValueKindDoesNotMatch;
        }
        
        arrayOffset = entry.Offset;
        return 0;
    }
    
    /// <remarks><em>Ported from C <c>lite3_iter</c>.</em></remarks>
    internal ref struct Lite3Iterator
    {
        public uint Gen;
        public fixed uint NodeOffsets[TreeHeightMax + 1];
        public byte Depth;
        public fixed byte NodeI[TreeHeightMax + 1];
    }

    /// <summary>
    ///     Create an iterator for the given object or array.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="value">On return, the iterator when successful; otherwise default.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks><em>Ported from C <c>lite3_iter_create</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Status CreateIterator(ReadOnlySpan<byte> buffer, int offset, out Lite3Iterator value)
    {
        Status status;
        value = default;
        
        if ((status = VerifyGet(buffer, offset)) < 0)
            return status;
        
        return IteratorCreateImpl(buffer, offset, out value);
    }

    /// <summary>
    ///     Returns the value type of <see cref="entry" />.
    /// </summary>
    /// <param name="entry">The value entry.</param>
    /// <remarks><em>Ported from C <c>lite3_val_type</c>.</em></remarks>
    [Lite3Api(IsTryPattern = false)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueKind GetValueKind(in ReadOnlyValueEntry entry)
    {
        var type = entry.Type;
        return type < ValueKind.Invalid ? type : ValueKind.Invalid;
    }

    /// <summary>
    ///     Gets the size of the <see cref="entry" /> value type.
    /// </summary>
    /// <param name="entry">The value entry.</param>
    /// <remarks>
    ///     <para>
    ///         For variable sized types like LITE3_TYPE_BYTES or LITE3_TYPE_STRING, the number of bytes (including
    ///         NULL-terminator for string) are written back.
    ///     </para>
    ///     <para>
    ///         <b>Warning:</b>This function assumes you have a valid `lite3_val`. Passing an invalid value will return an
    ///         invalid size.
    ///     </para>
    ///     <para><em>Ported from C <c>lite3_val_type_size</c>.</em></para>
    /// </remarks>
    [Lite3Api(IsTryPattern = false)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetValueSize(in ReadOnlyValueEntry entry)
    {
        var type = entry.Type;
        
        if (type is ValueKind.String or ValueKind.Bytes)
            return BinaryPrimitives.ReadInt32LittleEndian(entry.Value);
        
        return ValueKindSizes[(int)entry.Type];
    }
    
    /// <remarks><em>Ported from C <c>lite3_val_is_null</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ValueIsNull(ReadOnlyValueEntry entry) => entry.Type == ValueKind.Null;
    
    /// <remarks><em>Ported from C <c>lite3_val_is_bool</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ValueIsBool(ReadOnlyValueEntry entry) => entry.Type == ValueKind.Bool;
    
    /// <remarks><em>Ported from C <c>lite3_val_is_i64</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ValueIsLong(ReadOnlyValueEntry entry) => entry.Type == ValueKind.I64;
    
    /// <remarks><em>Ported from C <c>lite3_val_is_f64</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ValueIsDouble(ReadOnlyValueEntry entry) => entry.Type == ValueKind.F64;
    
    /// <remarks><em>Ported from C <c>lite3_val_is_bytes</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ValueIsBytes(ReadOnlyValueEntry entry) => entry.Type == ValueKind.Bytes;
    
    /// <remarks><em>Ported from C <c>lite3_val_is_str</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ValueIsString(ReadOnlyValueEntry entry) => entry.Type == ValueKind.String;
    
    /// <remarks><em>Ported from C <c>lite3_val_is_obj</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ValueIsObject(ReadOnlyValueEntry entry) => entry.Type == ValueKind.Object;
    
    /// <remarks><em>Ported from C <c>lite3_val_is_arr</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ValueIsArray(ReadOnlyValueEntry entry) => entry.Type == ValueKind.Array;

    /// <remarks><em>Ported from C <c>lite3_val_bool</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool GetValueBool(ReadOnlyValueEntry entry)
    {
        return entry.Value[0] == 1;
    }
    
    /// <remarks><em>Ported from C <c>lite3_val_i64</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long GetValueLong(ReadOnlyValueEntry entry)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(entry.Value);
    }
    
    /// <remarks><em>Ported from C <c>lite3_val_f64</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double GetValueDouble(ReadOnlyValueEntry entry)
    {
        return BinaryPrimitives.ReadDoubleLittleEndian(entry.Value);
    }
    
    /// <remarks><em>Ported from C <c>lite3_val_str</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<byte> GetValueUtf8(ReadOnlyValueEntry entry)
    {
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Value);
        --length; // C#: no NULL-terminator
        return entry.Value.Slice(StringLengthSize, length);
    }
    
    /// <remarks><em>Ported from C <c>lite3_val_bytes</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<byte> GetValueBytes(ReadOnlyValueEntry entry)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(entry.Value);
        return entry.Value.Slice(BytesLengthSize, length);
    }
}