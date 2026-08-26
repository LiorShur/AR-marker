using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarkerOne.Core;

namespace MarkerOne.Conformance
{
    internal sealed class FakeStore : IPlacementStore
    {
        public string Uid => "anon-1";
        public List<Placement> Contents = new();
        public readonly List<Placement> Written = new();
        public readonly List<(string Id, GeoPoint Position)> Moved = new();
        public Func<Task> OnNearby;

        public async Task<IReadOnlyList<Placement>> NearbyAsync(
            double lat, double lon, double radiusM, CancellationToken cancel = default)
        {
            if (OnNearby != null) { await OnNearby().ConfigureAwait(false); }
            return Contents.ToList();
        }

        public Task<Placement> PlaceAsync(Placement p, CancellationToken cancel = default)
        {
            p.Id = "p" + (Written.Count + 1);
            p.Owner = Uid;
            Written.Add(p);
            return Task.FromResult(p);
        }

        public Task MoveAsync(string id, GeoPoint position, double headingDeg,
            double groundOffset, bool claim = false, CancellationToken cancel = default)
        {
            Moved.Add((id, position));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string id, CancellationToken cancel = default)
        {
            Contents.RemoveAll(p => p.Id == id);
            return Task.CompletedTask;
        }
    }

    internal static class SessionTests
    {
        private const double BaseLat = 51.5007;
        private const double BaseLon = -0.1246;

        private static GeoPoint North(double m) =>
            new GeoPoint(BaseLat + (m / 6371008.8) * 180 / Math.PI, BaseLon, 0);

        private static Fix Gps(GeoPoint at, double accuracy = 5) => new Fix
        {
            Position = at,
            PositionAccuracyM = accuracy,
            HeadingAccuracyDeg = 90,
            Provider = "gps"
        };

        public static async Task Run(Action<string, bool, string> check)
        {
            // One fix is a position. Two, far enough apart, are a position and
            // a bearing. Everything waits on the second.
            {
                var store = new FakeStore();
                var session = new WorldSession(store);

                await session.AddFixAsync(Gps(North(0)), new Vec3(0, 0, 0));
                check("one fix is not enough to know which way north is",
                    session.State == SessionState.Calibrating, session.State.ToString());

                await session.AddFixAsync(Gps(North(30)), new Vec3(0, 0, -30));
                check("a second fix far enough away resolves it",
                    session.State == SessionState.Ready, session.State.ToString());
                check("the session's forward axis is found to be north",
                    Math.Abs(session.Frame.HeadingToLocalYaw(0)) < 0.01,
                    (session.Frame.HeadingToLocalYaw(0) * 180 / Math.PI).ToString("F2") + "°");
                check("heading accuracy reflects the walk",
                    session.Frame.Fix.HeadingAccuracyDeg > 0 &&
                    session.Frame.Fix.HeadingAccuracyDeg < 10,
                    "±" + session.Frame.Fix.HeadingAccuracyDeg.ToString("F1") + "°");
            }

            // Indoors, or standing still, a baseline can never resolve: the
            // position error is tens of metres and the baseline has to beat it.
            {
                var session = new WorldSession(new FakeStore())
                {
                    CompassHeadingDeg = 90,
                    CompassSpreadDeg = 25
                };

                await session.AddFixAsync(Gps(North(0), 30), new Vec3(0, 0, 0));
                check("a compass gets a stationary session going",
                    session.State == SessionState.Ready, session.State.ToString());
                check("and the bearing is labelled as the compass",
                    session.Frame.Fix.HeadingFrom == "compass" &&
                    Math.Abs(session.Frame.Fix.HeadingAccuracyDeg - 25) < 1e-9,
                    session.Frame.Fix.ToString());

                // ...and a walk, once it happens, must take over. Two good
                // fixes are needed, not one: a forty metre walk cannot beat the
                // thirty metre fix taken indoors, and Baseline is right to
                // refuse that pair. Stepping outside and walking on gives a
                // pair that both claim three metres.
                await session.AddFixAsync(Gps(North(40), 3), new Vec3(0, 0, -40));
                check("one good fix after a bad one is still not a bearing",
                    session.Frame.Fix.HeadingFrom == "compass",
                    session.Frame.Fix.HeadingFrom);

                await session.AddFixAsync(Gps(North(80), 3), new Vec3(0, 0, -80));
                check("a walked baseline overrides the compass",
                    session.Frame.Fix.HeadingFrom == "baseline",
                    session.Frame.Fix.HeadingFrom);
                check("and is a far better bearing",
                    session.Frame.Fix.HeadingAccuracyDeg < 10,
                    "±" + session.Frame.Fix.HeadingAccuracyDeg.ToString("F1") + "°");
            }

            // Placements arrive in session coordinates.
            {
                var store = new FakeStore
                {
                    Contents = new List<Placement>
                    {
                        new Placement
                        {
                            Id = "a", Scene = "rotary-phone", Scale = 1, GroundOffset = 0.25,
                            Position = North(80),
                            Orientation = Geodesy.HeadingToQuaternion(90)
                        }
                    }
                };

                IReadOnlyList<PlacedItem> seen = Array.Empty<PlacedItem>();
                var session = new WorldSession(store);
                session.PlacementsChanged += list => seen = list;

                await session.AddFixAsync(Gps(North(0)), new Vec3(0, 0, 0));
                await session.AddFixAsync(Gps(North(30)), new Vec3(0, 0, -30));

                check("becoming located fetches what is nearby", seen.Count == 1, seen.Count + " items");
                check("a placement to the north is ahead of the walker",
                    Math.Abs(seen[0].Local.Z + 80) < 1 && Math.Abs(seen[0].Local.X) < 1,
                    $"x={seen[0].Local.X:F1} z={seen[0].Local.Z:F1}");
                check("its facing is carried into the session frame",
                    Math.Abs(seen[0].YawRad + Math.PI / 2) < 0.01,
                    (seen[0].YawRad * 180 / Math.PI).ToString("F1") + "°");
                check("the label and time travel with it",
                    seen[0].Scene == "rotary-phone", seen[0].Scene);

                // Placing goes the other way, and records how it was localized.
                Placement wrote = await session.PlaceAsync("beacon", new Vec3(0, 1.4, -50), 0, "Lior");
                check("placing converts session coordinates back to the globe",
                    Math.Abs(wrote.Position.Lat - North(50).Lat) < 1e-5,
                    $"{wrote.Position.Lat:F6} vs {North(50).Lat:F6}");
                check("height above the floor is what is stored",
                    Math.Abs(wrote.GroundOffset - 1.4) < 1e-9, wrote.GroundOffset.ToString("R"));
                check("and how it was localized", wrote.Fix.Provider == "gps" &&
                    wrote.Fix.PositionM > 0, wrote.Fix.ToString());
                check("the name is carried through", wrote.Label == "Lior", wrote.Label);
            }

            // A bad altitude must not bury a placement, and the floor is the
            // one the session actually saw.
            {
                double floor = 0;
                var store = new FakeStore
                {
                    Contents = new List<Placement>
                    {
                        new Placement
                        {
                            Id = "b", Scene = "beacon", Scale = 1, GroundOffset = 0.5,
                            Position = new GeoPoint(North(30).Lat, BaseLon, -40),
                            Orientation = Quat.Identity
                        }
                    }
                };

                var session = new WorldSession(store, () => floor);
                await session.AddFixAsync(Gps(North(0)), new Vec3(0, 0, 0));
                await session.AddFixAsync(Gps(North(30)), new Vec3(0, 0, -30));

                double atZero = session.Reproject()[0].Local.Y;
                floor = -1.4;
                double shifted = session.Reproject()[0].Local.Y;

                check("a bad altitude cannot bury a placement",
                    Math.Abs(atZero - 0.5) < 1e-9, atZero.ToString("R"));
                check("and height follows the floor that was seen, not the globe",
                    Math.Abs(shifted + 0.9) < 1e-9, shifted.ToString("R"));
            }

            // Placing early used to bake the frame's error in permanently.
            {
                var store = new FakeStore();
                var session = new WorldSession(store) { CompassHeadingDeg = 0 };

                await session.AddFixAsync(Gps(North(0), 6), new Vec3(0, 0, 0));
                await session.PlaceAsync("beacon", new Vec3(0, 0, -5), 0);
                int before = store.Moved.Count;

                for (int i = 1; i <= 3; i++)
                {
                    await session.AddFixAsync(Gps(North(i * 14), 6), new Vec3(0, 0, 0));
                }

                check("a placement made early is rewritten as the frame improves",
                    store.Moved.Count > before, store.Moved.Count + " corrections");
                check("and it is the same placement being corrected",
                    store.Moved.All(m => m.Id == "p1"), "");
            }

            // Refusals.
            {
                var cold = new WorldSession(new FakeStore());
                try
                {
                    await cold.PlaceAsync("x", new Vec3(0, 0, 0), 0);
                    check("refuses to place before localizing", false, "no error raised");
                }
                catch (InvalidOperationException e)
                {
                    check("refuses to place before localizing",
                        e.Message.Contains("not localized"), e.Message);
                }

                var still = new WorldSession(new FakeStore()) { CompassHeadingDeg = 0 };
                await still.AddFixAsync(Gps(North(0)), new Vec3(0, 0, 0));

                check("no relocalization needed while standing still",
                    !still.NeedsRelocalize(new Vec3(0, 0, 0)), "");
                check("relocalization is needed after walking far enough",
                    still.NeedsRelocalize(new Vec3(0, 0, -100)), "");
            }

            // A geospatial provider knows the bearing outright, so the walk is
            // unnecessary — one fix and the session is ready, to a degree.
            {
                var session = new WorldSession(new FakeStore())
                {
                    CompassHeadingDeg = 90,
                    CompassSpreadDeg = 25
                };

                await session.AddFixAsync(new Fix
                {
                    Position = North(0),
                    PositionAccuracyM = 0.8,
                    Provider = "geospatial",
                    SessionYawDeg = 17,
                    SessionYawAccuracyDeg = 1.2
                }, new Vec3(0, 0, 0));

                check("a direct bearing needs no walk at all",
                    session.State == SessionState.Ready, session.State.ToString());
                check("and beats the compass sitting beside it",
                    session.Frame.Fix.HeadingFrom == "direct" &&
                    Math.Abs(session.Frame.Fix.HeadingAccuracyDeg - 1.2) < 1e-9,
                    session.Frame.Fix.ToString());

                // ...but a good walk still wins if it is genuinely better.
                for (int i = 1; i <= 3; i++)
                {
                    await session.AddFixAsync(new Fix
                    {
                        Position = North(i * 60),
                        PositionAccuracyM = 0.5,
                        Provider = "geospatial",
                        SessionYawDeg = 17,
                        SessionYawAccuracyDeg = 1.2
                    }, new Vec3(0, 0, -i * 60));
                }
                check("a long walk on good fixes beats even that",
                    session.Frame.Fix.HeadingFrom == "baseline" &&
                    session.Frame.Fix.HeadingAccuracyDeg < 1.2,
                    "±" + session.Frame.Fix.HeadingAccuracyDeg.ToString("F2") + "° via " +
                    session.Frame.Fix.HeadingFrom);
            }

            // A read that fails is not an empty world.
            {
                var store = new FakeStore
                {
                    OnNearby = () => throw new InvalidOperationException("permission denied")
                };
                var states = new List<(SessionState, string)>();
                var session = new WorldSession(store) { CompassHeadingDeg = 0 };
                session.StateChanged += (s, d) => states.Add((s, d));

                await session.AddFixAsync(Gps(North(0)), new Vec3(0, 0, 0));

                check("a refused read is reported rather than shown as empty",
                    states.Any(s => s.Item1 == SessionState.Error &&
                                    s.Item2 != null && s.Item2.Contains("Could not read")),
                    string.Join(" -> ", states.Select(s => s.Item1)));
            }
        }
    }
}
