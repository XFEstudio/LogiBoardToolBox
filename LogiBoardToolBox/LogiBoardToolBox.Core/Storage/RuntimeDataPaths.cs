namespace LogiBoardToolBox.Core.Storage;

/// <summary>One-time, non-destructive migration of the former product's onboard settings and backups.</summary>
public static class RuntimeDataPaths
{
    public const string ProductName = "LogiBoardToolBox";
    public const string LegacyProductName = "LogiBordToolBox";

    public static string GetProductDirectory(string localApplicationData)
    {
        var parent = Path.GetFullPath(localApplicationData);
        var current = Path.Combine(parent, ProductName);
        var legacy = Path.Combine(parent, LegacyProductName);
        var currentOnboard = Path.Combine(current, "Onboard");
        var legacyOnboard = Path.Combine(legacy, "Onboard");
        // The configuration framework can create the product root before this view model.
        // Only an existing onboard library means the migration has already been handled.
        if (Directory.Exists(currentOnboard) || !Directory.Exists(legacyOnboard)) return current;
        var staging = Path.Combine(parent, ProductName + ".migrating-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(legacyOnboard, staging);
            Directory.CreateDirectory(current);
            if (!Directory.Exists(currentOnboard)) Directory.Move(staging, currentOnboard);
            return current;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Original data is never moved or deleted. A failed migration must not hide it.
            return Directory.Exists(currentOnboard) ? current : legacy;
        }
        finally
        {
            if (Directory.Exists(staging) && Path.GetDirectoryName(Path.GetFullPath(staging)) == parent
                && Path.GetFileName(staging).StartsWith(ProductName + ".migrating-", StringComparison.Ordinal))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("迁移不会跟随数据目录链接。");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("迁移不会跟随数据文件链接。");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
