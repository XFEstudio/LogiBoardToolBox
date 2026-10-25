namespace LogiBoardToolBox.Core.Editing;

/// <summary>Keeps in-progress text out of the settings model and its persistence queue.</summary>
public sealed class CommittedText(string value = "")
{
    public string Value { get; private set; } = value;
    public string Draft { get; private set; } = value;
    public string Error { get; private set; } = "";
    public bool IsEditing { get; private set; }
    public bool IsDirty => Draft != Value;
    public event Action<string>? ValueCommitted;

    public void BeginEdit() => IsEditing = true;
    public void EndEdit() => IsEditing = false;
    public void Edit(string text) { IsEditing = true; Draft = text; Error = ""; }
    public void AcceptValue(string text)
    {
        var preserveDraft = IsEditing && IsDirty;
        Value = text;
        if (!preserveDraft) { Draft = text; Error = ""; }
    }
    public bool TryCommit(Func<string, string?> validate)
    {
        if (!IsDirty) { Error = ""; return true; }
        Error = validate(Draft) ?? "";
        if (Error.Length != 0) return false;
        Value = Draft;
        ValueCommitted?.Invoke(Value);
        return true;
    }
    public void Cancel() { Draft = Value; Error = ""; }
}
