using System.Text.Json;
using FSOps.Core;

namespace FSOps.Desktop;

/// <summary>Saved size, position and maximised state of the main window.</summary>
internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>
/// Remembers where the window was last time. Stored in the FSOps data directory rather than beside
/// the executable, for the same reason as the database: the app installs into Program Files, which
/// a standard user cannot write to.
///
/// <para>
/// Every read is validated against the monitors that exist right now. A saved position is only
/// honoured if a usable slice of the window would land on a screen - otherwise a user who
/// undocks a laptop, or unplugs the second monitor the window was on, gets an application that
/// starts entirely off-screen and looks like it failed to launch.
/// </para>
/// </summary>
internal static class WindowPlacementStore
{
    private const string FileName = "window.json";

    /// <summary>How much of the window must be visible for a saved position to be reused.</summary>
    private const int MinimumVisibleEdge = 120;

    private static string? PathOrNull()
    {
        try
        {
            return System.IO.Path.Combine(AppPaths.DataDirectory, FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static WindowPlacement? Load()
    {
        var path = PathOrNull();
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(WindowPlacement placement)
    {
        var path = PathOrNull();
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(placement));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the window position is not worth an error dialog.
        }
    }

    /// <summary>
    /// True when the saved rectangle overlaps a real screen by enough that the user could grab it.
    /// Kept separate and pure so the off-screen rule is testable without a display attached.
    /// </summary>
    public static bool IsOnScreen(WindowPlacement placement, IEnumerable<Rectangle> screenBounds)
    {
        var window = new Rectangle(placement.X, placement.Y, placement.Width, placement.Height);
        foreach (var screen in screenBounds)
        {
            var overlap = Rectangle.Intersect(window, screen);
            if (overlap.Width >= MinimumVisibleEdge && overlap.Height >= MinimumVisibleEdge)
            {
                return true;
            }
        }

        return false;
    }
}
