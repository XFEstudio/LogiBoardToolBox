using LogiBoardToolBox.Core.Windows;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class RecorderTests
{
    [Test]
    public void LeftAndRightEditorClicksAreCompleteRecordedGestures()
    {
        var filter = new MouseRecordingFilter();
        foreach (var button in new[] { 1, 2 })
        {
            Assert.Equal(true, filter.ShouldRecordButton(button, down: true, injected: false, isStopRecordingTarget: false));
            Assert.Equal(true, filter.ShouldRecordButton(button, down: false, injected: false, isStopRecordingTarget: false));
        }
    }

    [Test]
    public void StopPressDoesNotLeakItsReleaseAfterPointerLeavesTheButton()
    {
        var filter = new MouseRecordingFilter();
        foreach (var button in new[] { 1, 2 })
        {
            Assert.Equal(false, filter.ShouldRecordButton(button, true, false, true));
            Assert.Equal(false, filter.ShouldRecordButton(button, false, false, false));
            Assert.Equal(true, filter.ShouldRecordButton(button, true, false, false));
            Assert.Equal(true, filter.ShouldRecordButton(button, false, false, false));
        }
    }

    [Test]
    public void PressOutsideStopRetainsReleaseOverStop()
    {
        var filter = new MouseRecordingFilter();
        Assert.Equal(true, filter.ShouldRecordButton(1, true, false, false));
        Assert.Equal(true, filter.ShouldRecordButton(1, false, false, true));
        Assert.Equal(0, filter.ReleaseRecordedButtons().Count);
    }

    [Test]
    public void RepeatedDownAndOrphanReleaseDoNotCreateUnbalancedSteps()
    {
        var filter = new MouseRecordingFilter();
        Assert.Equal(false, filter.ShouldRecordButton(1, false, false, false));
        Assert.Equal(true, filter.ShouldRecordButton(1, true, false, false));
        Assert.Equal(false, filter.ShouldRecordButton(1, true, false, false));
        Assert.Equal(true, filter.ShouldRecordButton(1, false, false, false));
        Assert.Equal(false, filter.ShouldRecordButton(1, false, false, false));
    }

    [Test]
    public void InjectedMouseEventsNeverAlterAPhysicalRecordedGesture()
    {
        var filter = new MouseRecordingFilter();
        Assert.Equal(false, filter.ShouldRecordButton(1, true, true, false));
        Assert.Equal(false, filter.ShouldRecordButton(1, false, false, false));
        Assert.Equal(true, filter.ShouldRecordButton(1, true, false, false));
        Assert.Equal(false, filter.ShouldRecordButton(1, false, true, false));
        Assert.Equal(true, filter.ShouldRecordButton(1, false, false, false));
    }

    [Test]
    public void StopFinalizesOnlyRecordedHoldsAndClearsSuppressedGestures()
    {
        var filter = new MouseRecordingFilter();
        Assert.Equal(true, filter.ShouldRecordButton(2, true, false, false));
        Assert.Equal(false, filter.ShouldRecordButton(1, true, false, true));
        var releases = filter.ReleaseRecordedButtons();
        Assert.Equal(1, releases.Count); Assert.Equal(2, releases[0]);
        Assert.Equal(0, filter.ReleaseRecordedButtons().Count);
        Assert.Equal(false, filter.ShouldRecordButton(2, false, false, false));
        Assert.Equal(true, filter.ShouldRecordButton(1, true, false, false));
    }

    [Test]
    public void NewRecordingSessionDoesNotInheritHeldOrExcludedButtons()
    {
        var filter = new MouseRecordingFilter();
        filter.ShouldRecordButton(1, true, false, true);
        filter.ShouldRecordButton(2, true, false, false);
        filter.Reset();
        Assert.Equal(false, filter.ShouldRecordButton(2, false, false, false));
        Assert.Equal(true, filter.ShouldRecordButton(1, true, false, false));
        Assert.Equal(true, filter.ShouldRecordButton(2, true, false, false));
    }
}
