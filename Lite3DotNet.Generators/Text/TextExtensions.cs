namespace Lite3DotNet.Generators.Text;

public static class TextExtensions
{
    public static LineEnumerable EnumerateLines(this ReadOnlySpan<char> text) => new(text);

    public readonly ref struct LineEnumerable(ReadOnlySpan<char> text)
    {
        private readonly ReadOnlySpan<char> _text = text;
        
        public LineEnumerator GetEnumerator() => new(_text);
    }
    
    public ref struct LineEnumerator(ReadOnlySpan<char> text)
    {
        private ReadOnlySpan<char> _remaining = text;

        public ReadOnlySpan<char> Current { get; private set; }

        public bool MoveNext()
        {
            if (_remaining.IsEmpty)
                return false;

            var index = _remaining.IndexOf('\n');

            if (index < 0)
            {
                Current = _remaining;
                _remaining = ReadOnlySpan<char>.Empty;
                return true;
            }

            var line = _remaining[..index];
            if (!line.IsEmpty && line[^1] == '\r')
                line = line[..^1];

            Current = line;
            _remaining = _remaining[(index + 1)..];
            return true;
        }
    }
}