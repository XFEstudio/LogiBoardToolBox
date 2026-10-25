using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Storage;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class ProductRenameTests
{
    [Test]
    public void LegacyDataIsCopiedWithoutDeletingTheOldLibraryOrOverwritingNewSettings()
    {
        using var scratch = new ScratchDirectory();
        var old = Path.Combine(scratch.Path, RuntimeDataPaths.LegacyProductName, "Onboard");
        Directory.CreateDirectory(Path.Combine(old, "Backups"));
        File.WriteAllText(Path.Combine(old, "macros.json"), "recorded delays 59/198/25/10");
        File.WriteAllText(Path.Combine(old, "Backups", "mouse.json"), "old backup");
        var current = RuntimeDataPaths.GetProductDirectory(scratch.Path);
        Assert.Equal(RuntimeDataPaths.ProductName, Path.GetFileName(current));
        Assert.Equal("recorded delays 59/198/25/10", File.ReadAllText(Path.Combine(current, "Onboard", "macros.json")));
        Assert.Equal("old backup", File.ReadAllText(Path.Combine(current, "Onboard", "Backups", "mouse.json")));
        Assert.Equal(true, File.Exists(Path.Combine(old, "macros.json")));
        File.WriteAllText(Path.Combine(current, "Onboard", "macros.json"), "new settings");
        Assert.Equal(current, RuntimeDataPaths.GetProductDirectory(scratch.Path));
        Assert.Equal("new settings", File.ReadAllText(Path.Combine(current, "Onboard", "macros.json")));
    }

    [Test]
    public void FrameworkCreatedProductRootDoesNotPreventOnboardDataMigration()
    {
        using var scratch = new ScratchDirectory();
        var old = Path.Combine(scratch.Path, RuntimeDataPaths.LegacyProductName, "Onboard");
        var currentRoot = Path.Combine(scratch.Path, RuntimeDataPaths.ProductName);
        var framework = Path.Combine(currentRoot, "CrossVersion");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(framework);
        File.WriteAllText(Path.Combine(old, "macros.json"), "recorded macro library");
        File.WriteAllText(Path.Combine(framework, "settings.json"), "current framework settings");

        Assert.Equal(currentRoot, RuntimeDataPaths.GetProductDirectory(scratch.Path));
        Assert.Equal("recorded macro library", File.ReadAllText(Path.Combine(currentRoot, "Onboard", "macros.json")));
        Assert.Equal("current framework settings", File.ReadAllText(Path.Combine(framework, "settings.json")));
        Assert.Equal("recorded macro library", File.ReadAllText(Path.Combine(old, "macros.json")));
    }

    [Test]
    public async Task LegacyBackupHeadersRemainLoadableAndRestorableAfterTheProductRename()
    {
        using var scratch = new ScratchDirectory();
        var original = Fixture.Create(); original.Format = MemorySnapshot.LegacyFormat;
        var file = Path.Combine(scratch.Path, "legacy.json");
        await OnboardMemory.SaveBackupAsync(original, file);
        var loaded = await OnboardMemory.LoadBackupAsync(file);
        Assert.Equal(MemorySnapshot.LegacyFormat, loaded.Format);
        var plan = MemoryWritePlan.Restore(Fixture.Create(), loaded);
        Assert.Equal(0, plan.Changes.Count);
        Assert.Equal(MemorySnapshot.CurrentFormat, new MemorySnapshot().Format);
        Assert.Equal(false, MemorySnapshot.IsKnownFormat("Unrecognized.Onboard.v1"));
    }
}
