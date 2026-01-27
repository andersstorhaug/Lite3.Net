namespace Lite3DotNet;

public static partial class Lite3Core
{
    public enum Status
    {
        KeyEntryOutOfBounds = -99,
        KeyTagSizeDoesNotMatch,
        ValueOutOfBounds,
        ValueKindInvalid,
        ExpectedArrayOrObject,
        ExpectedObject,
        ExpectedNonEmptyKey,
        ExpectedArray,
        NodeOffsetNotAligned,
        NodeWalksOffsetOutOfBounds,
        NodeWalksExceededTreeHeightMax,
        KeyNotFound,
        InvalidIterator,
        HashProbeLimitReached,
        ArrayIndexOutOfBounds,
        KeyHashCollision,
        StartOffsetOutOfBounds,
        ValueKindDoesNotMatch,
        MutatedBuffer,
        ExpectedJsonProperty,
        JsonNestingDepthExceededMax,
        ExpectedJsonArrayOrObject,
        ExpectedJsonValue,
        InsufficientBuffer,
        NeedsMoreData,
        
        None = 0,
        IteratorDone = 1,
        IteratorItem = 2,
        GrewBuffer = 3
    }
    
    public static Exception AsException(this Status status) => status switch
    {
        Status.KeyEntryOutOfBounds => new InvalidOperationException("Key entry out of bounds."),
        Status.KeyTagSizeDoesNotMatch => new InvalidOperationException("Key tag size does not match."),
        Status.KeyHashCollision => new InvalidOperationException("Key hash collision."),
        Status.ValueOutOfBounds => new InvalidOperationException("Value out of bounds."),
        Status.ValueKindInvalid => new InvalidOperationException("Value kind invalid."),
        Status.ExpectedArrayOrObject => new InvalidOperationException("Expected array or object."),
        Status.ExpectedObject => new InvalidOperationException("Expected object."),
        Status.ExpectedNonEmptyKey => new InvalidOperationException("Expected non-empty key."),
        Status.ExpectedArray => new InvalidOperationException("Expected array."),
        Status.NodeOffsetNotAligned => new InvalidOperationException("Node offset not aligned."),
        Status.NodeWalksOffsetOutOfBounds => new InvalidOperationException("Node walks offset out of bounds."),
        Status.NodeWalksExceededTreeHeightMax => new InvalidOperationException("Node walks exceeded tree height max."),
        Status.InvalidIterator => new InvalidOperationException("Invalid iterator."),
        Status.InsufficientBuffer => new ArgumentException("Buffer is too small."),
        Status.HashProbeLimitReached => new InvalidOperationException("Hash probe limit reached."),
        Status.KeyNotFound  => new KeyNotFoundException(),
        Status.ArrayIndexOutOfBounds => new InvalidOperationException("Array index of bounds."),
        Status.StartOffsetOutOfBounds => new InvalidOperationException("Start offset of bounds."),
        Status.ValueKindDoesNotMatch => new InvalidOperationException("Value kind does not match."),
        Status.MutatedBuffer => new InvalidOperationException("Mutated buffer."),
        Status.ExpectedJsonProperty => new InvalidOperationException("Expected JSON property."),
        Status.ExpectedJsonArrayOrObject => new InvalidOperationException("Expected JSON array or object."),
        Status.ExpectedJsonValue => new InvalidOperationException("Expected JSON value."),
        Status.JsonNestingDepthExceededMax => new InvalidOperationException("JSON nesting depth exceeded max."),
        Status.NeedsMoreData => new InvalidOperationException("Unexpected end of input."),
        
        _ => new InvalidOperationException($"An unknown error occurred ({status}).")
    };
}