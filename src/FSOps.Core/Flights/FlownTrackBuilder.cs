using System.Text.Json;
using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Flights;

/// <summary>
/// Where a sector departed from, used as the fixed point a track's opening is judged against - see
/// <see cref="FlownTrackBuilder.AnchorRadiusNm"/>. Deliberately a plain coordinate rather than an
/// airport: the builder stays pure and takes no dependency on the database or on how the caller
/// resolved the ICAO.
/// </summary>
public readonly record struct TrackAnchor(double LatitudeDeg, double LongitudeDeg);

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
/// <param name="DiscardedLeadingPointCount">How many points were dropped off the FRONT of the track
/// because the simulator had not yet reported a real position when they were recorded - see
/// <see cref="FlownTrackBuilder"/>. Disclosed rather than hidden: together with
/// <paramref name="RecordedPointCount"/> it lets the UI say "228 recorded, 2 discarded, 226 drawn"
/// instead of quietly showing fewer points than the flight has.</param>
public sealed record FlownTrack(
    IReadOnlyList<FlownTrackPoint> Points,
    int RecordedPointCount,
    bool Thinned,
    int DiscardedLeadingPointCount = 0);

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
/// <b>The opening of a track cannot be taken on trust.</b> SimConnect delivers a packet before the
/// sim has a real aircraft state to put in it, and the position in that packet is roughly 0.0N
/// 90.0E - the middle of the Indian Ocean. Those samples were written into real flights' snapshot
/// rows, and drawing them dragged the map's bounds across half the planet: a Bristol-Edinburgh
/// sector rendered as a line from Scandinavia to the coast of Africa. So leading points are
/// discarded until the aircraft is somewhere it could credibly be, using the one fixed point the
/// flight actually knows - the airport it departed from. See <see cref="AnchorRadiusNm"/> and
/// <see cref="FlownTrack.DiscardedLeadingPointCount"/>.
/// </para>
/// <para>
/// <b>Only the LEADING run is ever discarded, and only when the data itself says it cannot belong
/// to this flight.</b> A jump in the MIDDLE of a track is a completely different thing - a teleport,
/// a scenery reload, or slew - and it survives here untouched on purpose. That is
/// <see cref="FlightIntegrityMonitor"/>'s business, it is recorded on the flight as
/// <c>PositionJumpDetected</c>, and it is the evidence for a sector not being paid. A track that
/// quietly smoothed such a jump away would be hiding exactly the thing the integrity system exists
/// to preserve.
/// </para>
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

    /// <summary>
    /// How close to its departure airport the aircraft has to be before a recorded position is
    /// believed to be the start of this flight. A hundred miles is far beyond any pushback, taxi or
    /// even a late start on the initial climb, and it is under two percent of the 5,505 nm the bad
    /// opening fix sat away from the stand - so the two are never close to being confused. It is
    /// deliberately generous rather than tight, because the cost of the two errors is not
    /// symmetrical: keeping a slightly odd point draws a slightly odd line, while discarding a real
    /// one deletes evidence of where the aircraft went.
    /// </summary>
    public const double AnchorRadiusNm = 100.0;

    /// <summary>
    /// With no departure anchor, at most this many leading points may be discarded. At the recorded
    /// ~15-second cadence that is the opening two minutes - long enough to cover the burst of junk
    /// fixes actually observed, short enough that a jump later in the flight can never be mistaken
    /// for one and quietly removed.
    /// </summary>
    public const int MaxUnanchoredLeadingDiscard = 8;

    /// <summary>
    /// With no departure anchor, two consecutive samples must ALSO be this far apart before the step
    /// between them is treated as a bad opening fix rather than as flying.
    /// <para>
    /// The anchored rule can afford to ask only "is this step impossible?", because the anchor
    /// already bounds it to points recorded before the aircraft was ever near its own departure
    /// airport. Without that bound, the impossible-step test alone is too eager: it fires on any
    /// opening pair implying more than <see cref="FlightIntegrityMonitor.ImpossibleGroundSpeedKt"/>,
    /// which is a real teleport as often as it is a bad fix. So the unanchored rule demands a
    /// separation the simulator cannot produce at all: MSFS tops out at 128x, and 128 times a 500 kt
    /// airliner still only covers about 270 nm between two 15-second samples. A thousand miles
    /// between consecutive samples is not fast flying under any setting - it is the sim reporting a
    /// position it does not have.
    /// </para>
    /// </summary>
    public const double UnanchoredSeparationNm = 1000.0;

    /// <param name="events">The flight's events. Non-snapshot rows are ignored.</param>
    /// <param name="departureAnchor">Where this sector departed from, when the caller can resolve
    /// it. Null is handled - see <see cref="LeadingPointsToDiscard"/> for what happens instead - but
    /// passing it makes the opening-fix rule far safer, because it bounds the discard to points
    /// recorded before the aircraft was ever anywhere near its own departure airport.</param>
    /// <param name="maxSimulationRate">The highest simulation rate observed on this flight
    /// (<c>Flight.MaxSimulationRateObserved</c>). Snapshots do not record the rate they were taken
    /// at, and a sector flown at 4x covers four times the ground between two samples, so without
    /// this an accelerated cruise reads as a teleport - the same normalisation
    /// <see cref="FlightIntegrityMonitor"/> and <see cref="PositionAcquisitionGate"/> both apply.</param>
    /// <param name="maxPoints">Cap on returned points - see <see cref="DefaultMaxPoints"/>.</param>
    public static FlownTrack Build(
        IEnumerable<FlightEvent> events,
        TrackAnchor? departureAnchor = null,
        double maxSimulationRate = 1.0,
        int maxPoints = DefaultMaxPoints)
    {
        var cap = Math.Max(2, maxPoints);

        var recorded = events
            .Where(e => e.Type == FlightEventType.PositionSnapshot)
            .OrderBy(e => e.Utc)
            .Select(TryReadPoint)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        var discarded = LeadingPointsToDiscard(recorded, departureAnchor, maxSimulationRate);
        var points = discarded == 0
            ? recorded
            : recorded.GetRange(discarded, recorded.Count - discarded);

        if (points.Count <= cap)
        {
            return new FlownTrack(points, recorded.Count, Thinned: false, discarded);
        }

        return new FlownTrack(Thin(points, cap), recorded.Count, Thinned: true, discarded);
    }

    /// <summary>
    /// How many points at the FRONT of <paramref name="points"/> were recorded before the simulator
    /// had a real position to report, and so must not be drawn.
    /// <para>
    /// The rule is an anchor, not a blocklist. Special-casing the coordinates the bad fix happens to
    /// land on would fix one simulator's one symptom and nothing else; asking "had the aircraft got
    /// anywhere near the airport this sector departs from yet?" is a question with a right answer
    /// whatever the sim reports. So: find the first point within <see cref="AnchorRadiusNm"/> of the
    /// departure airport, and discard everything before it.
    /// </para>
    /// <para>
    /// <b>Two guards keep that from eating real data.</b> First, the prefix is only removed when the
    /// step into the anchored point is physically impossible - the same
    /// <see cref="FlightIntegrityMonitor.ImpossibleGroundSpeedKt"/> test, at the same rate
    /// normalisation. A flight that legitimately begins its recording a little away from the gate,
    /// or well outside the radius and flies in, connects to the rest of the track at an ordinary
    /// speed and keeps every point. Second, nothing is discarded when no point is ever near the
    /// anchor: a recording that began mid-sector, a hundred miles out and flying away, never comes
    /// within the radius, and it is shown exactly as recorded.
    /// </para>
    /// <para>
    /// <b>With no anchor</b> - the route or airport row is gone, which is the only way that happens
    /// - the fallback keeps the impossible-step test but adds
    /// <see cref="UnanchoredSeparationNm"/> on top, and looks only inside the opening
    /// <see cref="MaxUnanchoredLeadingDiscard"/> points. It has to be stricter precisely because it
    /// has nothing bounding it: with no anchor it cannot tell a bad opening fix from a genuine
    /// teleport thirty seconds in, so it only acts on a separation no simulator can produce at any
    /// time-compression setting. Anything less than that is left alone, and a real jump remains
    /// separately and permanently recorded on the flight itself as <c>PositionJumpDetected</c>.
    /// </para>
    /// <para>
    /// A track that is junk from beginning to end - nothing in it ever near the departure airport -
    /// is returned WHOLE. Nothing here can prove it is junk, and a track that cannot be verified is
    /// better shown as recorded than deleted on a guess.
    /// </para>
    /// </summary>
    private static int LeadingPointsToDiscard(
        IReadOnlyList<FlownTrackPoint> points, TrackAnchor? anchor, double maxSimulationRate)
    {
        // One point is a position, not a path: there is no second reading to judge it against, and
        // discarding it would leave the flight with nothing at all.
        if (points.Count < 2)
        {
            return 0;
        }

        var firstBelievable = anchor is { } a
            ? FirstPointNear(points, a)
            : FirstPointAfterAnImpossibleOpeningStep(points, maxSimulationRate);

        if (firstBelievable <= 0)
        {
            return 0;
        }

        return IsImpossibleStep(points[firstBelievable - 1], points[firstBelievable], maxSimulationRate)
            ? firstBelievable
            : 0;
    }

    /// <summary>Index of the first point within <see cref="AnchorRadiusNm"/> of the anchor, or -1.</summary>
    private static int FirstPointNear(IReadOnlyList<FlownTrackPoint> points, TrackAnchor anchor)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var distanceNm = GreatCircle.DistanceNm(
                anchor.LatitudeDeg, anchor.LongitudeDeg, points[i].Latitude, points[i].Longitude);
            if (distanceNm <= AnchorRadiusNm)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Index of the point after the LAST impossible step inside the opening
    /// <see cref="MaxUnanchoredLeadingDiscard"/> points, or -1. The last rather than the first, so a
    /// run of several junk fixes that jump about among themselves is removed in one piece instead of
    /// leaving the later ones behind.
    /// </summary>
    private static int FirstPointAfterAnImpossibleOpeningStep(
        IReadOnlyList<FlownTrackPoint> points, double maxSimulationRate)
    {
        var found = -1;
        var limit = Math.Min(points.Count - 1, MaxUnanchoredLeadingDiscard);
        for (var i = 1; i <= limit; i++)
        {
            var separationNm = GreatCircle.DistanceNm(
                points[i - 1].Latitude, points[i - 1].Longitude, points[i].Latitude, points[i].Longitude);
            if (separationNm >= UnanchoredSeparationNm && IsImpossibleStep(points[i - 1], points[i], maxSimulationRate))
            {
                found = i;
            }
        }

        return found;
    }

    /// <summary>
    /// True when getting from <paramref name="from"/> to <paramref name="to"/> in the time between
    /// them would need a speed no aircraft has. The arithmetic is
    /// <see cref="FlightIntegrityMonitor"/>'s own, CALLED rather than restated - see
    /// <see cref="PositionAcquisitionGate"/> for what happened the last time two copies of this
    /// reasoning were allowed to drift apart.
    /// </summary>
    private static bool IsImpossibleStep(FlownTrackPoint from, FlownTrackPoint to, double maxSimulationRate)
    {
        var interval = to.Utc - from.Utc;
        if (interval <= TimeSpan.Zero)
        {
            // Duplicate or out-of-order timestamps carry no speed information at all, so they can
            // neither prove nor disprove anything. Never treat "no evidence" as evidence.
            return false;
        }

        var judgedInterval = interval < FlightIntegrityMonitor.MinimumJudgeableInterval
            ? FlightIntegrityMonitor.MinimumJudgeableInterval
            : interval;
        var rate = Math.Max(1.0, maxSimulationRate);
        var distanceNm = GreatCircle.DistanceNm(from.Latitude, from.Longitude, to.Latitude, to.Longitude);

        return distanceNm / judgedInterval.TotalHours / rate > FlightIntegrityMonitor.ImpossibleGroundSpeedKt;
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
