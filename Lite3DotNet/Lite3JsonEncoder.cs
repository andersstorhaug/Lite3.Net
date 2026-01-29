using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace Lite3DotNet;

public static class Lite3JsonEncoder
{
    /// <summary>
    ///     Encode a message buffer to a buffer writer.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="writer">The buffer writer.</param>
    /// <param name="options">Options for the JSON writer.</param>
    public static void Encode(ReadOnlySpan<byte> buffer, int offset, IBufferWriter<byte> writer, JsonWriterOptions options = default)
    {
        var jsonWriter = new Utf8JsonWriter(writer, options);

        Lite3Core.Status status;
        if ((status = EncodeDocument(buffer, offset, ref jsonWriter)) < 0)
            throw status.AsException();
        
        jsonWriter.Flush();
    }

    /// <summary>
    ///     Asynchronously encode a message buffer to a pipe writer.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="writer">The pipe writer.</param>
    /// <param name="options">Options for the JSON writer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resulting pipe writer's <see cref="FlushResult"/>.</returns>
    public static ValueTask<FlushResult> EncodeAsync(
        ReadOnlySpan<byte> buffer,
        int offset,
        PipeWriter writer,
        JsonWriterOptions options = default,
        CancellationToken cancellationToken = default)
    {
        var jsonWriter = new Utf8JsonWriter(writer, options);

        Lite3Core.Status status;
        return (status = EncodeDocument(buffer, offset, ref jsonWriter)) < 0
            ? throw status.AsException()
            : writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    ///     Encode the message to a UTF-16 .NET-native string.
    /// </summary>
    /// <param name="buffer">The message buffer.</param>
    /// <param name="offset">The start offset; 0 for root.</param>
    /// <param name="options">Options for the JSON writer.</param>
    /// <returns></returns>
    public static string EncodeString(ReadOnlySpan<byte> buffer, int offset, JsonWriterOptions options = default)
    {
        var writer = new ArrayBufferWriter<byte>();
        var jsonWriter = new Utf8JsonWriter(writer, options);

        Lite3Core.Status status;
        if ((status = EncodeDocument(buffer, offset, ref jsonWriter)) < 0)
            throw status.AsException();
        
        jsonWriter.Flush();
        
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }
    
    private static Lite3Core.Status EncodeSwitch(ReadOnlySpan<byte> buffer, int nestingDepth, Lite3Core.ValueEntry value, ref Utf8JsonWriter writer)
    {
        Lite3Core.Status status;
        
        switch (Lite3Core.GetValueKind(value))
        {
            case Lite3Core.ValueKind.Null:
                writer.WriteNullValue();
                break;
            case Lite3Core.ValueKind.Bool:
                writer.WriteBooleanValue(value.GetBool());
                break;
            case Lite3Core.ValueKind.I64:
                writer.WriteNumberValue(value.GetLong());
                break;
            case Lite3Core.ValueKind.F64:
                writer.WriteNumberValue(value.GetDouble());
                break;
            case Lite3Core.ValueKind.Bytes:
                writer.WriteBase64StringValue(value.GetBytes());
                break;
            case Lite3Core.ValueKind.String:
                writer.WriteStringValue(value.GetUtf8());
                break;
            case Lite3Core.ValueKind.Object:
                if ((status = EncodeObject(buffer, value.Offset, nestingDepth, ref writer)) < 0)
                    return status;
                break;
            case Lite3Core.ValueKind.Array:
                if ((status = EncodeArray(buffer, value.Offset, nestingDepth, ref writer)) < 0)
                    return status;
                break;
            default:
                return Lite3Core.Status.ExpectedJsonValue;
        }

        return 0;
    }

    private static Lite3Core.Status EncodeObject(ReadOnlySpan<byte> buffer, int offset, int nestingDepth, ref Utf8JsonWriter writer)
    {
        if (++nestingDepth > JsonConstants.NestingDepthMax)
            return Lite3Core.Status.JsonNestingDepthExceededMax;
        
        writer.WriteStartObject();
        
        foreach (var entry in Lite3.Enumerate(buffer, offset))
        {
            writer.WritePropertyName(entry.Key.GetUtf8Value(buffer));

            Lite3Core.Status status;
            if ((status = EncodeSwitch(buffer, nestingDepth, entry.GetValue(), ref writer)) < 0)
                return status;
        }
        
        writer.WriteEndObject();
        return 0;
    }

    private static Lite3Core.Status EncodeArray(ReadOnlySpan<byte> buffer, int offset, int nestingDepth, ref Utf8JsonWriter writer)
    {
        if (++nestingDepth > JsonConstants.NestingDepthMax)
            return Lite3Core.Status.JsonNestingDepthExceededMax;
        
        writer.WriteStartArray();

        foreach (var entry in Lite3.Enumerate(buffer, offset, withKey: false))
        {
            Lite3Core.Status status;
            if ((status = EncodeSwitch(buffer, nestingDepth, entry.GetValue(), ref writer)) < 0)
                return status;
        }
        
        writer.WriteEndArray();
        return 0;
    }

    private static Lite3Core.Status EncodeDocument(ReadOnlySpan<byte> buffer, int offset, ref Utf8JsonWriter writer)
    {
        Lite3Core.Status status;
        
        if ((status = Lite3Core.VerifyGet(buffer, offset)) < 0)
            return status;
        
        switch ((Lite3Core.ValueKind)buffer[offset])
        {
            case Lite3Core.ValueKind.Object:
                if ((status = EncodeObject(buffer, offset, nestingDepth: 0, ref writer)) < 0)
                    return status;
                break;
            case Lite3Core.ValueKind.Array:
                if ((status = EncodeArray(buffer, offset, nestingDepth: 0, ref writer)) < 0)
                    return status;
                break;
            default:
                return Lite3Core.Status.ExpectedJsonArrayOrObject;
        }

        return 0;
    }
}