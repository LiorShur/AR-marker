using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MarkerOne.Core
{
    public enum SessionState { Idle, Locating, Calibrating, Ready, Error }

    /// <summary>A placement expressed in the session's own frame, ready to be
    /// instantiated. Local coordinates are derived, never stored: the frame
    /// changes every time it re-localizes and the placements do not.</summary>
    public sealed class PlacedItem
    {
        public string Id;
        public string Scene;
        public double Scale;
        public double DistanceM;
        public string Owner;
        public string Label;
        public string Author;
        public string CreatedAt;
        public Vec3 Local;
        public double YawRad;

        /// <summary>Where this is on the globe, carried through unchanged.
        ///
        /// Local is this position resolved through the session's frame, and a
        /// frame is only ever as good as the fixes behind it. A renderer with
        /// access to something better — ARCore's geospatial anchors resolve a
        /// latitude and longitude against VPS continuously, rather than once
        /// through an estimate — needs the original to hand it over.
        /// </summary>
        public GeoPoint Position;

        /// <summary>Compass heading, clockwise from north, for the same
        /// reason: Local's yaw is relative to a frame, this is not.</summary>
        public double HeadingDeg;

        /// <summary>Metres above the floor it was left on. What a terrain
        /// anchor wants, and more trustworthy than any altitude.</summary>
        public double GroundOffset;

        /// <summary>How the pose was obtained. "map" means somebody dropped it
        /// on satellite imagery from a desk and it is accurate to whatever that
        /// was worth — which is the one case where a stranger walking past is
        /// better informed than the record.</summary>
        public string Provider;
    }

    /// <summary>
    /// Placements in a live session.
    ///
    /// Deliberately knows nothing about Unity, AR Foundation or ARCore. It takes
    /// fixes in and produces placements out; the platform layer supplies the
    /// device pose and instantiates what comes back. That separation is what
    /// makes any of this testable — an AR session cannot run on a build agent,
    /// but all of the logic that goes wrong can.
    ///
    /// The states read as a sentence: locating until there is a position,
    /// calibrating until there is a bearing, and only then ready. The middle
    /// step is the one nobody expects and the one that decides whether content
    /// lands in the right place.
    /// </summary>
    public sealed class WorldSession
    {
        private const int MaxSamples = 24;

        private readonly IPlacementStore _store;
        private readonly Func<double> _floor;
        private readonly List<BaselineSample> _samples = new();
        private readonly List<Placement> _placements = new();

        // Placements made in this session, remembered by the local point they
        // were dropped at. That point is exact; the mapping to the globe was
        // not.
        private readonly List<(string Id, Vec3 Local, double YawRad, GeoPoint At)> _mine = new();

        private BaselineHeading _heading;
        private string _headingSource = "none";
        private GeoPoint? _fetchedAt;
        private Vec3? _lastFixLocal;

        public double RadiusM { get; set; } = 300;
        public double RelocalizeAfterM { get; set; } = 25;

        public SessionState State { get; private set; } = SessionState.Idle;
        public LocalizationFrame Frame { get; private set; }
        public int Fixes => _samples.Count;
        public string LastError { get; private set; }

        public event Action<SessionState, string> StateChanged;
        public event Action<IReadOnlyList<PlacedItem>> PlacementsChanged;

        /// <summary>Compass heading and the measured spread of its readings.
        /// Averaging fixes noise and does nothing about bias, so the spread is
        /// floored well above what the arithmetic gives — a magnetometer beside
        /// a steel door reads twenty degrees wrong very consistently.</summary>
        public double? CompassHeadingDeg { get; set; }
        public double CompassSpreadDeg { get; set; } = 25;

        /// <summary>The name to credit on anything placed from here. Set by
        /// whoever knows who is signed in; empty is honest when nobody is.</summary>
        public string Author { get; set; } = "";

        public WorldSession(IPlacementStore store, Func<double> floor = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            // local-floor is the device's estimate of where the ground is,
            // recomputed each session and landing somewhere different each
            // time. A surface the hit test has actually touched does not move.
            _floor = floor ?? (() => 0);
        }

        private double Floor()
        {
            double y = _floor();
            return double.IsNaN(y) || double.IsInfinity(y) ? 0 : y;
        }

        // ── fixes ────────────────────────────────────────────────

        /// <summary>One fix, paired with where the session thought the device
        /// was at that moment. A position on the globe is only half of a
        /// bearing.</summary>
        public async Task AddFixAsync(Fix fix, Vec3 localPose, CancellationToken cancel = default)
        {
            if (fix == null) { throw new ArgumentNullException(nameof(fix)); }

            _samples.Add(new BaselineSample
            {
                Position = fix.Position,
                AccuracyM = fix.PositionAccuracyM <= 0 ? 30 : fix.PositionAccuracyM,
                Local = localPose,
                AtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            if (_samples.Count > MaxSamples) { _samples.RemoveAt(0); }

            await ResolveHeadingAsync(fix, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Three sources, and the best bearing wins.
        ///
        /// Direct is what a geospatial provider reports outright, about a
        /// degree. A walked baseline is atan(noise / separation), so anywhere
        /// from tenths of a degree with good fixes to twenty with poor ones.
        /// The compass is the floor of about twenty-five, and it exists so that
        /// standing still indoors is usable rather than impossible.
        ///
        /// Comparing them by their own accuracy figure rather than by rank is
        /// what lets a long walk outdoors beat a mediocre geospatial fix, and
        /// what stopped a wide baseline between two bad fixes beating a short
        /// one between good ones.
        /// </summary>
        private async Task ResolveHeadingAsync(Fix fix, CancellationToken cancel)
        {
            BaselineHeading candidate = null;
            string source = null;

            if (fix?.SessionYawDeg != null)
            {
                candidate = new BaselineHeading
                {
                    HeadingDeg = fix.SessionYawDeg.Value,
                    SessionYawDeg = fix.SessionYawDeg.Value,
                    SeparationM = 0,
                    AccuracyDeg = fix.SessionYawAccuracyDeg
                };
                source = "direct";
            }

            BaselineHeading walked = BestBaseline();
            if (walked != null && (candidate == null || walked.AccuracyDeg < candidate.AccuracyDeg))
            {
                candidate = walked;
                source = "baseline";
            }

            if (candidate == null && CompassHeadingDeg.HasValue)
            {
                candidate = new BaselineHeading
                {
                    HeadingDeg = CompassHeadingDeg.Value,
                    SessionYawDeg = CompassHeadingDeg.Value,
                    SeparationM = 0,
                    AccuracyDeg = CompassSpreadDeg
                };
                source = "compass";
            }

            if (candidate != null &&
                (_heading == null || candidate.AccuracyDeg < _heading.AccuracyDeg))
            {
                _heading = candidate;
                _headingSource = source;
            }

            if (_heading != null)
            {
                await RebuildAsync(fix, cancel).ConfigureAwait(false);
                return;
            }

            Emit(_samples.Count > 0 ? SessionState.Calibrating : SessionState.Locating, null);
        }

        /// <summary>The pair giving the best bearing wins — smallest
        /// atan(noise / separation), not widest separation.
        ///
        /// Those are not the same thing, and assuming they were is a bug this
        /// port inherited from the web version and only showed up under a test
        /// with mixed accuracies. A hundred-metre walk between two fixes that
        /// each admit to thirty metres is a twenty degree bearing; a forty
        /// metre walk between two claiming three is four. Taking the wider one
        /// throws away the better answer.</summary>
        private BaselineHeading BestBaseline()
        {
            BaselineHeading best = null;

            for (int i = 0; i < _samples.Count; i++)
            {
                for (int j = i + 1; j < _samples.Count; j++)
                {
                    BaselineHeading candidate = Baseline.FromWalk(_samples[i], _samples[j]);
                    if (candidate?.SessionYawDeg == null) { continue; }
                    if (best == null || candidate.AccuracyDeg < best.AccuracyDeg) { best = candidate; }
                }
            }

            return best;
        }

        private async Task RebuildAsync(Fix latest, CancellationToken cancel)
        {
            _lastFixLocal = _samples[_samples.Count - 1].Local;

            GeoPoint? origin = OriginEstimator.Estimate(_samples, _heading.SessionYawDeg ?? 0);
            if (origin == null) { return; }

            Frame = new LocalizationFrame(new Fix
            {
                Position = origin.Value,
                // The world heading of the session's forward axis, not the
                // device's — which is exactly what a baseline measures.
                HeadingDeg = _heading.SessionYawDeg ?? 0,
                PositionAccuracyM = OriginEstimator.Accuracy(_samples),
                HeadingAccuracyDeg = _heading.AccuracyDeg,
                Provider = latest?.Provider ?? "unknown",
                HeadingFrom = _headingSource
            }, SessionPose.Origin);

            Emit(SessionState.Ready, null);
            Reproject();

            await CorrectMineAsync(cancel).ConfigureAwait(false);
            await FetchIfNeededAsync(cancel).ConfigureAwait(false);
        }

        /// <summary>Session tracking drifts, quietly. Once the device has walked
        /// far enough for that to matter, the next fix re-anchors the frame.
        /// </summary>
        public bool NeedsRelocalize(Vec3 here)
        {
            if (Frame == null || !_lastFixLocal.HasValue) { return true; }
            double dx = here.X - _lastFixLocal.Value.X;
            double dz = here.Z - _lastFixLocal.Value.Z;
            return Math.Sqrt(dx * dx + dz * dz) > RelocalizeAfterM;
        }

        // ── content ──────────────────────────────────────────────

        public async Task RefreshAsync(CancellationToken cancel = default)
        {
            if (Frame == null) { return; }

            GeoPoint origin = Frame.Origin;
            _fetchedAt = origin;

            try
            {
                IReadOnlyList<Placement> found =
                    await _store.NearbyAsync(origin.Lat, origin.Lon, RadiusM, cancel).ConfigureAwait(false);

                _placements.Clear();
                _placements.AddRange(found);
                Reproject();
            }
            catch (Exception e)
            {
                // Let the next attempt try again rather than assuming this
                // centre is done with. A read that fails is not an empty world.
                _fetchedAt = null;
                Emit(SessionState.Error, "Could not read placements: " + e.Message);
                throw;
            }
        }

        /// <summary>Becoming located is what makes a query possible, so it is
        /// also what triggers one — and only again when the centre has moved
        /// enough for the answer to differ.</summary>
        private async Task FetchIfNeededAsync(CancellationToken cancel)
        {
            if (Frame == null) { return; }

            if (_fetchedAt.HasValue &&
                Geodesy.Haversine(Frame.Origin, _fetchedAt.Value) < RadiusM / 3)
            {
                return;
            }

            try { await RefreshAsync(cancel).ConfigureAwait(false); }
            catch { /* already reported */ }
        }

        public IReadOnlyList<PlacedItem> Reproject()
        {
            if (Frame == null) { return Array.Empty<PlacedItem>(); }

            double floor = Floor();
            var items = _placements.Select(p =>
            {
                double headingDeg = Geodesy.HeadingFromQuaternion(p.Orientation);
                Vec3 local = Frame.ToLocal(p.Position);

                // Vertical position comes from the floor of the current
                // session, not from the globe. Horizontally a few metres of GPS
                // error is a few metres sideways; vertically it is the
                // difference between being there and being underground.
                local = new Vec3(local.X, p.GroundOffset + floor, local.Z);

                return new PlacedItem
                {
                    Id = p.Id,
                    Scene = p.Scene,
                    Scale = p.Scale,
                    DistanceM = p.DistanceM,
                    Owner = p.Owner,
                    Label = p.Label,
                    Author = p.Author,
                    CreatedAt = p.CreatedAt,
                    Local = local,
                    YawRad = Frame.HeadingToLocalYaw(headingDeg),
                    Position = p.Position,
                    HeadingDeg = headingDeg,
                    GroundOffset = p.GroundOffset,
                    Provider = p.Fix?.Provider
                };
            }).ToList();

            PlacementsChanged?.Invoke(items);
            return items;
        }

        // ── placing ──────────────────────────────────────────────

        /// <summary>
        /// Place at coordinates somebody else worked out.
        ///
        /// The frame is an estimate and the caller may have access to a better
        /// one — on a device with Geospatial, ARCore will convert a session
        /// point to a latitude and longitude using the solution it is
        /// continuously refining, rather than the single rigid transform this
        /// session averaged out of a handful of fixes and then froze.
        ///
        /// Such a placement is not added to the correction list. That list
        /// exists to re-derive coordinates as the frame improves, and these
        /// coordinates did not come from the frame — rewriting them with it
        /// would replace a good answer with a worse one.
        /// </summary>
        public async Task<Placement> PlaceAtAsync(string scene, GeoPoint position,
            double headingDeg, Vec3 localPoint, string label = "",
            CancellationToken cancel = default)
        {
            return await WriteAsync(scene, position, headingDeg, localPoint, label,
                                    fromFrame: false, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Put something at a coordinate nobody is standing at.
        ///
        /// No local point, because there is no local: the caller read a pair of
        /// numbers off a map, possibly from another city. That also means no
        /// frame is needed — this is a write, not a placement — so it works
        /// before the device has localized, or somewhere it never will.
        ///
        /// The ground offset is zero and the provider is "map". Zero means "on
        /// the ground, wherever the ground turns out to be", which is the only
        /// honest answer for the height of a point on a map; and the provider
        /// marks it as a seed, accurate to whatever the imagery was worth, for
        /// somebody standing in front of the real thing to correct.
        /// </summary>
        public async Task<Placement> SeedAsync(string scene, GeoPoint position,
            double headingDeg = 0, string label = "", CancellationToken cancel = default)
        {
            var placement = new Placement
            {
                Scene = scene,
                Position = position,
                Orientation = Geodesy.HeadingToQuaternion(headingDeg),
                GroundOffset = 0,
                Label = label ?? "",
                Author = Author ?? "",
                Scale = 1,
                Fix = new FixQuality { Provider = "map", PositionM = 0, HeadingDeg = 0 }
            };

            IReadOnlyList<string> problems = placement.Problems();
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(
                    "refusing to place: " + string.Join(", ", problems));
            }

            Placement saved = await _store.PlaceAsync(placement, cancel).ConfigureAwait(false);
            _placements.Add(saved);

            // Only draws if this session happens to be near it and located.
            // Dropping a pin on somewhere far away is a perfectly good thing to
            // do and produces nothing to look at.
            if (Frame != null) { Reproject(); }

            return saved;
        }

        public async Task<Placement> PlaceAsync(string scene, Vec3 localPoint, double localYawRad,
            string label = "", CancellationToken cancel = default)
        {
            if (Frame == null) { throw new InvalidOperationException("not localized yet"); }

            return await WriteAsync(scene, Frame.ToGlobal(localPoint),
                                    Frame.LocalYawToHeading(localYawRad), localPoint, label,
                                    fromFrame: true, cancel).ConfigureAwait(false);
        }

        private async Task<Placement> WriteAsync(string scene, GeoPoint position,
            double headingDeg, Vec3 localPoint, string label, bool fromFrame,
            CancellationToken cancel)
        {
            if (Frame == null) { throw new InvalidOperationException("not localized yet"); }

            var placement = new Placement
            {
                Scene = scene,
                Position = position,
                Orientation = Geodesy.HeadingToQuaternion(headingDeg),
                GroundOffset = localPoint.Y - Floor(),
                Label = label ?? "",
                Author = Author ?? "",
                Scale = 1,
                Fix = new FixQuality
                {
                    Provider = Frame.Fix.Provider,
                    PositionM = Frame.Fix.PositionAccuracyM,
                    HeadingDeg = Frame.Fix.HeadingAccuracyDeg
                }
            };

            // Checked here rather than discovered at the server. Problems()
            // has existed since the beginning and was never called, so an
            // out-of-range placement was sent, refused, and reported as
            // whatever the server chose to say about it.
            IReadOnlyList<string> problems = placement.Problems();
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(
                    "refusing to place: " + string.Join(", ", problems));
            }

            Placement saved = await _store.PlaceAsync(placement, cancel).ConfigureAwait(false);
            saved.DistanceM = 0;
            _placements.Add(saved);

            // Keep the local point. When the frame improves this is what lets
            // the saved coordinates improve with it rather than keeping the
            // error they were written with.
            if (fromFrame)
            {
                _mine.Add((saved.Id, localPoint, Frame.HeadingToLocalYaw(headingDeg), position));
            }

            Reproject();
            return saved;
        }

        /// <summary>Re-derive what this session placed, now that the mapping is
        /// better. Placing on arrival used to bake the first fix's error into
        /// the record permanently: the object drifted as the frame converged
        /// and settled at whatever the wrong coordinates meant.</summary>
        private async Task CorrectMineAsync(CancellationToken cancel)
        {
            if (Frame == null || _mine.Count == 0) { return; }

            for (int i = 0; i < _mine.Count; i++)
            {
                (string id, Vec3 local, double yawRad, GeoPoint at) = _mine[i];

                GeoPoint position = Frame.ToGlobal(local);
                // Below this it is not worth a write, and the estimate wobbles
                // by this much anyway.
                if (Geodesy.Haversine(position, at) < 0.5) { continue; }

                _mine[i] = (id, local, yawRad, position);
                double headingDeg = Frame.LocalYawToHeading(yawRad);
                double offset = local.Y - Floor();

                try
                {
                    await _store.MoveAsync(id, position, headingDeg, offset, false, cancel)
                                .ConfigureAwait(false);

                    Placement stored = _placements.FirstOrDefault(p => p.Id == id);
                    if (stored != null)
                    {
                        stored.Position = position;
                        stored.GroundOffset = offset;
                    }
                }
                catch { /* it will be tried again on the next fix */ }
            }

            Reproject();
        }

        /// <summary>
        /// Write better coordinates for something already placed.
        ///
        /// For a caller that can re-derive the position of a placement it made
        /// — the same physical spot, converted again once the device knows more
        /// about where it is. Distinct from the frame's own correction pass,
        /// which re-derives from the frame; this takes an answer from outside.
        /// </summary>
        public async Task RepositionAsync(string id, GeoPoint position, double headingDeg,
            double groundOffset, bool claim = false, CancellationToken cancel = default)
        {
            await _store.MoveAsync(id, position, headingDeg, groundOffset, claim, cancel)
                        .ConfigureAwait(false);

            Placement stored = _placements.FirstOrDefault(p => p.Id == id);
            if (stored != null)
            {
                stored.Position = position;
                stored.Orientation = Geodesy.HeadingToQuaternion(headingDeg);
                stored.GroundOffset = groundOffset;
            }

            Reproject();
        }

        public async Task RemoveAsync(string id, CancellationToken cancel = default)
        {
            await _store.RemoveAsync(id, cancel).ConfigureAwait(false);
            _placements.RemoveAll(p => p.Id == id);
            _mine.RemoveAll(m => m.Id == id);
            Reproject();
        }

        public void Reset()
        {
            _samples.Clear();
            _placements.Clear();
            _mine.Clear();
            _heading = null;
            _headingSource = "none";
            _fetchedAt = null;
            _lastFixLocal = null;
            Frame = null;
            Emit(SessionState.Idle, null);
        }

        private void Emit(SessionState next, string detail)
        {
            State = next;
            LastError = next == SessionState.Error ? detail : null;
            StateChanged?.Invoke(next, detail);
        }
    }
}
