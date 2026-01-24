using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using TronDotNet.SystemTextJson;
using Xunit.Abstractions;
using static TronDotNet.Lite3;

namespace TronDotNet.Tests;

/// <remarks>
///     Ported from C <c>context_api</c> examples.
/// </remarks>
public class ContextApiExamples(ITestOutputHelper output)
{
    /// <remarks>
    ///     Ported from <c>01_building-messages.c</c>.
    /// </remarks>
    [Fact]
    public void Can_build_messages()
    {
        var context = TronContext.Create();
        using var scope = context.BeginScope();

        // Build message
        context
            .InitializeObject()
            .SetString(0, "event"u8, "lap_complete"u8)
            .SetLong(0, "lap"u8, 55)
            .SetDouble(0, "time_sec"u8, 88.427);

        output.WriteLine($"position: {context.Position}");
        output.WriteLine(TronJsonEncoder.EncodeString(context.WrittenBuffer, 0));
        
        output.WriteLine("updating lap count");
        context.SetLong(0, "lap"u8, 56);
        
        output.WriteLine("Data to send");
        output.WriteLine($"buflen: {context.Position}");
        output.WriteLine(TronJsonEncoder.EncodeString(context.WrittenBuffer, 0));
        
        // Transmit data / copy to new context
        var receiveContext = TronContext.CreateFrom(context.Buffer, context.Position);
        using var transmitScope = receiveContext.BeginScope();
        
        // Mutate (zero-copy, no parsing)
        output.WriteLine("Verifying fastest lap");
        receiveContext
            .SetString(0, "verified"u8, "race_control"u8)
            .SetBool(0, "fastest_lap"u8, true);
        
        output.WriteLine("Modified data:");
        output.WriteLine($"rx position: {receiveContext.Position}");
        output.WriteLine(TronJsonEncoder.EncodeString(receiveContext.WrittenBuffer, 0));
        
        // Ready to send
    }

    /// <remarks>
    ///     Ported from <c>02_reading-messages.c</c>.
    /// </remarks>
    [Fact]
    public void Can_read_messages()
    {
        var context = TronContext.Create();
        using var scope = context.BeginScope();
        
        // Build Message
        context
            .InitializeObject()
            .SetString(0, "title"u8, "C Programming Language, 2nd Edition"u8)
            .SetString(0, "language"u8, "en"u8)
            .SetDouble(0, "price_usd"u8, 60.30)
            .SetLong(0, "pages"u8, 272)
            .SetBool(0, "in_stock"u8, true)
            .SetNull(0, "reviews"u8);
        
        output.WriteLine($"position: {context.Position}");
        output.WriteLine(TronJsonEncoder.EncodeString(context.WrittenBuffer, 0));

        var title = context.GetString(0, "title"u8).GetStringValue(context);
        var language = context.GetString(0, "language"u8).GetStringValue(context);
        var priceUsd = context.GetDouble(0, "price_usd"u8);
        var pages = context.GetLong(0, "pages"u8);
        var inStock = context.GetBool(0, "in_stock"u8);
        
        output.WriteLine($"title: {title}");
        output.WriteLine($"language: {language}");
        output.WriteLine($"price_usd: {priceUsd}");
        output.WriteLine($"pages: {pages}");
        output.WriteLine($"in_stock: {inStock}");

        if (context.IsNull(0, "reviews"u8))
        {
            output.WriteLine("No reviews to display.");
        }
        
        output.WriteLine($"Title field exists: {context.ContainsKey(0, "title"u8)}");
        output.WriteLine($"Price field exists: {context.ContainsKey(0, "price_usd"u8)}");
        output.WriteLine($"ISBN field exists: {context.ContainsKey(0, "isbn"u8)}");

        var titleKind = context.GetValueKind(0, "title"u8);
        output.WriteLine($"Title is string type: {titleKind == ValueKind.String}");
        output.WriteLine($"Title is integer type: {titleKind == ValueKind.I64}");

        var priceValue = context.Get(0, "price_usd"u8);
        output.WriteLine($"Price is string type: {priceValue.IsString()}");
        output.WriteLine($"Price is double type: {priceValue.IsDouble()}");

        if (priceValue.GetValueKind() == ValueKind.F64)
        {
            output.WriteLine($"price value: {priceValue.GetDouble()}");
            output.WriteLine($"price value type size: {priceValue.GetValueSize()}");
        }

        var entryCount = context.GetCount(0);
        output.WriteLine($"Object entries: {entryCount}");
    }

    /// <remarks>
    ///     Ported from <c>03_strings.c</c>.
    /// </remarks>
    [Fact]
    public void Can_work_with_strings()
    {
        var context = TronContext.Create();
        using var scope = context.BeginScope();

        // Build message
        context
            .InitializeObject()
            .SetString(0, "name"u8, "Maria"u8)
            .SetLong(0, "age"u8, 24)
            .SetString(0, "email"u8, "marie@example.com"u8);
        
        // Remember: strings contain an offset to the live buffer
        var email = context.GetString(0, "email"u8);
        
        // ⚠️ Buffer mutation invalidates email!
        context.SetString(0, "phone"u8, "1234567890"u8);

        if (!email.TryGetUtf8Value(context, out _))
            output.WriteLine("Failed to get email");
        
        // ✅ Refresh the email so it becomes valid again
        email = context.GetString(0, "email"u8);
        
        output.WriteLine($"Phone number: {email.GetStringValue(context)}");

        context.SetString(0, "country"u8, "Germany"u8);
        
        output.WriteLine(TronJsonEncoder.EncodeString(context.WrittenBuffer, 0));
    }

    /// <remarks>
    ///     Ported from <c>04-nesting.c</c>.
    /// </remarks>
    [Fact]
    public void Can_work_with_nesting()
    {
        var context = TronContext.Create();
        using var scope = context.BeginScope();

        // Build message
        context
            .InitializeObject()
            .SetString(0, "event"u8, "http_request"u8)
            .SetString(0, "method"u8, "POST"u8)
            .SetLong(0, "duration_ms"u8, 47);
        
        // Set headers
        context
            .SetObject(0, "headers"u8, out var headers)
            .SetString(headers, "content-type"u8, "application/json"u8)
            .SetString(headers, "x-request-id"u8, "req_9f8e2a"u8)
            .SetString(headers, "user-agent"u8, "curl/8.1.2"u8);
        
        output.WriteLine(TronJsonEncoder.EncodeString(context.WrittenBuffer, 0));
        
        // Get user-agent
        headers = context.GetObject(0, "headers"u8);
        var userAgent = context.GetString(headers, "user-agent"u8);
        
        output.WriteLine($"User agent: {userAgent.GetStringValue(context)}");
    }

    /// <remarks>
    ///     Ported from <c>05-arrays.c</c>.
    /// </remarks>
    [Fact]
    public void Can_work_with_arrays()
    {
        var context = TronContext.Create();
        using var scope = context.BeginScope();

        context
            .InitializeArray()
            .ArrayAppendString(0, "zebra"u8)
            .ArrayAppendString(0, "giraffe"u8)
            .ArrayAppendString(0, "buffalo"u8)
            .ArrayAppendString(0, "lion"u8)
            .ArrayAppendString(0, "rhino"u8)
            .ArrayAppendString(0, "elephant"u8);
        
        output.WriteLine($"position: {context.Position}");
        output.WriteLine(TronJsonEncoder.EncodeString(context.WrittenBuffer, 0));

        var elementAtTwo = context.ArrayGetString(0, 2).GetStringValue(context);
        output.WriteLine($"Element at index 2: {elementAtTwo}");
        
        var elementCount = context.GetCount(0);
        output.WriteLine($"Element count: {elementCount}");
        
        var lastElement = context.ArrayGetString(0, elementCount - 1).GetStringValue(context);
        output.WriteLine($"Last element: {lastElement}");
        
        output.WriteLine("Overwriting index 2 with \"gnu\"");
        context.ArraySetString(0, 2, "gnu"u8);
        
        output.WriteLine($"position: {context.Position}");
        output.WriteLine(TronJsonEncoder.EncodeString(context.WrittenBuffer, 0));
        
        output.WriteLine("Overwriting index 3 with \"springbok\"");
        context.ArraySetString(0, 3, "springbok"u8);
        output.WriteLine(TronJsonEncoder.EncodeString(context.WrittenBuffer, 0));
    }

    /// <remarks>
    ///     Ported from <c>06-iterators.c</c>.
    /// </remarks>
    [Fact]
    public void Can_use_iterators()
    {
        var names = new List<byte[]>
        {
            "Boris"u8.ToArray(),
            "John"u8.ToArray(),
            "Olivia"u8.ToArray(),
            "Tanya"u8.ToArray(),
            "Paul"u8.ToArray(),
            "Sarah"u8.ToArray(),
        };
        
        var context = TronContext.Create();
        using var scope = context.BeginScope();
        
        // Build array
        context.InitializeArray();

        for (var i = 0; i < names.Count; i++)
        {
            context
                .ArrayAppendObject(0, out var objectOffset)
                .SetLong(objectOffset, "id"u8, i)
                .SetBool(objectOffset, "vip_member"u8, false)
                .SetNull(objectOffset, "benefits"u8)
                .SetString(objectOffset, "name"u8, names[i]);
        }
        
        output.WriteLine(TronJsonEncoder.EncodeString(context.WrittenBuffer, 0));

        var valueOffset = 0;
        foreach (var entry in Tron.Enumerate(context.WrittenBuffer, 0))
        {
            valueOffset = entry.Offset;
            var benefits = !context.IsNull(entry.Offset, "benefits"u8);
            var id = context.GetLong(entry.Offset, "id"u8);
            var vipMember = context.GetBool(entry.Offset, "vip_member"u8);
            var name = context.GetString(entry.Offset, "name"u8);

            output.WriteLine($"id: {id}, name: {name.GetStringValue(context)}, vip_member: {vipMember}, benefits {benefits}");
        }
        
        // Iterate over object key-value pairs
        
        output.WriteLine("Object keys:");
        
        foreach (var entry in Tron.Enumerate(context.WrittenBuffer, valueOffset))
        {
            var key = entry.Key.GetStringValue(context);
            var valueEntry = entry.GetValue();
            
            switch (valueEntry.GetValueKind())
            {
                case ValueKind.I64:
                    output.WriteLine($"key: {key}, value: {valueEntry.GetLong()}");
                    break;
                case ValueKind.Bool:
                    output.WriteLine($"key: {key}, value: {valueEntry.GetBool()}");
                    break;
                case ValueKind.Null:
                    output.WriteLine($"key: {key}, value: null");
                    break;
                case ValueKind.String:
                    output.WriteLine($"key: {key}, value: {valueEntry.GetStringValue()}");
                    break;
            }
        }
    }

    /// <remarks>
    ///     Ported from <c>07-json-conversion.c</c>.
    /// </remarks>
    [Fact]
    public async Task Can_convert_to_and_from_JSON()
    {
        // Convert JSON file to Lite³
        await using var fileStream = File.OpenRead("periodic_table.json");
        
        var decodeResult = await TronJsonDecoder.DecodeAsync(PipeReader.Create(fileStream));
        var context = TronContext.CreateFromOwned(decodeResult.Buffer, decodeResult.Position, decodeResult.ArrayPool);
        
        using var scope = context.BeginScope();
        
        // Iterator to find densest element
        var dataOffset = context.GetArray(0, "data"u8);

        var densestOffset = 0;
        var densestKgPerM3 = 0.0;
        foreach (var entry in Tron.Enumerate(context.WrittenBuffer, dataOffset))
        {
            if (context.IsNull(entry.Offset, "density_kg_per_m3"u8))
                continue;

            var kgPerM3 = context.GetDouble(entry.Offset, "density_kg_per_m3"u8);
            if (kgPerM3 > densestKgPerM3)
            {
                densestOffset = entry.Offset;
                densestKgPerM3 = kgPerM3;
            }
        }
        
        densestOffset.ShouldNotBe(0);
        
        var name = context.GetString(densestOffset, "name"u8).GetStringValue(context);
        output.WriteLine($"densest element: {name}");
        
        output.WriteLine("Convert to JSON by returned offset (prettified)");
        var json = TronJsonEncoder.EncodeString(context.WrittenBuffer, densestOffset, new JsonWriterOptions
        {
            Indented = true
        });
        output.WriteLine(json);
        
        output.WriteLine("Convert to JSON by writing to buffer (non-prettified):");
        var jsonBuffer = new ArrayBufferWriter<byte>(1024);
        TronJsonEncoder.Encode(context.WrittenBuffer, densestOffset, jsonBuffer);
        
        output.WriteLine(Encoding.UTF8.GetString(jsonBuffer.WrittenSpan));
        output.WriteLine($"json bytes written: {jsonBuffer.WrittenCount}");
    }
}