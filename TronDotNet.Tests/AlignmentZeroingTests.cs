namespace TronDotNet.Tests;

/// <remarks>
/// Ported from <c>alignment_zeroing.c</c>.
/// </remarks>
public class AlignmentZeroingTests
{
    [Fact]
    public void Can_zero_unaligned_bytes()
    {
        var buffer = new byte[1024].AsSpan();
        
        // Fill buffer with non-zero garbage
        buffer.Fill(0xEE);

        var position = Tron.InitializeObject(buffer);
        
        // Object insert adds 99 bytes: LITE3_NODE_SIZE (96) + "a" (size 2 including \0) + key_tag (size 1)
        // 1 padding byte is inserted to reach 100 bytes, for 4 byte alignment.
        Tron.SetObject(buffer, ref position, 0, "a"u8, out _);
        
        // Validate padding byte was zeroed
        buffer[Lite3.NodeSize].ShouldBe((byte)0);
        
        // Reset buffer to garbage for second test
        buffer.Fill(0xEE);
        
        position = Tron.InitializeObject(buffer);
        
        // Object insert adds 112 bytes (LITE3_NODE_SIZE (96) + keyval (16))
        // key_tag(1) + "key1\0"(5) + val_tag(1) + str_len(4) + "val1\0"(5) = 16 bytes.
        Tron.SetString(buffer, ref position, 0, "key1"u8, "val1"u8);
        
        var testPosition = position;
        
        // Overwrite "key1":"val1" with an Object
        Tron.SetObject(buffer, ref position, 0, "key1"u8, out _);
        
        buffer[testPosition].ShouldBe((byte)0);
        buffer[testPosition + 1].ShouldBe((byte)0);
    }
}