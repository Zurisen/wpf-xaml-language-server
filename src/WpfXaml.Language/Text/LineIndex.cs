namespace WpfXaml.Language.Text;

/// <summary>Zero-based line and UTF-16 character position, as used by LSP.</summary>
public readonly record struct LinePosition(int Line, int Character);

/// <summary>Maps between absolute offsets and line/character positions.</summary>
public sealed class LineIndex
{
    private readonly int[] _lineStarts;
    private readonly int _length;

    public LineIndex(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
            else if (c == '\n')
            {
                starts.Add(i + 1);
            }
        }

        _lineStarts = starts.ToArray();
        _length = text.Length;
    }

    public int LineCount => _lineStarts.Length;

    public int GetLineStart(int line) => _lineStarts[Math.Clamp(line, 0, _lineStarts.Length - 1)];

    public LinePosition GetPosition(int offset)
    {
        offset = Math.Clamp(offset, 0, _length);
        var line = Array.BinarySearch(_lineStarts, offset);
        if (line < 0)
        {
            line = ~line - 1;
        }

        return new LinePosition(line, offset - _lineStarts[line]);
    }

    /// <summary>Converts a position to an offset, clamping out-of-range lines and characters.</summary>
    public int GetOffset(LinePosition position) => GetOffset(position.Line, position.Character);

    public int GetOffset(int line, int character)
    {
        if (line < 0)
        {
            return 0;
        }

        if (line >= _lineStarts.Length)
        {
            return _length;
        }

        var start = _lineStarts[line];
        var nextStart = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _length;
        return Math.Min(start + Math.Max(character, 0), nextStart);
    }
}
