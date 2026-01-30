namespace Lite3DotNet;

public static partial class Lite3
{
    // Note that all public-facing Lite3 APIs and documentation comments are generated from the core implementation methods.
    
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
}