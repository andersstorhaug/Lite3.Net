namespace TronDotNet;

using static Lite3;

public readonly ref struct TronEnumerable(ReadOnlySpan<byte> buffer, int offset, bool writeKey, bool writeOffset)
{
    private readonly ReadOnlySpan<byte> _buffer = buffer;

    public Enumerator GetEnumerator()
    {
        Status status;
        return (status = CreateIterator(_buffer, offset, out var iterator)) < 0
            ? throw status.AsException()
            : new Enumerator(_buffer, iterator, writeKey, writeOffset);
    }

    public ref struct Enumerator()
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private Lite3Iterator _iterator;
        private readonly bool _withKey;
        private readonly bool _withOffset;

        internal Enumerator(ReadOnlySpan<byte> buffer, Lite3Iterator iterator, bool withKey, bool withOffset) : this()
        {
            _buffer = buffer;
            _iterator = iterator;
            _withKey = withKey;
            _withOffset = withOffset;
        }
        
        public Entry Current { get; private set; }

        public bool MoveNext()
        {
            Status status;
            if ((status = IteratorNext(_buffer, ref _iterator, _withKey, out var keyRef, _withOffset, out var offset)) == Status.IteratorItem)
            {
                Current = new Entry(_buffer, keyRef, offset);
                return true;
            }

            if (status == Status.IteratorDone)
            {
                Current = default;
                return false;
            }

            return status < 0
                ? throw status.AsException()
                : false;
        }
    }
    
    public readonly ref struct Entry(ReadOnlySpan<byte> buffer, StringEntry key, int offset)
    {
        private readonly ReadOnlySpan<byte> _buffer = buffer;
        
        /// <summary>
        ///     The object or array start offset.
        /// </summary>
        public readonly int Offset = offset;
        
        /// <summary>
        ///     The key entry.
        /// </summary>
        public readonly StringEntry Key = key;

        /// <summary>
        ///     Gets the value entry.
        /// </summary>
        public ReadOnlyValueEntry GetValue() => new(_buffer, Offset);
    }
}