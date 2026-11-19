using LogiBoardToolBox.Core.Editing;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace LogiBoardToolBox.Controls;

public enum TextCommitReason { Enter, LostFocus }
public sealed class TextCommitEventArgs(string text, TextCommitReason reason) : EventArgs
{
    public string Text { get; } = text;
    public TextCommitReason Reason { get; } = reason;
    public string? Error { get; set; }
}

/// <summary>Text is a local draft; only Value participates in the settings binding.</summary>
public sealed partial class CommitTextBox : TextBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(CommitTextBox), new PropertyMetadata("", ValueChanged));
    public static readonly DependencyProperty ErrorTextProperty = DependencyProperty.Register(nameof(ErrorText), typeof(string), typeof(CommitTextBox), new PropertyMetadata(""));
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string ErrorText { get => (string)GetValue(ErrorTextProperty); private set => SetValue(ErrorTextProperty, value); }
    public event EventHandler<TextCommitEventArgs>? Validating;
    public event EventHandler? ValueCommitted;
    public event EventHandler? DraftEdited;
    private readonly CommittedText edit = new();
    private bool syncing, markedInvalid;
    private object? previousBorder, previousTip;

    public CommitTextBox()
    {
        DefaultStyleKey = typeof(TextBox);
        edit.ValueCommitted += value => { Value = value; ValueCommitted?.Invoke(this, EventArgs.Empty); };
        TextChanged += (_, _) =>
        {
            if (syncing || Text == edit.Draft) return;
            edit.Edit(Text); ShowError(""); DraftEdited?.Invoke(this, EventArgs.Empty);
        };
        GotFocus += (_, _) => edit.BeginEdit();
        LostFocus += (_, _) => { Commit(TextCommitReason.LostFocus); edit.EndEdit(); };
        Unloaded += (_, _) => edit.EndEdit();
        KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { Commit(TextCommitReason.Enter); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.Escape) { CancelDraft(); e.Handled = true; }
        };
    }
    private static void ValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var field = (CommitTextBox)sender;
        if (!field.syncing && field.Text != field.edit.Draft) field.edit.Edit(field.Text);
        field.edit.AcceptValue(args.NewValue as string ?? "");
        field.SyncText(); field.ShowError(field.edit.Error);
    }
    public bool Commit(TextCommitReason reason = TextCommitReason.Enter)
    {
        // TextChanged may be delivered on a later UI turn than Enter, blur or a binding update.
        if (Text != edit.Draft) edit.Edit(Text);
        var accepted = edit.TryCommit(text =>
        {
            var args = new TextCommitEventArgs(text, reason);
            Validating?.Invoke(this, args); return args.Error;
        });
        SyncText(); ShowError(edit.Error); return accepted;
    }
    public void CancelDraft() { edit.Cancel(); SyncText(); ShowError(""); DraftEdited?.Invoke(this, EventArgs.Empty); }
    private void SyncText()
    {
        if (Text == edit.Draft) return;
        syncing = true; try { Text = edit.Draft; } finally { syncing = false; }
    }
    private void ShowError(string message)
    {
        ErrorText = message; AutomationProperties.SetHelpText(this, message);
        if (message.Length != 0)
        {
            if (!markedInvalid) { previousBorder = ReadLocalValue(BorderBrushProperty); previousTip = ToolTipService.GetToolTip(this); markedInvalid = true; }
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 128, 128));
            ToolTipService.SetToolTip(this, message);
        }
        else if (markedInvalid)
        {
            if (ReferenceEquals(previousBorder, DependencyProperty.UnsetValue)) ClearValue(BorderBrushProperty); else SetValue(BorderBrushProperty, previousBorder);
            ToolTipService.SetToolTip(this, previousTip); markedInvalid = false;
        }
    }
}
