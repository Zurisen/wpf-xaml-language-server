namespace WpfXaml.Language.Text;

/// <summary>A half-open range <c>[Start, End)</c> of UTF-16 code units in a document.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public bool IsEmpty => Length == 0;

    public static TextSpan FromBounds(int start, int end) => new(start, end - start);

    /// <summary>True when <paramref name="position"/> is inside the span, excluding the end.</summary>
    public bool Contains(int position) => position >= Start && position < End;

    /// <summary>True when <paramref name="position"/> is inside the span or exactly at its end (a caret position).</summary>
    public bool ContainsOrTouches(int position) => position >= Start && position <= End;

    public TextSpan Shift(int offset) => new(Start + offset, Length);

    public override string ToString() => $"[{Start}..{End})";
}
