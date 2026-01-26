namespace Lite3DotNet.Tests;

/// <remarks>
///     Ported from <c>type_queries.c</c>.
/// </remarks>
public class TypeQueryTests
{
    /// <remarks>
    ///     Ported from <c>test_arr_get_type_buffer_api</c>.
    /// </remarks>
    [Fact]
    public void Can_get_array_type_with_buffer_API()
    {
        var buffer = new byte[2048];
        
        // Initialize as array
        Lite3.InitializeArray(buffer, out var position);
        
        // Append various types
        Lite3.ArrayAppendString(buffer, ref position, 0, "hello"u8);
        Lite3.ArrayAppendLong(buffer, ref position, 0, 42);
        Lite3.ArrayAppendDouble(buffer, ref position, 0, 3.14);
        Lite3.ArrayAppendBool(buffer, ref position, 0, true);
        Lite3.ArrayAppendNull(buffer, ref position, 0);
        
        // Test type queries
        Lite3.ArrayGetValueKind(buffer, 0, 0).ShouldBe(Lite3Core.ValueKind.String);
        Lite3.ArrayGetValueKind(buffer, 0, 1).ShouldBe(Lite3Core.ValueKind.I64);
        Lite3.ArrayGetValueKind(buffer, 0, 2).ShouldBe(Lite3Core.ValueKind.F64);
        Lite3.ArrayGetValueKind(buffer, 0, 3).ShouldBe(Lite3Core.ValueKind.Bool);
        Lite3.ArrayGetValueKind(buffer, 0, 4).ShouldBe(Lite3Core.ValueKind.Null);
        
        // Test out-of-bounds returns invalid type
        Lite3.ArrayGetValueKind(buffer, 0, 5).ShouldBe(Lite3Core.ValueKind.Invalid);
        Lite3.ArrayGetValueKind(buffer, 0, 100).ShouldBe(Lite3Core.ValueKind.Invalid);
    }

    /// <remarks>
    ///     Ported from <c>test_arr_get_type_context_api</c>.
    /// </remarks>
    [Fact]
    public void Can_get_array_type_with_context_API()
    {
        using var context = Lite3Context.Create();
        
        // Initialize as array
        context.InitializeArray();
        
        // Append various types
        context
            .ArrayAppendString(0, "hello"u8)
            .ArrayAppendLong(0, 42)
            .ArrayAppendDouble(0, 3.14)
            .ArrayAppendBool(0, true)
            .ArrayAppendNull(0);
        
        // Test type queries
        context.ArrayGetValueKind(0, 0).ShouldBe(Lite3Core.ValueKind.String);
        context.ArrayGetValueKind(0, 1).ShouldBe(Lite3Core.ValueKind.I64);
        context.ArrayGetValueKind(0, 2).ShouldBe(Lite3Core.ValueKind.F64);
        context.ArrayGetValueKind(0, 3).ShouldBe(Lite3Core.ValueKind.Bool);
        context.ArrayGetValueKind(0, 4).ShouldBe(Lite3Core.ValueKind.Null);
        
        // Test out-of-bounds returns invalid type
        context.ArrayGetValueKind(0, 5).ShouldBe(Lite3Core.ValueKind.Invalid);
        context.ArrayGetValueKind(0, 100).ShouldBe(Lite3Core.ValueKind.Invalid);
    }
    
    /// <remarks>
    ///     Ported from <c>test_arr_get_type_nested</c>.
    /// </remarks>
    [Fact]
    public void Can_get_array_types_when_nested()
    {
        using var context = Lite3Context.Create();
        
        // Initialize as object
        context.InitializeObject();
        
        // Add a nested array
        context.SetArray(0, "items"u8, out var arrayOffset);
        
        // Append to nested array
        context
            .ArrayAppendLong(arrayOffset, 1)
            .ArrayAppendObject(arrayOffset, out _)
            .ArrayAppendString(arrayOffset, "test"u8);
        
        // Test type queries on nested array
        context.ArrayGetValueKind(arrayOffset, 0).ShouldBe(Lite3Core.ValueKind.I64);
        context.ArrayGetValueKind(arrayOffset, 1).ShouldBe(Lite3Core.ValueKind.Object);
        context.ArrayGetValueKind(arrayOffset, 2).ShouldBe(Lite3Core.ValueKind.String);
    }

    /// <remarks>
    ///     Ported from <c>test_root_type_query_context_api</c>.
    /// </remarks>
    [Fact]
    public void Can_get_root_type_with_context_API()
    {
        // Test object root
        var context = Lite3Context.Create();

        using (context)
        {
            context.InitializeObject();
        
            context.GetRootType().ShouldBe(Lite3Core.ValueKind.Object);
        }
        
        // Test array root
        context = Lite3Context.Create();

        using (context)
        {
            context.InitializeArray();
            
            context.GetRootType().ShouldBe(Lite3Core.ValueKind.Array);
        }
    }
    
    /// <remarks>
    ///     Ported from <c>test_root_type_query_buffer_api</c>.
    /// </remarks>
    [Fact]
    public void Can_get_root_type_with_buffer_API()
    {
        var buffer = new byte[2048];
        
        // Test object root
        Lite3.InitializeObject(buffer, out _);
        Lite3.GetRootType(buffer).ShouldBe(Lite3Core.ValueKind.Object);
        
        // Test array root
        Lite3.InitializeArray(buffer, out _);
        Lite3.GetRootType(buffer).ShouldBe(Lite3Core.ValueKind.Array);
    }

    /// <remarks>
    ///     Ported from <c>test_root_type_query_buffer_api</c>.
    /// </remarks>
    [Fact]
    public void Can_get_root_type_with_empty_buffer()
    {
        using var context = Lite3Context.Create();
        
        context.GetRootType().ShouldBe(Lite3Core.ValueKind.Invalid);
    }
}