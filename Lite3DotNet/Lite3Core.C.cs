using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Lite3DotNet.Generators;
using Microsoft.Extensions.Logging;

namespace Lite3DotNet;

public static unsafe partial class Lite3Core
{
    #region Logging
    [LoggerMessage(LogLevel.Debug, "GET key: {Hash}"), Conditional("DEBUG")]
    private static partial void LogGetKey(this ILogger logger, uint hash);
    
    [LoggerMessage(LogLevel.Debug, "GET index: {Hash}"), Conditional("DEBUG")]
    private static partial void LogGetIndex(this ILogger logger, uint hash);

    [LoggerMessage(LogLevel.Debug, "probe attempt: {Attempt}, hash {Hash}"), Conditional("DEBUG")]
    private static partial void LogProbeAttempt(this ILogger logger, uint attempt, uint hash);
    
    [LoggerMessage(LogLevel.Debug, "INITIALIZE {kind}"), Conditional("DEBUG")]
    private static partial void LogInitialize(this ILogger logger, ValueKind kind);
    
    [LoggerMessage(LogLevel.Debug, Message = "SET key: {Hash}"), Conditional("DEBUG")]
    private static partial void LogSetKey(this ILogger logger, uint hash);
    
    [LoggerMessage(LogLevel.Debug, Message = "SET index: {Hash}"), Conditional("DEBUG")]
    private static partial void LogSetIndex(this ILogger logger, uint hash);
    
    [LoggerMessage(LogLevel.Debug, Message = "i: {Iteration}, kc: {KeyCount}, node->hashes[i]: {Hash}"), Conditional("DEBUG")]
    private static partial void LogSetKeyCounts(this ILogger logger, int iteration, int keyCount, uint hash);
    
    [LoggerMessage(LogLevel.Debug, Message = "INSERTING HASH: {Hash}, i: {Iteration}"), Conditional("DEBUG")]
    private static partial void LogInsertHash(this ILogger logger, int iteration, uint hash);
    #endregion
    
    [StructLayout(LayoutKind.Sequential)]
    private struct Node
    {
        public uint GenType;
        public fixed uint Hashes[NodeHashesLength];
        public uint SizeKc;
        public fixed uint KvOffsets[NodeKeyValueOffsetsLength];
        public fixed uint ChildOffsets[NodeChildOffsetsLength];
    }
    
    private const int
        NodeHashesLength = 7,
        NodeKeyValueOffsetsLength = 7,
        NodeChildOffsetsLength = 8;
    
    private const byte NodeTypeMask = (1 << 8) - 1;

    private const int NodeGenShift = 8;
    private const uint NodeGenMask = ~((1u << 8) - 1u);

    private const int NodeKeyCountMax = NodeHashesLength;
    private const int NodeKeyCountMin = NodeKeyCountMax / 2;
    
    private const uint NodeKeyCountMask = (1u << 3) - 1u;
    
    private const int KeyTagSizeMax = 4;
    private const byte KeyTagSizeMask = (1 << 2) - 1;
    
    private const int KeyTagKeySizeShift = 2;

    /// <summary>
    ///     <para>Verify a key inside the buffer to ensure readers don't go out of bounds.</para>
    ///     <para>Optionally compare the existing key to an input key; a mismatch implies a hash collision.</para>
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="key">The key string. Optionally, call with empty.</param>
    /// <param name="keySize">The key size in bytes, including null-terminator. Optionally call with 0.</param>
    /// <param name="compareKeyTagSize">The key tag size in bytes. Optionally, call with 0.</param>
    /// <param name="offset">The key entry offset within the buffer.</param>
    /// <param name="keyTagSize">The actual key tag size.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks>
    ///     <em>Ported from C <c>_verify_key</c>.</em>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Status VerifyKey(
        ReadOnlySpan<byte> buffer,
        ReadOnlySpan<byte> key,
        int keySize,
        int compareKeyTagSize,
        ref int offset,
        out int keyTagSize)
    {
        keyTagSize = 0;
        
        if (KeyTagSizeMax > buffer.Length || offset > buffer.Length - KeyTagSizeMax)
        {
            _logger.LogError("KEY ENTRY OUT OF BOUNDS");
            return Status.KeyEntryOutOfBounds;
        }
        var actualKeyTagSize = (buffer[offset] & KeyTagSizeMask) + 1;
        if (compareKeyTagSize != 0)
        {
            if (compareKeyTagSize != actualKeyTagSize)
            {
                _logger.LogError("KEY TAG SIZE DOES NOT MATCH");
                return Status.KeyTagSizeDoesNotMatch;
            }
        }
        
        var keyEntrySize = 0;
        for (var i = 0; i < actualKeyTagSize; i++)
            keyEntrySize |= buffer[offset + i] << (8 * i);
        keyEntrySize >>= KeyTagKeySizeShift;
        offset += actualKeyTagSize;

        if (keyEntrySize > buffer.Length || offset > buffer.Length - keyEntrySize)
        {
            _logger.LogError("KEY ENTRY OUT OF BOUNDS");
            return Status.KeyEntryOutOfBounds;
        }
        if (keySize > 0)
        {
            var compareLength = keySize < keyEntrySize ? keySize : keyEntrySize;
            // C#: NULL-terminator not provided
            compareLength--;
            var comparison = buffer.Slice(offset, compareLength).SequenceCompareTo(key[..compareLength]);

            if (comparison != 0)
            {
                _logger.LogError("HASH COLLISION");
                return Status.KeyHashCollision;
            }
        }
        
        offset += keyEntrySize;
        keyTagSize = actualKeyTagSize;
        return 0;
    }

    /// <summary>
    ///     Verify a value inside the buffer to ensure readers don't go out of bounds.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The value entry offset within the buffer.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks>
    ///     <em>Ported from C <c>_verify_val</c>.</em>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Status VerifyValue(ReadOnlySpan<byte> buffer, ref int offset)
    {
        if (ValueHeaderSize > buffer.Length || offset > buffer.Length - ValueHeaderSize)
        {
            _logger.LogError("VALUE OUT OF BOUNDS");
            return Status.ValueOutOfBounds;
        }
        
        var kind = (ValueKind)buffer[offset];
        if (kind >= ValueKind.Invalid)
        {
            _logger.LogError("VALUE TYPE INVALID");
            return Status.ValueKindInvalid;
        }
        
        var valueEntrySize = ValueHeaderSize + ValueKindSizes[(int)kind];
        if (valueEntrySize > buffer.Length || offset > buffer.Length - valueEntrySize)
        {
            _logger.LogError("VALUE OUT OF BOUNDS");
            return Status.ValueOutOfBounds;
        }
        
        if (kind is ValueKind.String or ValueKind.Bytes)
        {
            var byteCount = 0;
            for (var i = 0; i < ValueKindSizes[(int)ValueKind.Bytes]; i++)
                byteCount |= buffer[offset + ValueHeaderSize + i] << (8 * i);
            
            valueEntrySize += byteCount;
            
            if (valueEntrySize > buffer.Length || offset > buffer.Length - valueEntrySize)
            {
                _logger.LogError("VALUE OUT OF BOUNDS");
                return Status.ValueOutOfBounds;
            }
        }
        
        offset += valueEntrySize;
        return 0;
    }

    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key UTF8 text.</param>
    /// <param name="keyData">The key hash data.</param>
    /// <param name="value">On return, the successfully retrieved value; otherwise default.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks>
    ///     <em>Ported from C <c>lite3_get_impl</c>.</em>
    /// </remarks>
    private static Status GetImpl(
        ReadOnlySpan<byte> buffer,
        int offset,
        ReadOnlySpan<byte> key,
        scoped in KeyData keyData,
        out ValueEntry value)
    {
        value = default;

        switch ((ValueKind)buffer[offset])
        {
            case ValueKind.Object:
                _logger.LogGetKey(keyData.Hash);
                break;
            case ValueKind.Array:
                _logger.LogGetIndex(keyData.Hash);
                break;
            default:
                _logger.LogError("GET INVALID: EXPECTING ARRAY OR OBJECT TYPE");
                return Status.ExpectedArrayOrObject;
        }

        var keyTagSize =
            ((keyData.Size >> (16 - KeyTagKeySizeShift) != 0 ? 1 : 0) << 1) +
            (keyData.Size >> (8 - KeyTagKeySizeShift) != 0 ? 1 : 0) +
            (keyData.Size != 0 ? 1 : 0);

        var probeAttempts = key.IsEmpty ? 1u : HashProbeMax;
        for (var attempt = 0u; attempt < probeAttempts; attempt++)
        {
            var attemptKey = keyData;
            attemptKey.Hash = keyData.Hash + attempt * attempt;
            _logger.LogProbeAttempt(attempt, attemptKey.Hash);
            
            if ((offset & NodeAlignmentMask) != 0)
            {
                _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
                return Status.NodeOffsetNotAligned;
            }
            
            var node = GetNodePtr(buffer, offset);

            var nodeWalks = 0;
            while (true)
            {
                var keyCount = (int)(node->SizeKc & NodeKeyCountMask);
                var i = 0;
                
                while (i < keyCount && node->Hashes[i] < attemptKey.Hash)
                    i++;
                
                // target key found
                if (i < keyCount && node->Hashes[i] == attemptKey.Hash)
                {
                    Status status;
                    
                    var targetOffset = (int)node->KvOffsets[i];
                    
                    if (!key.IsEmpty)
                    {
                        if ((status = VerifyKey(buffer, key, (int)attemptKey.Size, keyTagSize, ref targetOffset, out _)) < 0)
                        {
                            // try next probe
                            if (status == Status.KeyHashCollision)
                                break;

                            return status;
                        }
                    }
                    var valueStartOffset = targetOffset;
                    if ((status = VerifyValue(buffer, ref targetOffset)) < 0)
                        return status;
                    value = new ValueEntry(buffer, valueStartOffset);
                    return 0;
                }
                
                // if children, walk to next node
                if (node->ChildOffsets[0] != 0)
                {
                    var nextNodeOffset = (int)node->ChildOffsets[i];
                    
                    if ((nextNodeOffset & NodeAlignmentMask) != 0)
                    {
                        _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
                        return Status.NodeOffsetNotAligned;
                    }
                    node =  GetNodePtr(buffer, nextNodeOffset);
                    if (nextNodeOffset > buffer.Length - NodeSize)
                    {
                        _logger.LogError("NODE WALK OFFSET OUT OF BOUNDS");
                        return Status.NodeWalksOffsetOutOfBounds;
                    }
                    if (++nodeWalks > TreeHeightMax)
                    {
                        _logger.LogError("NODE WALKS EXCEEDED Lite3DotNet.TreeHeightMax");
                        return Status.NodeWalksExceededTreeHeightMax;
                    }
                }
                else
                {
                    _logger.LogError("KEY NOT FOUND");
                    return Status.KeyNotFound;
                }
            }
        }
        _logger.LogError("Lite3DotNet.HashProbeMax LIMIT REACHED");
        return Status.HashProbeLimitReached;
    }
    
    /// <remarks><em>Ported from C <c>lite3_iter_create_impl</c>.</em></remarks>
    private static Status IteratorCreateImpl(ReadOnlySpan<byte> buffer, int offset, out Lite3Iterator iterator)
    {
        iterator = default;
        
        _logger.LogProbe("CREATE ITER");

        if ((offset & NodeAlignmentMask) != 0)
        {
            _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
            return Status.NodeOffsetNotAligned;
        }

        var node = GetNodePtr(buffer, offset);

        var type = (ValueKind)(node->GenType & NodeTypeMask);
        if (type is not (ValueKind.Object or ValueKind.Array))
        {
            _logger.LogError("INVALID ARGUMENT: EXPECTING ARRAY OR OBJECT TYPE");
            return Status.ExpectedArrayOrObject;
        }

        iterator.Gen = GetNodePtr(buffer, 0)->GenType;
        iterator.Depth = 0;
        iterator.NodeOffsets[0] = (uint)offset;
        iterator.NodeI[0] = 0;

        while (node->ChildOffsets[0] != 0) // has children, travel down
        {
            var nextNodeOffset = (int)node->ChildOffsets[0];
            
            if ((nextNodeOffset & NodeAlignmentMask) != 0)
            {
                _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
                return Status.NodeOffsetNotAligned;
            }
            node = GetNodePtr(buffer, nextNodeOffset);
            if (++iterator.Depth > TreeHeightMax)
            {
                _logger.LogError("NODE WALKS EXCEEDED Lite3DotNet.TreeHeightMax");
                return Status.NodeWalksExceededTreeHeightMax;
            }
            if (nextNodeOffset > buffer.Length - NodeSize)
            {
                _logger.LogError("NODE WALK OFFSET OUT OF BOUNDS");
                return Status.NodeWalksOffsetOutOfBounds;
            }
            iterator.NodeOffsets[iterator.Depth] = (uint)nextNodeOffset;
            iterator.NodeI[iterator.Depth] = 0;
        }
        if (Sse.IsSupported)
        {
            fixed (byte* basePtr = buffer)
            {
                var kv = node->KvOffsets;
                Sse.Prefetch0(basePtr + kv[0]);
                Sse.Prefetch0(basePtr + kv[0] + 64);
                Sse.Prefetch0(basePtr + kv[1]);
                Sse.Prefetch0(basePtr + kv[1] + 64);
                Sse.Prefetch0(basePtr + kv[2]);
                Sse.Prefetch0(basePtr + kv[2] + 64);
            }
        }
        return 0;
    }

    /// <summary>
    ///     <para>
    ///         Get the next item from an iterator.
    ///         To use in conjunction with value functions; the <see cref="offset" /> can be used with
    ///         <see cref="ValueEntry" />.
    ///     </para>
    ///     <para>
    ///         <b>Warning</b>:
    ///         Iterators are read-only. Any attempt to write to the buffer will immediately invalidate the iterator.
    ///         If you need to make changes to the buffer, first prepare your changes, then apply them after in one batch.
    ///     </para>
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="iterator">The iterator to advance.</param>
    /// <param name="writeKey">Whether to provide the entry key.</param>
    /// <param name="key">The entry key.</param>
    /// <param name="writeOffset">Whether to provide container offset.</param>
    /// <param name="offset">The start offset.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks>
    ///     <em>Ported from C <c>lite3_iter_next</c>.</em>
    /// </remarks>
    internal static Status IteratorNext(
        ReadOnlySpan<byte> buffer,
        ref Lite3Iterator iterator,
        bool writeKey,
        out StringEntry key,
        bool writeOffset,
        out int offset)
    {
        key = default;
        offset = 0;
        Status status;
        
        if (iterator.Gen != GetNodePtr(buffer, 0)->GenType)
        {
            _logger.LogError("ITERATOR INVALID: iter->gen != node->gen_type (BUFFER MUTATION INVALIDATES ITERATORS");
            return Status.InvalidIterator;
        }

        Node* node;
        {
            var nextOffset = (int)iterator.NodeOffsets[iterator.Depth];
            if ((nextOffset & NodeAlignmentMask) != 0)
            {
                _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
                return Status.NodeOffsetNotAligned;
            }

            node = GetNodePtr(buffer, nextOffset);
        }

        var type = (ValueKind)(node->GenType & NodeTypeMask);
        if (type is not (ValueKind.Object or ValueKind.Array))
        {
            _logger.LogError("INVALID ARGUMENT: EXPECTING ARRAY OR OBJECT TYPE");
            return Status.ExpectedArrayOrObject;
        }
        
        // key_count reached, done
        if (iterator.Depth == 0 && iterator.NodeI[iterator.Depth] == (node->SizeKc & NodeKeyCountMask))
            return Status.IteratorDone;
        
        var targetOffset = (int)node->KvOffsets[iterator.NodeI[iterator.Depth]];

        // Write back key if requested
        if (type is ValueKind.Object && writeKey)
        {
            var keyStartOffset = targetOffset;
            if ((status = VerifyKey(buffer, ReadOnlySpan<byte>.Empty, 0, 0, ref targetOffset, out var keyTagSize)) < 0)
                return status;
            key.Gen = iterator.Gen;
            uint length = 0;
            for (var i = 0; i < keyTagSize; i++)
                length |= (uint)buffer[keyStartOffset + i] << (8 * i);
            key.Length = (int)(length >> KeyTagKeySizeShift);
            // Lite³ stores string size including NULL-terminator. Correction required for public API.
            --key.Length;
            key.Offset = keyStartOffset + keyTagSize;
        }
        
        // Write back value if requested
        if (writeOffset)
        {
            var valueStartOffset = targetOffset;
            if ((status = VerifyValue(buffer, ref targetOffset)) < 0)
                return status;
            offset = valueStartOffset;
        }
        
        ++iterator.NodeI[iterator.Depth];
        
        // has children, travel down
        while (node->ChildOffsets[iterator.NodeI[iterator.Depth]] != 0)
        {
            var nextNodeOffset = node->ChildOffsets[iterator.NodeI[iterator.Depth]];

            if ((nextNodeOffset & NodeAlignmentMask) != 0)
            {
                _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
                return Status.NodeOffsetNotAligned;
            }
            node = GetNodePtr(buffer, (int)nextNodeOffset);
            if (++iterator.Depth > TreeHeightMax)
            {
                _logger.LogError("NODE WALKS EXCEEDED Lite3DotNet.TreeHeightMax");
                return Status.NodeWalksExceededTreeHeightMax;
            }
            if (nextNodeOffset > buffer.Length - NodeSize)
            {
                _logger.LogError("NODE WALKS OFFSET OUT OF BOUNDS");
                return Status.NodeWalksOffsetOutOfBounds;
            }
            iterator.NodeOffsets[iterator.Depth] = nextNodeOffset;
            iterator.NodeI[iterator.Depth] = 0;
        }

        while (iterator.Depth > 0 && iterator.NodeI[iterator.Depth] == (node->SizeKc & NodeKeyCountMask))
        {
            --iterator.Depth;

            {
                var nextOffset = (int)iterator.NodeOffsets[iterator.Depth];
                if ((nextOffset & NodeAlignmentMask) != 0)
                {
                    _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
                    return Status.NodeOffsetNotAligned;
                } 
                node = GetNodePtr(buffer, nextOffset);
            }
            if (Sse.IsSupported)
            {
                fixed (byte* basePtr = buffer)
                {
                    // Prefetch next nodes
                    var baseOffset = iterator.NodeI[iterator.Depth];
                    Sse.Prefetch2(basePtr + node->ChildOffsets[(baseOffset + 1) & NodeKeyCountMask]);
                    Sse.Prefetch2(basePtr + node->ChildOffsets[(baseOffset + 1) & NodeKeyCountMask] + 64);
                    Sse.Prefetch2(basePtr + node->ChildOffsets[(baseOffset + 2) & NodeKeyCountMask]);
                    Sse.Prefetch2(basePtr + node->ChildOffsets[(baseOffset + 2) & NodeKeyCountMask] + 64);
                }
            }
        }
        if (Sse.IsSupported)
        {
            fixed (byte* basePtr = buffer)
            {
                var kv = node->KvOffsets;
                var baseOffset = iterator.NodeI[iterator.Depth];
                Sse.Prefetch0(basePtr + kv[(baseOffset + 0) & NodeKeyCountMask]);
                Sse.Prefetch0(basePtr + kv[(baseOffset + 0) & NodeKeyCountMask] + 64);
                Sse.Prefetch0(basePtr + kv[(baseOffset + 1) & NodeKeyCountMask]);
                Sse.Prefetch0(basePtr + kv[(baseOffset + 1) & NodeKeyCountMask] + 64);
                Sse.Prefetch0(basePtr + kv[(baseOffset + 2) & NodeKeyCountMask]);
                Sse.Prefetch0(basePtr + kv[(baseOffset + 2) & NodeKeyCountMask] + 64);
            }
        }
        return Status.IteratorItem;
    }
    
    /// <remarks><em>Ported from C <c>_lite3_init_impl</c>.</em></remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InitializeImpl(Span<byte> buffer, int offset, ValueKind kind)
    {
        _logger.LogInitialize(kind);
        
        var node = GetNodePtr(buffer, offset);
        node->GenType = (uint)((byte)kind & NodeTypeMask);
        node->SizeKc = 0x00;
        new Span<uint>(node->Hashes, NodeHashesLength).Clear();
        new Span<uint>(node->KvOffsets, NodeKeyValueOffsetsLength).Clear();
        new Span<uint>(node->ChildOffsets, NodeChildOffsetsLength).Clear();
    }

    /// <summary>
    ///     Initialize a message buffer as an object.
    ///     The available buffer space must be at least <see cref="NodeSize" />.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks>
    ///     <para>
    ///         This function can also be used to reset an existing message; the root node is simply replaced with an empty
    ///         object.
    ///     </para>
    ///     <para>
    ///         <em>Ported from C <c>lite3_init_obj</c>.</em>
    ///     </para>
    /// </remarks>
    [Lite3Api]
    public static Status InitializeObject(Span<byte> buffer, out int position)
    {
        position = 0;
        
        if (buffer.Length < NodeSize)
        {
            _logger.LogError("INVALID ARGUMENT: buf.Length < Lite3DotNet.NodeSize");
            return Status.InsufficientBuffer;
        }
        InitializeImpl(buffer, 0, ValueKind.Object);
        position = NodeSize;
        return 0;
    }

    /// <summary>
    ///     Initialize a message buffer as an array.
    ///     The available buffer space must be at least <see cref="NodeSize" />.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks>
    ///     <para>
    ///         This function can also be used to reset an existing message; the root node is simply replaced with an empty
    ///         array.
    ///     </para>
    ///     <para>
    ///         <em>Ported from C <c>lite3_init_arr</c>.</em>
    ///     </para>
    /// </remarks>
    [Lite3Api]
    public static Status InitializeArray(Span<byte> buffer, out int position)
    {
        position = 0;
        
        if (buffer.Length < NodeSize)
        {
            _logger.LogError("INVALID ARGUMENT: buf.Length < Lite3DotNet.NodeSize");
            return Status.InsufficientBuffer;
        }
        InitializeImpl(buffer, 0, ValueKind.Array);
        position = NodeSize;
        return 0;
    }

    /// <summary>
    ///     Inserts an entry into the message structure to prepare for writing of the actual value.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="position">The current buffer position.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="key">The key string; empty for arrays.</param>
    /// <param name="keyData">The key hash data.</param>
    /// <param name="valueLength">The length of the value in bytes.</param>
    /// <param name="valueStartOffset">The start offset of the value.</param>
    /// <param name="value">The value entry.</param>
    /// <returns><c>0</c> on success; otherwise a negative status code.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Note</b>: this function expects the caller to write to:
    ///         <list type="number">
    ///             <item><see cref="MutableValueEntry.Type" />: the value type (length of <see cref="ValueHeaderSize" />).</item>
    ///             <item><see cref="MutableValueEntry.Value" />: the actual value (length of <see cref="valueLength" />).</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         This has the advantage that the responsibility of type-specific logic is also moved to the caller.
    ///         Otherwise, this function would have to contain branches to account for all types.
    ///     </para>
    ///     <para>
    ///         <em>Ported from C <c>lite3_set_impl</c>.</em>
    ///     </para>
    /// </remarks>
    private static Status SetImpl(
        Span<byte> buffer,
        ref int position,
        int offset,
        ReadOnlySpan<byte> key,
        scoped in KeyData keyData,
        int valueLength,
        out int valueStartOffset,
        out MutableValueEntry value)
    {
        valueStartOffset = 0;
        value = default;

        switch ((ValueKind)buffer[offset])
        {
            case ValueKind.Object:
                _logger.LogSetKey(keyData.Hash);
                break;
            case ValueKind.Array:
                _logger.LogSetIndex(keyData.Hash);
                break;
            default:
                _logger.LogError("SET INVALID: EXPECTING ARRAY OR OBJECT TYPE");
                return Status.ExpectedArrayOrObject;
        }
        
        var keyTagSize =
            ((keyData.Size >> (16 - KeyTagKeySizeShift) != 0 ? 1 : 0) << 1) +
            (keyData.Size >> (8 - KeyTagKeySizeShift) != 0 ? 1 : 0) +
            (keyData.Size != 0 ? 1 : 0);
        var baseEntrySize = keyTagSize + (int)keyData.Size + ValueHeaderSize + valueLength;
        
        if ((offset & NodeAlignmentMask) != 0)
        {
            _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
            return Status.NodeOffsetNotAligned;
        }
        
        var root = GetNodePtr(buffer, offset);

        var gen = root->GenType >> NodeGenShift;
        ++gen;
        root->GenType = (root->GenType & ~NodeGenMask) | (gen << NodeGenShift);
        
        fixed (byte* basePtr = buffer)
        {
            var probeAttempts = key.IsEmpty ? 1u : HashProbeMax;
            for (var attempt = 0u; attempt < probeAttempts; attempt++)
            {
                var attemptKey = keyData;
                attemptKey.Hash = keyData.Hash + attempt * attempt;
                
                _logger.LogProbeAttempt(attempt, attemptKey.Hash);

                var entrySize = baseEntrySize;
                Node* parent = null;
                var node = root;

                var keyCount = 0;
                var i = 0;
                var nodeWalks = 0;

                while (true)
                {
                    var doMatchSkip = false;
                    
                    // node full, need to split
                    if ((node->SizeKc & NodeKeyCountMask) == NodeKeyCountMax)
                    {
                        // next multiple of NodeAlignment
                        var positionAligned = (position + NodeAlignmentMask) & ~NodeAlignmentMask;
                        var newNodeSize = parent != null ? NodeSize : 2 * NodeSize;

                        if (newNodeSize > buffer.Length || positionAligned > buffer.Length - newNodeSize)
                        {
                            _logger.LogError("NO BUFFER SPACE FOR NODE SPLIT");
                            return Status.InsufficientBuffer;
                        }
                        position = positionAligned;
                        // TODO_C: add lost bytes from alignment to GC index
                        
                        // if root split, create new root
                        if (parent == null)
                        {
                            _logger.LogProbe("NEW ROOT");
                            new ReadOnlySpan<byte>(node, NodeSize).CopyTo(buffer.Slice(position, NodeSize));

                            if ((position & NodeAlignmentMask) != 0)
                            {
                                _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
                                return Status.NodeOffsetNotAligned;
                            }
                            node = (Node*)(basePtr + position);

                            if ((offset & NodeAlignmentMask) != 0)
                            {
                                _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
                                return  Status.NodeOffsetNotAligned;
                            }
                            parent = (Node*)(basePtr + offset);
                            
                            new Span<uint>(parent->Hashes, NodeHashesLength).Clear();
                            new Span<uint>(parent->KvOffsets, NodeKeyValueOffsetsLength).Clear();
                            new Span<uint>(parent->ChildOffsets, NodeChildOffsetsLength).Clear();
                            // set key_count to 0
                            parent->SizeKc &= ~NodeKeyCountMask;
                            // insert node as child of new root
                            parent->ChildOffsets[0] = (uint)position;
                            position += NodeSize;
                            keyCount = 0;
                            i = 0;
                        }

                        _logger.LogProbe("SPLIT NODE");
                        // shift parent array before separator insert
                        for (var j = keyCount; j > i; j--)
                        {
                            parent->Hashes[j] = parent->Hashes[j - 1];
                            parent->KvOffsets[j] = parent->KvOffsets[j - 1];
                            parent->ChildOffsets[j + 1] = parent->ChildOffsets[j];
                        }
                        // insert new separator key in parent
                        parent->Hashes[i] = node->Hashes[NodeKeyCountMin];
                        parent->KvOffsets[i] = node->KvOffsets[NodeKeyCountMin];
                        // insert sibling as child in parent
                        parent->ChildOffsets[i + 1] = (uint)position;
                        // key_count++
                        parent->SizeKc = (parent->SizeKc & ~NodeKeyCountMask) |
                                                  ((parent->SizeKc + 1) & NodeKeyCountMask);
                        node->Hashes[NodeKeyCountMin] = 0;
                        node->KvOffsets[NodeKeyCountMin] = 0;

                        if ((position & NodeAlignmentMask) != 0)
                        {
                            _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
                            return Status.NodeOffsetNotAligned;
                        }

                        var siblingOffset = position;
                        var sibling = (Node*)(basePtr + siblingOffset);

                        new Span<uint>(sibling->Hashes, NodeHashesLength).Clear();
                        new Span<uint>(sibling->KvOffsets, NodeKeyValueOffsetsLength).Clear();
                        
                        sibling->GenType = ((Node*)(basePtr + offset))->GenType & NodeTypeMask;
                        sibling->SizeKc = NodeKeyCountMin & NodeKeyCountMask;
                        node->SizeKc = NodeKeyCountMin & NodeKeyCountMask;
                        new Span<uint>(sibling->ChildOffsets, NodeChildOffsetsLength).Clear();
                        // take child from node
                        sibling->ChildOffsets[0] = node->ChildOffsets[NodeKeyCountMin + 1];
                        node->ChildOffsets[NodeKeyCountMin + 1] = 0;
                        
                        // copy half of node's keys to sibling
                        for (var j = 0; j < NodeKeyCountMin; j++)
                        {
                            sibling->Hashes[j] = node->Hashes[j + NodeKeyCountMin + 1];
                            sibling->KvOffsets[j] = node->KvOffsets[j + NodeKeyCountMin + 1];
                            sibling->ChildOffsets[j + 1] = node->ChildOffsets[j + NodeKeyCountMin + 2];
                            
                            node->Hashes[j + NodeKeyCountMin + 1] = 0;
                            node->KvOffsets[j + NodeKeyCountMin + 1] = 0;
                            node->ChildOffsets[j + NodeKeyCountMin + 2] = 0;
                        }
                        position += NodeSize;
                        
                        // sibling has target key? then we follow
                        if (attemptKey.Hash > parent->Hashes[i])
                        {
                            if ((siblingOffset & NodeAlignmentMask) != 0)
                            {
                                _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
                                return Status.NodeOffsetNotAligned;
                            }
                            node = sibling;
                        }
                        else if (attemptKey.Hash == parent->Hashes[i])
                        {
                            node = parent;
                            _logger.LogProbe("GOTO SKIP");
                            doMatchSkip = true;
                        }
                    }

                    if (!doMatchSkip)
                    {
                        keyCount = (int)(node->SizeKc & NodeKeyCountMask);
                        i = 0;
                        while (i < keyCount && node->Hashes[i] < attemptKey.Hash)
                            i++;
                        
                        _logger.LogSetKeyCounts(i, keyCount, node->Hashes[i]);
                    }

                    var doInsertAppend = false;

                    if (doMatchSkip || (i < keyCount && node->Hashes[i] == attemptKey.Hash)) // matching key found, already exists?
                    {
                        Status status;
                        
                        var targetOffset = (int)node->KvOffsets[i];
                        var keyStartOffset = targetOffset;
                        if (!key.IsEmpty)
                        {
                            if ((status = VerifyKey(buffer[..position], key, (int)attemptKey.Size, keyTagSize, ref targetOffset, out _)) < 0)
                            {
                                // try next probe
                                if (status == Status.KeyHashCollision)
                                    break;

                                return status;
                            }
                        }
                        
                        valueStartOffset = targetOffset;
                        if ((status = VerifyValue(buffer[..position], ref targetOffset)) < 0)
                            return status;
                        
                        // value is too large, we must append
                        if (valueLength >= targetOffset - valueStartOffset)
                        {
                            var alignmentMask = valueLength == ValueKindSizes[(int)ValueKind.Object] ? NodeAlignmentMask : 0;
                            var unalignedValueOffset = position + keyTagSize + (int)attemptKey.Size;
                            var alignmentPadding = ((unalignedValueOffset + alignmentMask) & ~alignmentMask) - unalignedValueOffset;
                            entrySize += alignmentPadding;
                            if (entrySize > buffer.Length || position > buffer.Length - entrySize)
                            {
                                _logger.LogError("NO BUFFER SPACE FOR ENTRY INSERTION");
                                return Status.InsufficientBuffer;
                            }
                            // zero out key + value
                            buffer.Slice((int)node->KvOffsets[i], targetOffset - keyStartOffset).Clear();
                            buffer.Slice(position, alignmentPadding).Clear();
                            position += alignmentPadding;
                            node->KvOffsets[i] = (uint)position;
                            doInsertAppend = true;
                            // TODO_C: add lost bytes to GC index
                        }

                        if (!doInsertAppend)
                        {
                            // zero out value
                            buffer.Slice(valueStartOffset, targetOffset - valueStartOffset).Clear();
                            // caller overwrites value in place
                            value = new MutableValueEntry(buffer, valueStartOffset);
                            // TODO_C: add lost bytes to GC index
                            return 0;
                        }
                    }

                    if (!doInsertAppend)
                    {
                        // if children, walk to next node
                        if (node->ChildOffsets[0] != 0)
                        {
                            var nextNodeOffset = (int)node->ChildOffsets[i];

                            if ((nextNodeOffset & NodeAlignmentMask) != 0)
                            {
                                _logger.LogError("NODE OFFSET NOT ALIGNED TO LITE3_NODE_ALIGNMENT");
                                return Status.NodeOffsetNotAligned;
                            }
                            if (nextNodeOffset > position - NodeSize)
                            {
                                _logger.LogError("NODE WALK OFFSET OUT OF BOUNDS");
                                return Status.NodeWalksOffsetOutOfBounds;
                            }
                            if (++nodeWalks > TreeHeightMax)
                            {
                                _logger.LogError("NODE WALKS EXCEEDED TreeHeightMax");
                                return Status.NodeWalksExceededTreeHeightMax;
                            }
                            parent = node;
                            node = (Node*)(basePtr + nextNodeOffset);
                        }
                        // insert the kv-pair
                        else
                        {
                            var alignmentMask = valueLength == ValueKindSizes[(int)ValueKind.Object]
                                ? NodeAlignmentMask
                                : 0;
                            var unalignedValueOffset = position + keyTagSize + (int)attemptKey.Size;
                            var alignmentPadding = ((unalignedValueOffset + alignmentMask) & ~alignmentMask) - unalignedValueOffset;
                            entrySize += alignmentPadding;
                            if (entrySize > buffer.Length || position > buffer.Length - entrySize)
                            {
                                _logger.LogError("NO BUFFER SPACE FOR ENTRY INSERTION");
                                return Status.InsufficientBuffer;
                            }
                            for (var j = keyCount; j > i; j--)
                            {
                                node->Hashes[j] = node->Hashes[j - 1];
                                node->KvOffsets[j] = node->KvOffsets[j - 1];
                            }
                            
                            _logger.LogInsertHash(i, attemptKey.Hash);
                            
                            node->Hashes[i] = attemptKey.Hash;
                            // key_count++
                            node->SizeKc = (node->SizeKc & ~NodeKeyCountMask)
                                           | ((node->SizeKc + 1) & NodeKeyCountMask);
                            buffer.Slice(position, alignmentPadding).Clear();
                            position += alignmentPadding;
                            node->KvOffsets[i] = (uint)position;

                            // set node to root
                            root = (Node*)(basePtr + offset);
                            var size = root->SizeKc >> NodeSizeShift;
                            ++size;
                            root->SizeKc = (root->SizeKc & ~NodeSizeMask) | (size << NodeSizeShift); // node size++
                            doInsertAppend = true;
                        }
                    }

                    if (!doInsertAppend)
                        continue;

                    if (!key.IsEmpty)
                    {
                        var keySizeTmp = (attemptKey.Size << KeyTagKeySizeShift) | ((uint)keyTagSize - 1);
                        for (var j = 0; j < keyTagSize; j++)
                            buffer[position + j] = (byte)(keySizeTmp >> (8 * j));
                        
                        position += keyTagSize;
                        
                        // C#: no NULL-terminator
                        key[..((int)attemptKey.Size - 1)].CopyTo(buffer[position..]);
                        buffer[position + ((int)attemptKey.Size - 1)] = 0x0;
                        position += (int)attemptKey.Size;
                    }
                    
                    valueStartOffset = position;
                    value = new MutableValueEntry(buffer, position);
                    position += ValueHeaderSize + valueLength;
                    
                    _logger.LogProbe("OK");
                    return 0;
                }
            }
            
            _logger.LogError("LITE3_HASH_PROBE_MAX LIMIT REACHED");
            return Status.HashProbeLimitReached;
        }
    }
    
    /// <remarks><em>Ported from C <c>lite3_set_obj_impl</c>.</em></remarks>
    private static Status SetObjectImpl(
        Span<byte> buffer,
        ref int position,
        int offset,
        ReadOnlySpan<byte> key,
        scoped in KeyData keyData,
        out int objectOffset)
    {
        Status status;
        
        if ((status = SetImpl(buffer, ref position, offset, key, keyData, ValueKindSizes[(int)ValueKind.Object], out objectOffset, out _)) < 0)
            return status;
        
        InitializeImpl(buffer, objectOffset, ValueKind.Object);
        return 0;
    }

    /// <remarks><em>Ported from C <c>lite3_set_arr_impl</c>.</em></remarks>
    private static Status SetArrayImpl(
        Span<byte> buffer,
        ref int position,
        int offset,
        ReadOnlySpan<byte> key,
        scoped in KeyData keyData,
        out int arrayOffset)
    {
        Status status;
        
        if ((status = SetImpl(buffer, ref position, offset, key, keyData, ValueKindSizes[(int)ValueKind.Array], out arrayOffset, out _)) < 0)
            return status;
        
        InitializeImpl(buffer, arrayOffset, ValueKind.Array);
        return 0;
    }

    /// <remarks><em>Ported from C <c>lite3_arr_append_obj_impl</c>.</em></remarks>
    private static Status ArrayAppendObjectImpl(
        Span<byte> buffer,
        ref int position,
        int offset,
        out int objectOffset)
    {
        Status status;
        
        var size = GetNodePtr(buffer, offset)->SizeKc >> NodeSizeShift;
        
        var keyData = new KeyData
        {
            Hash = size,
            Size = 0
        };
        
        if ((status = SetImpl(buffer, ref position, offset, ReadOnlySpan<byte>.Empty, keyData, ValueKindSizes[(int)ValueKind.Object], out objectOffset, out _)) < 0)
            return status;
        
        InitializeImpl(buffer, objectOffset, ValueKind.Object);
        return 0;
    }
    
    /// <remarks><em>Ported from C <c>lite3_arr_append_arr_impl</c>.</em></remarks>
    private static Status ArrayAppendArrayImpl(
        Span<byte> buffer,
        ref int position,
        int offset,
        out int arrayOffset)
    {
        Status status;
        
        var size = GetNodePtr(buffer, offset)->SizeKc >> NodeSizeShift;
        
        var keyData = new KeyData
        {
            Hash = size,
            Size = 0
        };
        
        if ((status = SetImpl(buffer, ref position, offset, ReadOnlySpan<byte>.Empty, keyData, ValueKindSizes[(int)ValueKind.Array], out arrayOffset, out _)) < 0)
            return status;
        
        InitializeImpl(buffer, arrayOffset, ValueKind.Array);
        return 0;
    }

    /// <remarks><em>Ported from C <c>lite3_arr_set_obj_impl</c>.</em></remarks>
    private static Status ArraySetObjectImpl(
        Span<byte> buffer,
        ref int position,
        int offset,
        uint index,
        out int objectOffset)
    {
        Status status;
        
        var size = GetNodePtr(buffer, offset)->SizeKc >> NodeSizeShift;
        if (index > size)
        {
            objectOffset = 0;
            _logger.LogArrayIndexOutOfBounds(index, size);
            return Status.ArrayIndexOutOfBounds;
        }
        
        var keyData = new KeyData
        {
            Hash = index,
            Size = 0
        };
        
        if ((status = SetImpl(buffer, ref position, offset, ReadOnlySpan<byte>.Empty, keyData, ValueKindSizes[(int)ValueKind.Object], out objectOffset, out _)) < 0)
            return status;
        
        InitializeImpl(buffer, objectOffset, ValueKind.Object);
        return 0;
    }

    /// <remarks><em>Ported from C <c>lite3_arr_set_arr_impl</c>.</em></remarks>
    private static Status ArraySetArrayImpl(
        Span<byte> buffer,
        ref int position,
        int offset,
        uint index,
        out int arrayOffset)
    {
        Status status;
        arrayOffset = 0;
        
        var node = GetNodePtr(buffer, offset);
        var size = node->SizeKc >> NodeSizeShift;

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

        if ((status = SetImpl(buffer, ref position, offset, ReadOnlySpan<byte>.Empty, keyData, ValueKindSizes[(int)ValueKind.Array], out arrayOffset, out _)) < 0)
            return status;

        InitializeImpl(buffer, arrayOffset, ValueKind.Array);
        return 0;
    }
}