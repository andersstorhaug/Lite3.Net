using System.Runtime.CompilerServices;
using System.Text;

namespace TronDotNet;

public static partial class Tron
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
    public static TronEnumerable Enumerate(ReadOnlySpan<byte> buffer, int offset, bool withKey = true, bool withOffset = true)
    {
        return new TronEnumerable(buffer, offset, withKey, withOffset);
    }

    extension(Lite3.StringEntry value)
    {
        /// <inheritdoc cref="Lite3.GetUtf8Value" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetUtf8Value(ReadOnlySpan<byte> buffer, out ReadOnlySpan<byte> result)
        {
            return Lite3.GetUtf8Value(buffer, value, out result) < 0;
        }
        
        /// <inheritdoc cref="Lite3.GetUtf8Value" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> GetUtf8Value(ReadOnlySpan<byte> buffer)
        {
            Lite3.Status status;
            return (status = Lite3.GetUtf8Value(buffer, value, out var result)) >= 0
                ? result
                : throw status.AsException();
        }
        
        /// <inheritdoc cref="Lite3.GetUtf8Value" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetUtf8Value(TronContext context, out ReadOnlySpan<byte> result)
        {
            return Lite3.GetUtf8Value(context.Buffer, value, out result) >= 0;
        }

        /// <inheritdoc cref="Lite3.GetUtf8Value" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> GetUtf8Value(TronContext context)
        {
            Lite3.Status status;
            return (status = Lite3.GetUtf8Value(context.Buffer, value, out var result)) >= 0
                ? result
                : throw status.AsException();
        }

        /// <summary>
        ///     <para><b><em>Do not use for performance-sensitive code</em></b>.</para>
        ///     <para>Convenience method for getting a converted .NET-native UTF-16 string.</para>
        /// </summary>
        /// <seealso cref="Lite3.GetUtf8Value"/>
        /// <param name="buffer">The message buffer.</param>
        /// <returns>The converted UTF-16 string value.</returns>
        public string GetStringValue(ReadOnlySpan<byte> buffer)
        {
            Lite3.Status status;
            return (status = Lite3.GetUtf8Value(buffer, value, out var result)) >= 0
                ? Encoding.UTF8.GetString(result)
                : throw status.AsException();
        }

        /// <summary>
        ///     <para><b><em>Do not use for performance-sensitive code</em></b>.</para>
        ///     <para>Convenience method for getting a converted .NET-native UTF-16 string.</para>
        /// </summary>
        /// <seealso cref="Lite3.GetUtf8Value"/>
        /// <param name="context">The context.</param>
        /// <returns>The converted UTF-16 string value.</returns>
        public string GetStringValue(TronContext context)
        {
            Lite3.Status status;
            return (status = Lite3.GetUtf8Value(context.Buffer, value, out var result)) >= 0
                ? Encoding.UTF8.GetString(result)
                : throw status.AsException();
        }
    }

    extension(Lite3.ReadOnlyValueEntry value)
    {
        /// <inheritdoc cref="Lite3.GetValueKind(in Lite3.ReadOnlyValueEntry)" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Lite3.ValueKind GetValueKind() => Lite3.GetValueKind(value);

        /// <inheritdoc cref="Lite3.GetValueKindSize" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetValueKindSize() => Lite3.GetValueKindSize(value);

        /// <inheritdoc cref="Lite3.ValueIsNull" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsNull() => Lite3.ValueIsNull(value);

        /// <inheritdoc cref="Lite3.ValueIsBool" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsBool() => Lite3.ValueIsBool(value);

        /// <inheritdoc cref="Lite3.ValueIsLong" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsLong() => Lite3.ValueIsLong(value);

        /// <inheritdoc cref="Lite3.ValueIsDouble" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsDouble() => Lite3.ValueIsDouble(value);

        /// <inheritdoc cref="Lite3.ValueIsBytes" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsBytes() => Lite3.ValueIsBytes(value);

        /// <inheritdoc cref="Lite3.ValueIsString" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsString() => Lite3.ValueIsString(value);

        /// <inheritdoc cref="Lite3.ValueIsObject" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsObject() => Lite3.ValueIsObject(value);

        /// <inheritdoc cref="Lite3.ValueIsArray" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsArray() => Lite3.ValueIsArray(value);

        /// <inheritdoc cref="Lite3.GetValueBool" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetBool() => Lite3.GetValueBool(value);

        /// <inheritdoc cref="Lite3.GetValueLong" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long GetLong() => Lite3.GetValueLong(value);

        /// <inheritdoc cref="Lite3.GetValueDouble" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double GetDouble() => Lite3.GetValueDouble(value);
        
        /// <inheritdoc cref="Lite3.GetUtf8Value" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> GetUtf8() => Lite3.GetValueUtf8(value);
        
        /// <summary>
        ///     <para><b><em>Do not use for performance-sensitive code</em></b>.</para>
        ///     <para>Convenience method for getting a converted .NET-native UTF-16 string.</para>
        /// </summary>
        /// <seealso cref="Lite3.GetUtf8Value"/>
        /// <param name="context">The context.</param>
        /// <returns>The converted UTF-16 string value.</returns>
        public string GetStringValue()
        {
            return Encoding.UTF8.GetString(Lite3.GetValueUtf8(value));
        }
        
        /// <inheritdoc cref="Lite3.GetValueBytes" />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> GetBytes() => Lite3.GetValueBytes(value);
    }
}