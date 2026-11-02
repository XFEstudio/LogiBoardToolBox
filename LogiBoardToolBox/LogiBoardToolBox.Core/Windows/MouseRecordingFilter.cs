namespace LogiBoardToolBox.Core.Windows;

/// <summary>
/// Tracks complete physical button gestures independently of the window under the pointer.
/// Excluding a stop-button press also excludes its release, even after the pointer moves.
/// </summary>
public sealed class MouseRecordingFilter
{
    private readonly HashSet<int> recorded = [];
    private readonly HashSet<int> excluded = [];

    public bool ShouldRecordButton(int button, bool down, bool injected, bool isStopRecordingTarget)
    {
        if (injected || button is < 1 or > 5) return false;
        if (down)
        {
            if (recorded.Contains(button) || excluded.Contains(button)) return false;
            if (isStopRecordingTarget) { excluded.Add(button); return false; }
            recorded.Add(button);
            return true;
        }
        if (excluded.Remove(button)) return false;
        // A real press retains its release even if released over the stop button.
        return recorded.Remove(button);
    }

    public IReadOnlyList<int> ReleaseRecordedButtons()
    {
        var buttons = recorded.Order().ToArray();
        Reset();
        return buttons;
    }

    public void Reset() { recorded.Clear(); excluded.Clear(); }
}
