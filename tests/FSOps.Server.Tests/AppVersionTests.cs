using System.Reflection;
using System.Reflection.Emit;
using FSOps.Server.Services;

namespace FSOps.Server.Tests;

/// <summary>
/// Version resolution, tested against assemblies built for the purpose rather than against whatever
/// the test host happens to be versioned as - the whole point of the type is that it survives builds
/// that carry version metadata and builds that do not.
/// </summary>
public class AppVersionTests
{
    private static Assembly AssemblyWith(string? informationalVersion, Version? assemblyVersion = null)
    {
        var name = new AssemblyName("FSOps.VersionProbe." + Guid.NewGuid().ToString("N"));
        if (assemblyVersion is not null)
        {
            name.Version = assemblyVersion;
        }

        var builder = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);

        if (informationalVersion is not null)
        {
            var constructor = typeof(AssemblyInformationalVersionAttribute).GetConstructor(new[] { typeof(string) })!;
            builder.SetCustomAttribute(new CustomAttributeBuilder(constructor, new object[] { informationalVersion }));
        }

        return builder;
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("1.4.2", "1.4.2")]
    public void TheInformationalVersionIsUsedWhenItIsPresent(string attribute, string expected)
    {
        Assert.Equal(expected, AppVersion.Resolve(AssemblyWith(attribute)));
    }

    [Theory]
    [InlineData("0.2.0+9a3f21c", "0.2.0")]
    [InlineData("1.0.0+build.42", "1.0.0")]
    public void BuildMetadataIsStripped_SoSourceLinkDoesNotBreakTheComparison(string attribute, string expected)
    {
        // MSBuild and SourceLink append "+<commit>" automatically. Left in place, that value would
        // still compare correctly (semver ignores build metadata) but would be shown to the user.
        Assert.Equal(expected, AppVersion.Resolve(AssemblyWith(attribute)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+onlymetadata")]
    public void AnAssemblyWithNoUsableVersionMetadata_FallsBackRatherThanReturningNothing(string? attribute)
    {
        // A trimmed or single-file publish can strip this. Returning null here would leave the
        // updater unable to compare anything at all, which is worse than a possibly-stale constant.
        Assert.Equal(AppVersion.Fallback, AppVersion.Resolve(AssemblyWith(attribute)));
    }

    [Fact]
    public void TheMsBuildDefaultAssemblyVersionIsNotMistakenForARealAnswer()
    {
        // 1.0.0.0 is what an assembly reports when nobody set <Version> at all. Trusting it would
        // make the app believe it is newer than every release below 1.0.0 and go permanently quiet.
        Assert.Equal(AppVersion.Fallback, AppVersion.Resolve(AssemblyWith(null, new Version(1, 0, 0, 0))));
    }

    [Fact]
    public void ADeliberateAssemblyVersionIsUsedWhenThereIsNoInformationalVersion()
    {
        Assert.Equal("2.3.4", AppVersion.Resolve(AssemblyWith(null, new Version(2, 3, 4, 0))));
    }

    [Fact]
    public void TheRunningBuildsVersionIsAlwaysSomethingTheUpdaterCanCompare()
    {
        // Whatever the build carries, it must at minimum parse - otherwise the updater refuses to
        // offer anything and nobody would ever find out why.
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Current));
        Assert.True(SemanticVersion.TryParse(AppVersion.Current, out _));
    }
}
