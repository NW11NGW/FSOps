using System.Text.Json;
using FSOps.Core.Entities;

namespace FSOps.Core.Flights;

/// <summary>One sampled point of a flown track, in the order it was recorded.</summary>
/// <param name="Utc">When the sample was taken.</param>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east, as recorded - never normalised or unwrapped here; drawing
/// a path that crosses the antimeridian is the renderer's job (see the frontend's
/// <c>splitAntimeridian</c>), and quietly rewriting a longitude would corrupt the record.</param>
/// <param name="AltitudeMslFt">Altitude above mean sea level, feet. Null if the sample never carried one.</param>
/// <param name="GroundSpeedKt">Ground speed, knots. Null if the sample never carried one.</param>
/// <param name="Phase">The flight phase the state machine was in at this sample, e.g. "Cruise". Null on an older row that never recorded one.</param>
public sealed record FlownTrackPoint(
    DateTimeOffset Utc,
    double Latitude,
    double Longitude,
    double? AltitudeMslFt,
    double? GroundSpeedKt,
    string? Phase);

/// <summary>
/// The result of reading a flight's recorded track.
/// </summary>
/// <param name="Points">The points to draw, in time order. Possibly thinned - see <paramref name="Thinned"/>.</param>
/// <param name="RecordedPointCount">How many position samples were actually recorded for this
/// flight, before any thinning. Always the honest total, so the UI can say "showing 500 of 2,880"
/// rather than implying the thinned figure is all that exists.</param>
/// <param name="Thinned">True when <paramref name="Points"/> is a subsample of the recorded track.</param>
public sealed record FlownTrack(
    IReadOnlyList<FlownTrackPoint> Points,
    int RecordedPointCount,
    bool Thinned);

/// <summary>
/// Reads the path a flight actually flew out of its append-only
/// <see cref="FlightEventType.PositionSnapshot"/> rows - the roughly-15-second position stream
/// <c>FlightLifecycleService</c> has always written and nothing has ever shown.
/// <para>
/// Pure and deterministic (no clock, no database, no randomness), so the awkward cases can be
/// unit-tested with exact expected values rather than hoped about:
/// </para>
/// <list type="bullet">
/// <item>A flight with <b>no snapshots at all</b> - every flight flown before position snapshots
/// existed, and <b>every virtual-pilot flight</b>, which never had a simulator attached and writes
/// no events whatsoever - returns zero points. That is a legitimate answer, not a failure, and the
/// caller must render it as "no track was recorded", never as a broken map.</item>
/// <item>A flight with <b>one point</b> returns that one point. It is a position, not a path; a
/// caller must not draw a line through it.</item>
/// <item>A <b>malformed or truncated payload</b> is skipped rather than throwing. These rows are
/// append-only history: one unreadable row must never cost the player the whole track.</item>
/// </list>
/// <para>
/// <b>Thinning is a rendering concession and nothing else.</b> When a track has more than
/// <paramref name="maxPoints"/> samples, an evenly-spaced subset is returned so a long-haul sector
/// does not ship megabytes of JSON to draw a line whose shape is identical either way. The first
/// and last points are always kept, so the track still begins and ends exactly where it really did.
/// The stored rows are never touched, and <see cref="FlownTrack.RecordedPointCount"/> always reports
/// the true total so the reduction is disclosed rather than hidden.
/// </para>
/// </summary>
public static class FlownTrackBuilder
{
    /// <summary>
    /// Default cap on returned points. 1,500 samples at the recorded ~15s cadence is over six hours
    /// of flying at full resolution, so all but genuinely long-haul sectors are returned untouched;
    /// beyond that the extra samples add payload without adding a visible bend to the line.
    /// </summary>
    public const int DefaultMaxPoints = 1500;

    public static FlownTrack Build(IEnumerable<FlightEvent> events, int maxPoints = DefaultMaxPoints)
    {
        var cap = Math.Max(2, maxPoints);

        var points = events
            .Where(e => e.Type == FlightEventType.PositionSnapshot)
            .OrderBy(e => e.Utc)
            .Select(TryReadPoint)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        if (points.Count <= cap)
        {
            return new FlownTrack(points, points.Count, Thinned: false);
        }

        return new FlownTrack(Thin(points, cap), points.Count, Thinned: true);
    }

    /// <summary>
    /// Evenly-spaced subsample of <paramref name="points"/> holding at most <paramref name="cap"/>
    /// entries, always including the very first and very last. Index-based rather than
    /// time-based on purpose: a gap in the recorded stream (the sim disconnected mid-sector and
    /// reconnected later) is a real feature of the track, and a time-based resample would invent
    /// evenly-spaced points across a stretch where nothing was ever observed.
    /// </summary>
    private static List<FlownTrackPoint> Thin(IReadOnlyList<FlownTrackPoint> points, int cap)
    {
        var kept = new List<FlownTrackPoint>(cap);
        // cap - 1 intervals across the full index range, so step lands exactly on the last index.
        var step = (points.Count - 1) / (double)(cap - 1);
        for (var i = 0; i < cap; i++)
        {
            var index = (int)Math.Round(i * step, MidpointRounding.AwayFromZero);
            if (index >= points.Count) index = points.Count - 1;
            var point = points[index];
            // Rounding can land twice on the same index at the very end; never emit a duplicate.
            if (kept.Count > 0 && ReferenceEquals(kept[^1], point)) continue;
            kept.Add(point);
        }

        return kept;
    }

    /// <summary>
    /// Parses one <see cref="FlightEventType.PositionSnapshot"/> payload. Returns null for anything
    /// unreadable - malformed JSON, a payload with no usable latitude/longitude pair, or a
    /// coordinate outside the range the Earth actually has. Every optional field is read
    /// defensively: rows written by earlier versions may carry fewer keys, and a missing altitude is
    /// "not recorded", never zero.
    /// </summary>
    private static FlownTrackPoint? TryReadPoint(FlightEvent evt)
    {
        try
        {
            using var doc = JsonDocument.Parse(evt.PayloadJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryReadDouble(root, "lat", out var lat) || !TryReadDouble(root, "lon", out var lon))
            {
                return null;
            }

            if (double.IsNaN(lat) || double.IsNaN(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
            {
                return null;
            }

            double? altitude = TryReadDouble(root, "altMslFt", out var alt) ? alt : null;
            double? groundSpeed = TryReadDouble(root, "gsKt", out var gs) ? gs : null;
            string? phase = root.TryGetProperty("phase", out var phaseEl) && phaseEl.ValueKind == JsonValueKind.String
                ? phaseEl.GetString()
                : null;

            return new FlownTrackPoint(evt.Utc, lat, lon, altitude, groundSpeed, phase);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadDouble(JsonElement root, string name, out double value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value);
    }
}
