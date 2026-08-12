namespace FSOps.Data.Import;

/// <summary>
/// Where the bundled world-data CSVs live at runtime.
///
/// <para>AppContext.BaseDirectory rather than the host's content root, so this resolves the same
/// way under "dotnet run" (the project's bin output) and from a published, installed executable -
/// both ship the data/ folder next to the assembly. Resolving it from the content root instead has
/// already broken the installed app once.</para>
/// </summary>
public static class WorldDataSeedPaths
{
    public static string BundledSeedDirectory => Path.Combine(AppContext.BaseDirectory, "data");
}
