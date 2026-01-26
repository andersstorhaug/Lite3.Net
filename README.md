# Lite³: A JSON-Compatible Zero-Copy Serialization Format

[![NuGet](https://img.shields.io/nuget/v/Lite3.Net.svg)](https://www.nuget.org/packages/Lite3.Net)

This is a C# port of [Lite³](https://github.com/fastserial/lite3).

## Current Status

Note that this project is in **beta** status, as specifications are being defined for Lite³.

This port currently tracks upstream `ac7fc19`.

## Feature Parity

This port includes two APIs, which correspond closely to the C impementation:

- _Buffer API_: for working directly against fixed `Span<byte>` buffers.
- _Context API_: for working against a resizable buffer, which is internally managed by `ArrayPool<byte>`.
- `Try` and non-`Try` overloads for exceptionless use if desired.

These APIs are supported by a Source Generator against the core implementation, which closely matches the reference C implementation.

Additionally, feature-parity is provided in general, with additional features specific to .NET.

- JSON decoding and encoding
  - Uses `System.Text.Json`'s `Utf8JsonReader` and `Utf8JsonWriter` internally.
  - Asynchronous decode/encode using `System.IO.Pipelines`'s `PipeReader` and `PipeWriter`, respectively.
  - Synchronous decode/encode using `Span<byte>` and `System.Buffers.IBufferWriter<byte>`, respectively.
- Enumeration by `foreach` against a `struct` enumerator.

## Code Example

The following example using the Buffer API outputs `Max Retries: 3`.

```csharp
var buffer = new byte[1024];

Lite3.InitializeObject(buffer, out var position);
Lite3.SetString(buffer, ref position, 0, "app_name"u8, "demo_app"u8);
Lite3.SetLong(buffer, ref position, 0, "max_retries"u8, 3);
Lite3.SetBool(buffer, ref position, 0, "debug_mode"u8, false);

var maxRetries = Lite3.GetLong(buffer, 0, "max_retries"u8);

Console.WriteLine($"Max Retries: {maxRetries}");
```

The equivalent Context API code is below.

```csharp
using var context = Lite3Context.Create();

context
    .InitializeObject()
    .SetString(0, "app_name"u8, "demo_app"u8)
    .SetLong(0, "max_retries"u8, 3)
    .SetBool(0, "debug_mode"u8, false);

var maxRetries = context.GetLong(0, "max_retries"u8);

Console.WriteLine($"Max Retries: {maxRetries}");
```

See [`ContextApiExamples.cs`](Lite3DotNet.Tests/ContextApiExamples.cs) and [`BufferApiExamples.cs`](Lite3DotNet.Tests/BufferApiExamples.cs) for more examples.

## Attribution

This project is a C# port of the original Lite³ C implementation by Elias de Jong.
All credit for the original design belongs to the original authors.
