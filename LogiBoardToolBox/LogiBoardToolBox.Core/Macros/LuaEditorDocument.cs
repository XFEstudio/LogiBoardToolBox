namespace LogiBoardToolBox.Core.Macros;

/// <summary>Keeps the entire source while limiting each native text control to a small editable page.</summary>
public sealed class LuaEditorDocument
{
    public const int PageCharacters = 32768;
    public string Source { get; private set; }
    public int PageCount => Math.Max(1, (Source.Length + PageCharacters - 1) / PageCharacters);
    public LuaEditorDocument(string source) { LuaSourceFile.ValidateSource(source); Source = source; }
    private int Boundary(int page)
    {
        var at = (int)Math.Min((long)page * PageCharacters, Source.Length);
        if (at > 0 && at < Source.Length && char.IsHighSurrogate(Source[at - 1]) && char.IsLowSurrogate(Source[at])) at++;
        return at;
    }
    public string ReadPage(int page)
    {
        if (page < 0 || page >= PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        return Source[Boundary(page)..Boundary(page + 1)];
    }
    public void ReplacePage(int page, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (page < 0 || page >= PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        var start = Boundary(page); var end = Boundary(page + 1);
        if ((long)Source.Length - (end - start) + text.Length > LuaSourceFile.MaximumSourceCharacters)
            throw new InvalidDataException("编辑后的 Lua 源码超过 4 MiB 字符，原文仍保留。");
        Source = string.Concat(Source.AsSpan(0, start), text.AsSpan(), Source.AsSpan(end));
    }
}
