using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using TronDotNet.SystemTextJson;
using Xunit.Abstractions;
using static TronDotNet.Lite3;

namespace TronDotNet.Tests;

/// <remarks>
///     Ported from C <c>buffer_api</c> examples.
/// </remarks>
public class BufferApiExamples(ITestOutputHelper output)
{
    /// <remarks>
    ///     Ported from <c>01-building-messages.c</c>.
    /// </remarks>
    [Fact]
    public void Can_build_messages()
    {
        var buffer = new byte[1024];
        
        // Build message
        var position = Tron.InitializeObject(buffer);
        Tron.SetString(buffer, ref position, 0, "event"u8, "lap_complete"u8);
        Tron.SetLong(buffer, ref position, 0, "lap"u8, 55);
        Tron.SetDouble(buffer, ref position, 0, "time_sec"u8, 88.427);

        output.WriteLine($"position: {position}");
        output.WriteLine(TronJsonEncoder.EncodeString(buffer, 0));
        
        output.WriteLine("updating lap count");
        Tron.SetLong(buffer, ref position, 0, "lap"u8, 56);
        
        output.WriteLine("Data to send");
        output.WriteLine($"buflen: {position}");
        output.WriteLine(TronJsonEncoder.EncodeString(buffer, 0));
        
        // Transmit data / copy to new context
        var receiveBuffer = new byte[1024];
        var receivePosition = position;
        buffer.CopyTo(receiveBuffer, 0);
        
        // Mutate (zero-copy, no parsing)
        output.WriteLine("Verifying fastest lap");
        Tron.SetString(buffer, ref receivePosition, 0, "verified"u8, "race_control"u8);
        Tron.SetBool(buffer, ref receivePosition, 0, "fastest_lap"u8, true);
        
        output.WriteLine("Modified data:");
        output.WriteLine($"rx position: {receivePosition}");
        output.WriteLine(TronJsonEncoder.EncodeString(receiveBuffer, 0));
        
        // Ready to send
    }

    /// <remarks>
    ///     Ported from <c>02_reading-messages.c</c>.
    /// </remarks>
    [Fact]
    public void Can_read_messages()
    {
        var buffer = new byte[1024];
        
        // Build Message
        var position = Tron.InitializeObject(buffer);
        Tron.SetString(buffer, ref position, 0, "title"u8, "C Programming Language, 2nd Edition"u8);
        Tron.SetString(buffer, ref position, 0, "language"u8, "en"u8);
        Tron.SetDouble(buffer, ref position, 0, "price_usd"u8, 60.30);
        Tron.SetLong(buffer, ref position, 0, "pages"u8, 272);
        Tron.SetBool(buffer, ref position, 0, "in_stock"u8, true);
        Tron.SetNull(buffer, ref position, 0, "reviews"u8);
        
        output.WriteLine($"position: {position}");
        output.WriteLine(TronJsonEncoder.EncodeString(buffer, 0));

        var title = Tron.GetString(buffer, 0, "title"u8).GetStringValue(buffer);
        var language = Tron.GetString(buffer, 0, "language"u8).GetStringValue(buffer);
        var priceUsd = Tron.GetDouble(buffer, 0, "price_usd"u8);
        var pages = Tron.GetLong(buffer, 0, "pages"u8);
        var inStock = Tron.GetBool(buffer, 0, "in_stock"u8);
        
        output.WriteLine($"title: {title}");
        output.WriteLine($"language: {language}");
        output.WriteLine($"price_usd: {priceUsd}");
        output.WriteLine($"pages: {pages}");
        output.WriteLine($"in_stock: {inStock}");

        if (Tron.IsNull(buffer, 0, "reviews"u8))
        {
            output.WriteLine("No reviews to display.");
        }
        
        output.WriteLine($"Title field exists: {Tron.ContainsKey(buffer, 0, "title"u8)}");
        output.WriteLine($"Price field exists: {Tron.ContainsKey(buffer, 0, "price_usd"u8)}");
        output.WriteLine($"ISBN field exists: {Tron.ContainsKey(buffer, 0, "isbn"u8)}");

        var titleKind = Tron.GetValueKind(buffer, 0, "title"u8);
        output.WriteLine($"Title is string type: {titleKind == ValueKind.String}");
        output.WriteLine($"Title is integer type: {titleKind == ValueKind.I64}");

        var priceValue = Tron.Get(buffer, 0, "price_usd"u8);
        output.WriteLine($"Price is string type: {priceValue.IsString()}");
        output.WriteLine($"Price is double type: {priceValue.IsDouble()}");

        if (priceValue.GetValueKind() == ValueKind.F64)
        {
            output.WriteLine($"price value: {priceValue.GetDouble()}");
            output.WriteLine($"price value type size: {priceValue.GetValueSize()}");
        }

        var entryCount = Tron.GetCount(buffer, 0);
        output.WriteLine($"Object entries: {entryCount}");
    }

    /// <remarks>
    ///     Ported from <c>03_strings.c</c>.
    /// </remarks>
    [Fact]
    public void Can_work_with_strings()
    {
        var buffer = new byte[1024];
        
        // Build message
        var position = Tron.InitializeObject(buffer);
        Tron.SetString(buffer, ref position, 0, "name"u8, "Maria"u8);
        Tron.SetLong(buffer, ref position, 0, "age"u8, 24);
        Tron.SetString(buffer, ref position, 0, "email"u8, "marie@example.com"u8);
        
        // Remember: strings contain an offset to the live buffer
        var email = Tron.GetString(buffer , 0, "email"u8);
        
        // ⚠️ Buffer mutation invalidates email!
        Tron.SetString(buffer, ref position, 0, "phone"u8, "1234567890"u8);

        if (!email.TryGetUtf8Value(buffer, out _))
            output.WriteLine("Failed to get email");
        
        // ✅ Refresh the email so it becomes valid again
        email = Tron.GetString(buffer, 0, "email"u8);
        
        output.WriteLine($"Phone number: {email.GetStringValue(buffer)}");

        Tron.SetString(buffer, ref position, 0, "country"u8, "Germany"u8);
        
        output.WriteLine(TronJsonEncoder.EncodeString(buffer, 0));
    }
    
    /// <remarks>
    ///     Ported from <c>04-nesting.c</c>.
    /// </remarks>
    [Fact]
    public void Can_work_with_nesting()
    {
        var buffer = new byte[1024];

        // Build message
        var position = Tron.InitializeObject(buffer);
        Tron.SetString(buffer, ref position, 0, "event"u8, "http_request"u8);
        Tron.SetString(buffer, ref position, 0, "method"u8, "POST"u8);
        Tron.SetLong(buffer, ref position, 0, "duration_ms"u8, 47);
        
        // Set headers
        Tron.SetObject(buffer, ref position, 0, "headers"u8, out var headers);
        Tron.SetString(buffer, ref position, headers, "content-type"u8, "application/json"u8);
        Tron.SetString(buffer, ref position, headers, "x-request-id"u8, "req_9f8e2a"u8);
        Tron.SetString(buffer, ref position, headers, "user-agent"u8, "curl/8.1.2"u8);
        
        output.WriteLine(TronJsonEncoder.EncodeString(buffer, 0));
        
        // Get user-agent
        headers = Tron.GetObject(buffer, 0, "headers"u8);
        var userAgent = Tron.GetString(buffer, headers, "user-agent"u8);
        
        output.WriteLine($"User agent: {userAgent.GetStringValue(buffer)}");
    }
    
    /// <remarks>
    ///     Ported from <c>05-arrays.c</c>.
    /// </remarks>
    [Fact]
    public void Can_work_with_arrays()
    {
        var buffer = new byte[1024];
        
        var position = Tron.InitializeArray(buffer);
        Tron.ArrayAppendString(buffer, ref position, 0, "zebra"u8);
        Tron.ArrayAppendString(buffer, ref position, 0, "giraffe"u8);
        Tron.ArrayAppendString(buffer, ref position, 0, "buffalo"u8);
        Tron.ArrayAppendString(buffer, ref position, 0, "lion"u8);
        Tron.ArrayAppendString(buffer, ref position, 0, "rhino"u8);
        Tron.ArrayAppendString(buffer, ref position, 0, "elephant"u8);
        
        output.WriteLine($"position: {position}");
        output.WriteLine(TronJsonEncoder.EncodeString(buffer, 0));

        var elementAtTwo = Tron.ArrayGetString(buffer, 0, 2).GetStringValue(buffer);
        output.WriteLine($"Element at index 2: {elementAtTwo}");
        
        var elementCount = Tron.GetCount(buffer, 0);
        output.WriteLine($"Element count: {elementCount}");
        
        var lastElement = Tron.ArrayGetString(buffer, 0, elementCount - 1).GetStringValue(buffer);
        output.WriteLine($"Last element: {lastElement}");
        
        output.WriteLine("Overwriting index 2 with \"gnu\"");
        Tron.ArraySetString(buffer, ref position, 0, 2, "gnu"u8);
        
        output.WriteLine($"position: {position}");
        output.WriteLine(TronJsonEncoder.EncodeString(buffer, 0));
        
        output.WriteLine("Overwriting index 3 with \"springbok\"");
        Tron.ArraySetString(buffer, ref position, 0, 3, "springbok"u8);
        output.WriteLine(TronJsonEncoder.EncodeString(buffer, 0));
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
        
        var buffer = new byte[1024];
        
        // Build array
        var position = Tron.InitializeArray(buffer);

        for (var i = 0; i < names.Count; i++)
        {
            Tron.ArrayAppendObject(buffer, ref position, 0, out var objectOffset);
            Tron.SetLong(buffer, ref position, objectOffset, "id"u8, i);
            Tron.SetBool(buffer, ref position, objectOffset, "vip_member"u8, false);
            Tron.SetNull(buffer, ref position, objectOffset, "benefits"u8);
            Tron.SetString(buffer, ref position, objectOffset, "name"u8, names[i]);
        }
        
        output.WriteLine(TronJsonEncoder.EncodeString(buffer, 0));

        var valueOffset = 0;
        foreach (var entry in Tron.Enumerate(buffer, 0))
        {
            valueOffset = entry.Offset;
            var benefits = !Tron.IsNull(buffer, entry.Offset, "benefits"u8);
            var id = Tron.GetLong(buffer, entry.Offset, "id"u8);
            var vipMember = Tron.GetBool(buffer, entry.Offset, "vip_member"u8);
            var name = Tron.GetString(buffer, entry.Offset, "name"u8);

            output.WriteLine($"id: {id}, name: {name.GetStringValue(buffer)}, vip_member: {vipMember}, benefits {benefits}");
        }
        
        // Iterate over object key-value pairs
        
        output.WriteLine("Object keys:");
        
        foreach (var entry in Tron.Enumerate(buffer, valueOffset))
        {
            var key = entry.Key.GetStringValue(buffer);
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
        var buffer = new byte[256 * 1024];
        
        // Convert JSON file to Lite³
        await using var fileStream = File.OpenRead("periodic_table.json");
        
        await TronJsonDecoder.DecodeAsync(PipeReader.Create(fileStream), buffer);
        
        // Iterator to find densest element
        var dataOffset = Tron.GetArray(buffer , 0, "data"u8);

        var densestOffset = 0;
        var densestKgPerM3 = 0.0;
        foreach (var entry in Tron.Enumerate(buffer, dataOffset))
        {
            if (Tron.IsNull(buffer, entry.Offset, "density_kg_per_m3"u8))
                continue;

            var kgPerM3 = Tron.GetDouble(buffer, entry.Offset, "density_kg_per_m3"u8);
            if (kgPerM3 > densestKgPerM3)
            {
                densestOffset = entry.Offset;
                densestKgPerM3 = kgPerM3;
            }
        }
        
        densestOffset.ShouldNotBe(0);
        
        var name = Tron.GetString(buffer, densestOffset, "name"u8).GetStringValue(buffer);
        output.WriteLine($"densest element: {name}");
        
        output.WriteLine("Convert to JSON by returned offset (prettified)");
        var json = TronJsonEncoder.EncodeString(buffer, densestOffset, new JsonWriterOptions
        {
            Indented = true
        });
        output.WriteLine(json);
        
        output.WriteLine("Convert to JSON by writing to buffer (non-prettified):");
        var jsonBuffer = new ArrayBufferWriter<byte>(1024);
        TronJsonEncoder.Encode(buffer, densestOffset, jsonBuffer);
        
        output.WriteLine(Encoding.UTF8.GetString(jsonBuffer.WrittenSpan));
        output.WriteLine($"json bytes written: {jsonBuffer.WrittenCount}");
    }
}