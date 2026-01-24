namespace Lite3.Generators.Text;

internal class Indent
{
    private int _level;
    private string _current = "";
        
    public Indent(int level = 0)
    {
        if (level > 0)
            SetCurrent(level);
    }

    private Indent SetCurrent(int level)
    {
        _current = new string(' ', (_level = level) * 4);
        return this;
    }

    public override string ToString() => _current;
    public static Indent operator ++(Indent value) => value.SetCurrent(Math.Min(value._level + 1, 8));
    public static Indent operator --(Indent value) => value.SetCurrent(Math.Max(value._level - 1, 0));
}