namespace FSOps.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void DataDirectory_DefaultsToLocalAppData()
    {
        var resolved = AppPaths.ResolveDataDirectoryFrom(null);
        var expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(expectedRoot, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("FSOps", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DataDirectory_IgnoresBlankOverride(string blank)
    {
        var expected = AppPaths.ResolveDataDirectoryFrom(null);

        Assert.Equal(expected, AppPaths.ResolveDataDirectoryFrom(blank));
    }

    [Fact]
    public void DataDirectory_UsesOverrideWhenSet()
    {
        var target = Path.Combine(Path.GetTempPath(), "fsops-override-test");

        var resolved = AppPaths.ResolveDataDirectoryFrom(target);

        Assert.Equal(Path.GetFullPath(target), resolved);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Path.DirectorySeparatorChar + "FSOps",
            resolved,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DataDirectory_MakesRelativeOverrideAbsolute()
    {
        // The working directory differs between dotnet run, a launched exe and the test host,
        // so a relative override must be pinned down rather than left to chance.
        var resolved = AppPaths.ResolveDataDirectoryFrom("some-relative-dir");

        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith("some-relative-dir", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DataDirectory_TrimsSurroundingWhitespaceInOverride()
    {
        var target = Path.Combine(Path.GetTempPath(), "fsops-trim-test");

        Assert.Equal(Path.GetFullPath(target), AppPaths.ResolveDataDirectoryFrom("  " + target + "  "));
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
