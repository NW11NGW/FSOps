using System.Text.Json;
using FSOps.Core.Entities;

namespace FSOps.Core.Flights;

/// <summary>
/// Turns a stream of telemetry samples into an explicit flight phase, with hysteresis so a noisy
/// reading can't flap the phase back and forth, plus OOOI capture and landing-quality collection.
/// Pure - no I/O, no wall-clock reads - so it is entirely driven, and entirely determined, by the
/// samples fed to <see cref="Advance"/>. See <see cref="FlightPhaseThresholds"/> for every guard
/// number used below.
/// <para>
/// Guards, in the order phases normally progress:
/// Preflight -> TaxiOut: on the ground, rolling faster than <see cref="FlightPhaseThresholds.TaxiGroundSpeedKt"/>, parking brake off.
/// TaxiOut -> TakeoffRoll: on the ground, faster than <see cref="FlightPhaseThresholds.TakeoffRollGroundSpeedKt"/>.
/// TakeoffRoll -> Climb: airborne (this is also "Off" for OOOI). VS is essentially guaranteed to be
///   climbing at that instant, so the phase machine does not gate this specific edge on VS as well -
///   that would only add a gap between "airborne" and "recognised as climbing" with no benefit.
/// Climb -> Cruise: |VS| under <see cref="FlightPhaseThresholds.CruiseVerticalSpeedBandFpm"/>, sustained.
/// Cruise -> Descent: VS under minus <see cref="FlightPhaseThresholds.DescentVerticalSpeedFpm"/>, sustained.
/// Descent -> Approach: AGL under <see cref="FlightPhaseThresholds.ApproachAltitudeAglFt"/> while still descending.
/// Approach -> Landed: on-ground edge (this is also "On" for OOOI, and starts touchdown/bounce capture).
/// Landed -> TaxiIn: ground speed under <see cref="FlightPhaseThresholds.TaxiInGroundSpeedKt"/>.
/// TaxiIn -> Shutdown: engines off and parking brake set (this is also "In" for OOOI).
/// Landed -> Climb: go-around - airborne again for longer than <see cref="FlightPhaseThresholds.GoAroundAirborneDuration"/>.
///   Shorter airborne spells are treated as a bounce, not a go-around, and the touchdown they
///   produced is kept.
/// </para>
/// <para>Every other combination is a no-op: the machine only ever moves forward through <see cref="FlightPhase"/>, with that one explicit exception.</para>
/// </summary>
public sealed class FlightPhaseStateMachine
{
    private readonly FlightPhaseThresholds _thresholds;
    private readonly List<TouchdownRecord> _touchdowns = new();

    private FlightPhase _phase = FlightPhase.Preflight;
    private FlightTelemetrySample? _lastSample;
    private bool _hasPrevious;

    private DateTimeOffset? _cruiseCandidateStartUtc;
    private DateTimeOffset? _descentCandidateStartUtc;
    private DateTimeOffset? _airborneAgainStartUtc;
    private DateTimeOffset? _bounceGroupStartUtc;
    private DateTimeOffset? _touchdownPeakWindowEndUtc;

    public FlightPhaseStateMachine(FlightPhaseThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? new FlightPhaseThresholds();
    }

    public FlightPhase CurrentPhase => _phase;

    public DateTimeOffset? OutUtc { get; private set; }

    public DateTimeOffset? OffUtc { get; private set; }

    public DateTimeOffset? OnUtc { get; private set; }

    public DateTimeOffset? InUtc { get; private set; }

    /// <summary>Every ground-contact event of the current landing attempt, in order. Cleared on a go-around.</summary>
    public IReadOnlyList<TouchdownRecord> Touchdowns => _touchdowns;

    public TouchdownRecord? FirstTouchdown => _touchdowns.Count > 0 ? _touchdowns[0] : null;

    /// <summary>The hardest of the current landing's contacts, ranked by sink rate.</summary>
    public TouchdownRecord? HardestTouchdown =>
        _touchdowns.Count == 0 ? null : _touchdowns.OrderByDescending(t => t.Fpm).First();

    /// <summary>Bounces after the first, genuine contact. Zero for a clean, single-contact landing.</summary>
    public int BounceCount => Math.Max(0, _touchdowns.Count - 1);

    /// <summary>Feeds one telemetry sample through the machine and reports what happened.</summary>
    public FlightPhaseAdvanceResult Advance(FlightTelemetrySample sample)
    {
        var previousPhase = _phase;
        var priorOnGround = _hasPrevious ? _lastSample!.OnGround : sample.OnGround;
        var risingEdge = !priorOnGround && sample.OnGround;

        TouchdownRecord? newTouchdown = null;
        var wasGoAround = false;

        // Keep tracking the peak G-force of the most recent contact for a short window afterwards -
        // the true peak of an impact often lands a sample or two after the edge itself.
        if (_touchdownPeakWindowEndUtc is { } peakEnd && sample.TimestampUtc <= peakEnd && _touchdowns.Count > 0)
        {
            var last = _touchdowns[^1];
            if (sample.GForce > last.GForce)
            {
                _touchdowns[^1] = last with { GForce = sample.GForce };
            }
        }

        switch (_phase)
        {
            case FlightPhase.Preflight:
                if (sample.OnGround && sample.GroundSpeedKt > _thresholds.TaxiGroundSpeedKt && !sample.ParkingBrakeSet)
                {
                    OutUtc ??= sample.TimestampUtc;
                    _phase = FlightPhase.TaxiOut;
                }

                break;

            case FlightPhase.TaxiOut:
                if (sample.OnGround && sample.GroundSpeedKt > _thresholds.TakeoffRollGroundSpeedKt)
                {
                    _phase = FlightPhase.TakeoffRoll;
                }

                break;

            case FlightPhase.TakeoffRoll:
                if (!sample.OnGround)
                {
                    OffUtc ??= sample.TimestampUtc;
                    _phase = FlightPhase.Climb;
                }

                break;

            case FlightPhase.Climb:
                if (Math.Abs(sample.VerticalSpeedFpm) < _thresholds.CruiseVerticalSpeedBandFpm)
                {
                    _cruiseCandidateStartUtc ??= sample.TimestampUtc;
                    if (sample.TimestampUtc - _cruiseCandidateStartUtc >= _thresholds.CruiseSustainDuration)
                    {
                        _phase = FlightPhase.Cruise;
                        _cruiseCandidateStartUtc = null;
                    }
                }
                else
                {
                    _cruiseCandidateStartUtc = null;
                }

                break;

            case FlightPhase.Cruise:
                if (sample.VerticalSpeedFpm < -_thresholds.DescentVerticalSpeedFpm)
                {
                    _descentCandidateStartUtc ??= sample.TimestampUtc;
                    if (sample.TimestampUtc - _descentCandidateStartUtc >= _thresholds.DescentSustainDuration)
                    {
                        _phase = FlightPhase.Descent;
                        _descentCandidateStartUtc = null;
                    }
                }
                else
                {
                    _descentCandidateStartUtc = null;
                }

                break;

            case FlightPhase.Descent:
                if (sample.AltitudeAglFt < _thresholds.ApproachAltitudeAglFt && sample.VerticalSpeedFpm < 0)
                {
                    _phase = FlightPhase.Approach;
                }

                break;

            case FlightPhase.Approach:
                if (risingEdge)
                {
                    newTouchdown = RegisterTouchdown(sample);
                    OnUtc ??= sample.TimestampUtc;
                    _phase = FlightPhase.Landed;
                }

                break;

            case FlightPhase.Landed:
                if (risingEdge)
                {
                    // A bounce - another contact belonging to the same landing attempt.
                    newTouchdown = RegisterTouchdown(sample);
                }

                if (!sample.OnGround)
                {
                    _airborneAgainStartUtc ??= sample.TimestampUtc;
                    if (sample.TimestampUtc - _airborneAgainStartUtc >= _thresholds.GoAroundAirborneDuration)
                    {
                        // Go-around: this wasn't the landing after all - clear what we captured for
                        // it so only the eventual real landing is scored.
                        _touchdowns.Clear();
                        OnUtc = null;
                        _bounceGroupStartUtc = null;
                        _touchdownPeakWindowEndUtc = null;
                        _airborneAgainStartUtc = null;
                        wasGoAround = true;
                        _phase = FlightPhase.Climb;
                    }
                }
                else
                {
                    _airborneAgainStartUtc = null;
                    if (sample.GroundSpeedKt < _thresholds.TaxiInGroundSpeedKt)
                    {
                        _phase = FlightPhase.TaxiIn;
                    }
                }

                break;

            case FlightPhase.TaxiIn:
                if (!sample.EngineRunning && sample.ParkingBrakeSet)
                {
                    InUtc ??= sample.TimestampUtc;
                    _phase = FlightPhase.Shutdown;
                }

                break;

            case FlightPhase.Shutdown:
                // Terminal - nothing more to do.
                break;
        }

        _lastSample = sample;
        _hasPrevious = true;

        var changed = _phase != previousPhase;
        return new FlightPhaseAdvanceResult(_phase, changed, changed ? previousPhase : null, wasGoAround, newTouchdown);
    }

    private TouchdownRecord RegisterTouchdown(FlightTelemetrySample sample)
    {
        var record = new TouchdownRecord(
            sample.TimestampUtc,
            sample.LatitudeDeg,
            sample.LongitudeDeg,
            sample.TrueHeadingDeg,
            sample.TouchdownNormalVelocityFps * 60.0,
            sample.GForce);

        _bounceGroupStartUtc ??= sample.TimestampUtc;
        _touchdowns.Add(record);
        _touchdownPeakWindowEndUtc = sample.TimestampUtc + _thresholds.TouchdownPeakGForceWindow;
        return record;
    }

    /// <summary>
    /// Rebuilds a machine's phase/OOOI/touchdown state from a flight's persisted, append-only
    /// event stream, so a flight in progress when the app was closed can pick back up from where
    /// it left off instead of starting over. Pure - just replays PhaseChange and Touchdown rows in
    /// order - so it needs no I/O of its own; the caller is responsible for loading the events.
    /// </summary>
    public static FlightPhaseStateMachine RestoreFrom(IEnumerable<FlightEvent> events, FlightPhaseThresholds? thresholds = null)
    {
        var machine = new FlightPhaseStateMachine(thresholds);

        foreach (var evt in events.OrderBy(e => e.Utc))
        {
            switch (evt.Type)
            {
                case FlightEventType.PhaseChange:
                    RestorePhaseChange(machine, evt);
                    break;

                case FlightEventType.Touchdown:
                    RestoreTouchdown(machine, evt);
                    break;
            }
        }

        return machine;
    }

    private static void RestorePhaseChange(FlightPhaseStateMachine machine, FlightEvent evt)
    {
        PhaseChangePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PhaseChangePayload>(evt.PayloadJson);
        }
        catch (JsonException)
        {
            return;
        }

        if (payload is null || !Enum.TryParse<FlightPhase>(payload.ToPhase, out var toPhase))
        {
            return;
        }

        machine._phase = toPhase;

        switch (toPhase)
        {
            case FlightPhase.TaxiOut:
                machine.OutUtc ??= evt.Utc;
                break;
            case FlightPhase.Climb when payload.WasGoAround:
                machine._touchdowns.Clear();
                machine.OnUtc = null;
                break;
            case FlightPhase.Climb:
                machine.OffUtc ??= evt.Utc;
                break;
            case FlightPhase.Landed:
                machine.OnUtc ??= evt.Utc;
                break;
            case FlightPhase.Shutdown:
                machine.InUtc ??= evt.Utc;
                break;
        }
    }

    private static void RestoreTouchdown(FlightPhaseStateMachine machine, FlightEvent evt)
    {
        TouchdownPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TouchdownPayload>(evt.PayloadJson);
        }
        catch (JsonException)
        {
            return;
        }

        if (payload is null)
        {
            return;
        }

        machine._touchdowns.Add(new TouchdownRecord(evt.Utc, payload.LatitudeDeg, payload.LongitudeDeg, payload.TrueHeadingDeg, payload.Fpm, payload.GForce));
    }
}
