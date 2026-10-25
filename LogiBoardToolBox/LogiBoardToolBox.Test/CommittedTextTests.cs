using LogiBoardToolBox.Core.Editing;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class CommittedTextTests
{
    [Test]
    public async Task SlowDpiTypingNeverValidatesOrWritesEvenForValidPrefixes()
    {
        var baseline = Fixture.Create(10); using var mouse = new FakeMouse(baseline);
        using var client = new HidPlusPlusClient(mouse, 1);
        var memory = new OnboardMemory(client, baseline.DeviceIdentity, baseline.DeviceName);
        var edit = new CommittedText("800"); var validationCalls = 0; var commits = 0;
        using var scratch = new ScratchDirectory();
        await using var queue = new ImmediateSaveQueue(async () =>
        {
            var profile = baseline.GetProfiles()[0].Copy(); var dpis = profile.Dpis;
            dpis[0] = ushort.Parse(edit.Value); profile.Dpis = dpis; profile.SetDpisY(baseline.Capabilities, dpis);
            await memory.CommitAsync(MemoryWritePlan.Create(baseline, profile, [], true), scratch.Path);
        }, TimeSpan.FromMilliseconds(1));
        edit.ValueCommitted += _ => { commits++; queue.Request(); };
        foreach (var text in new[] { "", "1", "16", "160", "1600" })
        {
            edit.Edit(text); await Task.Delay(210); // Longer than the former autosave debounce.
            Assert.Equal("800", edit.Value); Assert.Equal("", edit.Error);
            Assert.Equal(0, commits); Assert.Equal(0, validationCalls); Assert.Equal(0, mouse.WriteStarts);
        }
        Assert.Equal(true, edit.TryCommit(text => { validationCalls++; return Dpi(text); }));
        await queue.FlushAsync();
        Assert.Equal(1, validationCalls); Assert.Equal(1, commits); Assert.Equal(1, mouse.WriteStarts);
        var readback = await memory.ReadAsync(); Assert.Equal((ushort)1600, readback.GetProfiles()[0].Dpis[0]);
    }

    [Test]
    public void InvalidCommitKeepsLastValidSettingAndDoesNotQueueSave()
    {
        var edit = new CommittedText("800"); var commits = 0; edit.ValueCommitted += _ => commits++;
        foreach (var text in new[] { "", "1", "abc", "99999999999999", "-100" })
        {
            edit.Edit(text); Assert.Equal("", edit.Error);
            Assert.Equal(false, edit.TryCommit(Dpi)); Assert.Equal("800", edit.Value);
            Assert.Equal(0, commits); Assert.Equal(true, edit.Error.Length != 0);
        }
        edit.Edit("1600"); Assert.Equal("", edit.Error); Assert.Equal(true, edit.TryCommit(Dpi)); Assert.Equal(1, commits);
    }

    [Test]
    public void EnterThenLostFocusCommitsOnceAndNoChangeDoesNotValidate()
    {
        var edit = new CommittedText("800"); var commits = 0; var validations = 0; edit.ValueCommitted += _ => commits++;
        string? Validate(string text) { validations++; return Dpi(text); }
        edit.Edit("1600"); Assert.Equal(true, edit.TryCommit(Validate));
        Assert.Equal(true, edit.TryCommit(Validate)); edit.EndEdit();
        Assert.Equal(1, commits); Assert.Equal(1, validations);
    }

    [Test]
    public void EscapeCancelsDraftWithoutACommitOrValidation()
    {
        var edit = new CommittedText("800"); var commits = 0; edit.ValueCommitted += _ => commits++;
        edit.Edit("1600"); edit.Cancel();
        Assert.Equal("800", edit.Draft); Assert.Equal("800", edit.Value); Assert.Equal(0, commits);
        Assert.Equal(true, edit.TryCommit(_ => throw new Exception("Unchanged text must not be validated.")));
    }

    [Test]
    public void ReadbackOfAnotherSettingDoesNotReplaceAnActiveDraft()
    {
        var edit = new CommittedText("800"); edit.Edit("16"); edit.AcceptValue("800");
        Assert.Equal("16", edit.Draft); Assert.Equal("800", edit.Value); Assert.Equal("", edit.Error);
        edit.AcceptValue("1200"); Assert.Equal("16", edit.Draft); edit.Cancel(); Assert.Equal("1200", edit.Draft);
    }

    [Test]
    public async Task SavingAnotherFieldUsesCommittedDpiAndPreservesMacros()
    {
        var before = Fixture.Create(10); var edit = new CommittedText("800"); edit.Edit("160");
        using var mouse = new FakeMouse(before); using var client = new HidPlusPlusClient(mouse, 1);
        var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
        var profile = before.GetProfiles()[0].Copy(); profile.Name = "A completed name edit";
        var dpis = profile.Dpis; dpis[0] = ushort.Parse(edit.Value); profile.Dpis = dpis;
        using var scratch = new ScratchDirectory(); await memory.CommitAsync(MemoryWritePlan.Create(before, profile, [], true), scratch.Path);
        var actual = await memory.ReadAsync(); Assert.Equal((ushort)800, actual.GetProfiles()[0].Dpis[0]);
        Assert.Equal("160", edit.Draft);
        for (var i = 32; i < 148; i++) Assert.Equal(before.Sectors[1][i], actual.Sectors[1][i]);
    }

    [Test]
    public void LinkedAxesChangeTogetherOnlyAfterSuccessfulCommit()
    {
        var x = new CommittedText("800"); var y = new CommittedText("800");
        x.ValueCommitted += value => y.AcceptValue(value);
        x.Edit("1"); Assert.Equal("800", y.Value); Assert.Equal(false, x.TryCommit(Dpi)); Assert.Equal("800", y.Value);
        x.Edit("1600"); Assert.Equal(true, x.TryCommit(Dpi)); Assert.Equal("1600", y.Value);
    }

    [Test]
    public void CompletedExternalChangeReplacesAnUnfocusedRejectedDraft()
    {
        var edit = new CommittedText("800"); edit.Edit("1"); edit.TryCommit(Dpi); edit.EndEdit(); edit.AcceptValue("1200");
        Assert.Equal("1200", edit.Value); Assert.Equal("1200", edit.Draft); Assert.Equal("", edit.Error);
    }
    private static string? Dpi(string text) => int.TryParse(text, out var value) && value is >= 100 and <= 44000 ? null : "DPI out of range";
}
