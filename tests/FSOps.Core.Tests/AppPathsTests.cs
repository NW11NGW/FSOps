namespace FSOps.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void DataDirectory_IsUnderLocalAppData()
    {
        var expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(expectedRoot, AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("FSOps", AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DataDirectory_IsCreatedOnAccess()
    {
        var path = AppPaths.DataDirectory;

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void LogsDirectory_IsUnderDataDirectoryAndCreated()
    {
        var path = AppPaths.LogsDirectory;

        Assert.True(Directory.Exists(path));
        Assert.StartsWith(AppPaths.DataDirectory, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DatabasePath_IsInsideDataDirectory()
    {
        Assert.StartsWith(AppPaths.DataDirectory, AppPaths.DatabasePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("fsops.db", AppPaths.DatabasePath, StringComparison.OrdinalIgnoreCase);
    }
}
