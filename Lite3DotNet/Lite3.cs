using System.Runtime.CompilerServices;
using System.Text;

namespace Lite3DotNet;

public static partial class Lite3
{
    /// <summary>
    ///     <para>Enumerate entries for objects or arrays.</para>
    ///     <para>
    ///         Reads the position to know the currently used portion of the buffer.
    ///     </para>
    ///     <para>
    ///         The <see cref="offset" /> is used to target an object or array inside the message buffer. To target the
    ///         root-level object or array, use an offset of 0.
    ///     </para>
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="withKey"><c>true</c> to read the key when enumerating.</param>
    /// <param name="withOffset"><c>true</c> to read the offset when enumerating</param>
    /// <remarks>
    ///     Read-only operations are thread-safe. Mixing reads and writes however is not thread-safe.
    /// </remarks>
    /// <returns>An enumerable struct.</returns>
    public static Lite3Enumerable Enumerate(ReadOnlySpan<byte> buffer, int offset, bool withKey = true, bool withOffset = true)
    {
        return new Lite3Enumerable(buffer, offset, withKey, withOffset);
    }

    extension(Lite3Core.StringEntry value)
    {
        /// <inheritdoc cref="Lite3Core.GetUtf8Value" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetUtf8Value(ReadOnlySpan<byte> buffer, out ReadOnlySpan<byte> result)
        {
            return Lite3Core.GetUtf8Value(buffer, value, out result) < 0;
        }
        
        /// <inheritdoc cref="Lite3Core.GetUtf8Value" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> GetUtf8Value(ReadOnlySpan<byte> buffer)
        {
            Lite3Core.Status status;
            return (status = Lite3Core.GetUtf8Value(buffer, value, out var result)) >= 0
                ? result
                : throw status.AsException();
        }
        
        /// <inheritdoc cref="Lite3Core.GetUtf8Value" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetUtf8Value(Lite3Context context, out ReadOnlySpan<byte> result)
        {
            return Lite3Core.GetUtf8Value(context.Buffer, value, out result) >= 0;
        }

        /// <inheritdoc cref="Lite3Core.GetUtf8Value" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> GetUtf8Value(Lite3Context context)
        {
            Lite3Core.Status status;
            return (status = Lite3Core.GetUtf8Value(context.Buffer, value, out var result)) >= 0
                ? result
                : throw status.AsException();
        }

        /// <summary>
        ///     <para><b><em>Do not use for performance-sensitive code</em></b>.</para>
        ///     <para>Convenience method for getting a converted .NET-native UTF-16 string.</para>
        /// </summary>
        /// <seealso cref="Lite3Core.GetUtf8Value"/>
        /// <param name="buffer">The message buffer.</param>
        /// <returns>The converted UTF-16 string value.</returns>
        public string GetStringValue(ReadOnlySpan<byte> buffer)
        {
            Lite3Core.Status status;
            return (status = Lite3Core.GetUtf8Value(buffer, value, out var result)) >= 0
                ? Encoding.UTF8.GetString(result)
                : throw status.AsException();
        }

        /// <summary>
        ///     <para><b><em>Do not use for performance-sensitive code</em></b>.</para>
        ///     <para>Convenience method for getting a converted .NET-native UTF-16 string.</para>
        /// </summary>
        /// <seealso cref="Lite3Core.GetUtf8Value"/>
        /// <param name="context">The context.</param>
        /// <returns>The converted UTF-16 string value.</returns>
        public string GetStringValue(Lite3Context context)
        {
            Lite3Core.Status status;
            return (status = Lite3Core.GetUtf8Value(context.Buffer, value, out var result)) >= 0
                ? Encoding.UTF8.GetString(result)
                : throw status.AsException();
        }
    }

    extension(Lite3Core.ReadOnlyValueEntry value)
    {
        /// <inheritdoc cref="Lite3Core.GetValueKind(in Lite3Core.ReadOnlyValueEntry)" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Lite3Core.ValueKind GetValueKind() => Lite3Core.GetValueKind(value);

        /// <inheritdoc cref="Lite3Core.GetValueSize" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetValueSize() => Lite3Core.GetValueSize(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetSize()
        {
            return Lite3Core.ValueHeaderSize +
                   value.Type switch
                   {
                       Lite3Core.ValueKind.String => Lite3Core.StringLengthSize,
                       Lite3Core.ValueKind.Bytes => Lite3Core.BytesLengthSize,
                       _ => 0
                   } +
                   Lite3Core.GetValueSize(value);
        }

        /// <inheritdoc cref="Lite3Core.ValueIsNull" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsNull() => Lite3Core.ValueIsNull(value);

        /// <inheritdoc cref="Lite3Core.ValueIsBool" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsBool() => Lite3Core.ValueIsBool(value);

        /// <inheritdoc cref="Lite3Core.ValueIsLong" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsLong() => Lite3Core.ValueIsLong(value);

        /// <inheritdoc cref="Lite3Core.ValueIsDouble" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsDouble() => Lite3Core.ValueIsDouble(value);

        /// <inheritdoc cref="Lite3Core.ValueIsBytes" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsBytes() => Lite3Core.ValueIsBytes(value);

        /// <inheritdoc cref="Lite3Core.ValueIsString" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsString() => Lite3Core.ValueIsString(value);

        /// <inheritdoc cref="Lite3Core.ValueIsObject" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsObject() => Lite3Core.ValueIsObject(value);

        /// <inheritdoc cref="Lite3Core.ValueIsArray" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsArray() => Lite3Core.ValueIsArray(value);

        /// <inheritdoc cref="Lite3Core.GetValueBool" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetBool() => Lite3Core.GetValueBool(value);

        /// <inheritdoc cref="Lite3Core.GetValueLong" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long GetLong() => Lite3Core.GetValueLong(value);

        /// <inheritdoc cref="Lite3Core.GetValueDouble" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double GetDouble() => Lite3Core.GetValueDouble(value);
        
        /// <inheritdoc cref="Lite3Core.GetUtf8Value" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> GetUtf8() => Lite3Core.GetValueUtf8(value);
        
        /// <summary>
        ///     <para><b><em>Do not use for performance-sensitive code</em></b>.</para>
        ///     <para>Convenience method for getting a converted .NET-native UTF-16 string.</para>
        /// </summary>
        /// <seealso cref="Lite3Core.GetUtf8Value"/>
        /// <returns>The converted UTF-16 string value.</returns>
        public string GetStringValue()
        {
            return Encoding.UTF8.GetString(Lite3Core.GetValueUtf8(value));
        }
        
        /// <inheritdoc cref="Lite3Core.GetValueBytes" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> GetBytes() => Lite3Core.GetValueBytes(value);
    }
}