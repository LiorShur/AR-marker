using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MarkerOne.Core;

/// <summary>
/// The port, checked against the implementation it was ported from.
///
/// spatial/geo.js has a hundred-odd assertions behind it and two corrected sign
/// errors in its history — a mirrored render frame and a quaternion facing
/// backwards. Neither announced itself as anything other than the world being
/// subtly wrong, and one of them survived a round trip test because a mirrored
/// transform is its own consistent inverse.
///
/// So this does not test the C# against its author's intentions. It tests it
/// against the same inputs and the same outputs, produced by code that is known
/// to work. Regenerate the vectors with `node scripts/make-vectors.mjs`.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    private static int Main(string[] args)
    {
        string path = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "vectors", "core.json");

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"no vectors at {Path.GetFullPath(path)}");
            Console.Error.WriteLine("run: node scripts/make-vectors.mjs");
            return 2;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;

        Section("ellipsoid", () => Ecef(root.GetProperty("ecef")));
        Section("tangent plane", () => EnuCases(root.GetProperty("enu")));
        Section("render frame", () => Render(root.GetProperty("render")));
        Section("heading", () => Heading(root.GetProperty("heading")));
        Section("orientation", () => Quaternion(root.GetProperty("quaternion")));
        Section("distance", () => Distance(root.GetProperty("distance")));
        Section("geohash", () => Geohash(root.GetProperty("geohash")));
        Section("query bounds", () => Bounds(root.GetProperty("bounds")));
        Section("localization frame", () => Frame(root.GetProperty("frame")));
        Section("walked baseline", () => Walk(root.GetProperty("baseline")));

        Console.WriteLine();
        foreach (string failure in Failures) { Console.WriteLine("  ✗ " + failure); }

        int total = _passed + Failures.Count;
        Console.WriteLine($"\n  {_passed}/{total} vectors matched\n");
        return Failures.Count == 0 ? 0 : 1;
    }

    // ── sections ─────────────────────────────────────────────

    private static void Ecef(JsonElement cases)
    {
        foreach (JsonElement c in cases.EnumerateArray())
        {
            GeoPoint input = Point(c.GetProperty("input"));
            Ecef ecef = Geodesy.ToEcef(input);
            JsonElement want = c.GetProperty("ecef");

            // Metre-scale coordinates: a nanometre is far tighter than anything
            // that could matter and still catches a formula that differs.
            Near("ecef.x", ecef.X, want.GetProperty("x").GetDouble(), 1e-6);
            Near("ecef.y", ecef.Y, want.GetProperty("y").GetDouble(), 1e-6);
            Near("ecef.z", ecef.Z, want.GetProperty("z").GetDouble(), 1e-6);

            GeoPoint back = Geodesy.FromEcef(ecef);
            JsonElement wantBack = c.GetProperty("roundTrip");
            Near("ecef round trip lat", back.Lat, wantBack.GetProperty("lat").GetDouble(), 1e-9);
            Near("ecef round trip lon", back.Lon, wantBack.GetProperty("lon").GetDouble(), 1e-9);
            Near("ecef round trip h", back.Height, wantBack.GetProperty("h").GetDouble(), 1e-6);
        }
    }

    private static void EnuCases(JsonElement cases)
    {
        foreach (JsonElement c in cases.EnumerateArray())
        {
            GeoPoint origin = Point(c.GetProperty("origin"));
            GeoPoint target = Point(c.GetProperty("target"));

            Enu enu = Geodesy.ToEnu(target, origin);
            JsonElement want = c.GetProperty("enu");
            Near("enu.e", enu.East, want.GetProperty("e").GetDouble(), 1e-6);
            Near("enu.n", enu.North, want.GetProperty("n").GetDouble(), 1e-6);
            Near("enu.u", enu.Up, want.GetProperty("u").GetDouble(), 1e-6);

            GeoPoint back = Geodesy.FromEnu(enu, origin);
            JsonElement wantBack = c.GetProperty("back");
            Near("enu back lat", back.Lat, wantBack.GetProperty("lat").GetDouble(), 1e-9);
            Near("enu back lon", back.Lon, wantBack.GetProperty("lon").GetDouble(), 1e-9);
        }
    }

    private static void Render(JsonElement cases)
    {
        foreach (JsonElement c in cases.EnumerateArray())
        {
            JsonElement e = c.GetProperty("enu");
            var enu = new Enu(
                e.GetProperty("e").GetDouble(),
                e.GetProperty("n").GetDouble(),
                e.GetProperty("u").GetDouble());

            Vec3 v = Geodesy.EnuToRender(enu);
            JsonElement want = c.GetProperty("three");
            Near("render.x", v.X, want.GetProperty("x").GetDouble(), 1e-9);
            Near("render.y", v.Y, want.GetProperty("y").GetDouble(), 1e-9);
            Near("render.z", v.Z, want.GetProperty("z").GetDouble(), 1e-9);

            Enu back = Geodesy.RenderToEnu(v);
            Near("render round trip", back.North, enu.North, 1e-9);
        }
    }

    private static void Heading(JsonElement cases)
    {
        foreach (JsonElement c in cases.EnumerateArray())
        {
            double deg = c.GetProperty("headingDeg").GetDouble();
            Near($"yaw of {deg}", Geodesy.HeadingToYaw(deg),
                c.GetProperty("yawRad").GetDouble(), 1e-9);
        }
    }

    private static void Quaternion(JsonElement cases)
    {
        foreach (JsonElement c in cases.EnumerateArray())
        {
            double deg = c.GetProperty("headingDeg").GetDouble();
            Quat q = Geodesy.HeadingToQuaternion(deg);
            JsonElement want = c.GetProperty("quaternion");

            Near($"q.x at {deg}", q.X, want.GetProperty("x").GetDouble(), 1e-9);
            Near($"q.y at {deg}", q.Y, want.GetProperty("y").GetDouble(), 1e-9);
            Near($"q.z at {deg}", q.Z, want.GetProperty("z").GetDouble(), 1e-9);
            Near($"q.w at {deg}", q.W, want.GetProperty("w").GetDouble(), 1e-9);

            Near($"heading back from {deg}", Geodesy.HeadingFromQuaternion(q),
                c.GetProperty("back").GetDouble(), 1e-6);
        }
    }

    private static void Distance(JsonElement cases)
    {
        foreach (JsonElement c in cases.EnumerateArray())
        {
            GeoPoint a = Point(c.GetProperty("a"));
            GeoPoint b = Point(c.GetProperty("b"));
            Near("haversine", Geodesy.Haversine(a, b),
                c.GetProperty("metres").GetDouble(), 1e-6);
        }
    }

    private static void Geohash(JsonElement cases)
    {
        foreach (JsonElement c in cases.EnumerateArray())
        {
            string got = Geodesy.Geohash(
                c.GetProperty("lat").GetDouble(),
                c.GetProperty("lon").GetDouble(),
                c.GetProperty("precision").GetInt32());
            Same("geohash", got, c.GetProperty("hash").GetString());
        }
    }

    private static void Bounds(JsonElement cases)
    {
        foreach (JsonElement c in cases.EnumerateArray())
        {
            double radius = c.GetProperty("radiusM").GetDouble();
            Same($"precision at {radius}m",
                Geodesy.GeohashPrecisionFor(radius).ToString(CultureInfo.InvariantCulture),
                c.GetProperty("precision").GetInt32().ToString(CultureInfo.InvariantCulture));

            var got = Geodesy.GeohashQueryBounds(
                c.GetProperty("lat").GetDouble(),
                c.GetProperty("lon").GetDouble(),
                radius);

            var want = c.GetProperty("ranges").EnumerateArray()
                .Select(r => (r[0].GetString(), r[1].GetString()))
                .ToList();

            // Order is not part of the contract; coverage is.
            Same($"range count at {radius}m", got.Count.ToString(), want.Count.ToString());
            foreach ((string start, string end) in want)
            {
                Truthy($"range {start} present at {radius}m",
                    got.Any(g => g.Start == start && g.End == end));
            }
        }
    }

    private static void Frame(JsonElement cases)
    {
        foreach (JsonElement c in cases.EnumerateArray())
        {
            GeoPoint origin = Point(c.GetProperty("origin"));
            double headingDeg = c.GetProperty("headingDeg").GetDouble();

            // The offset is recovered from what the JS reported for the origin
            // itself: toLocal(origin) is exactly the local offset.
            JsonElement off = c.GetProperty("offset");
            var offset = new Vec3(
                off.GetProperty("x").GetDouble(),
                off.GetProperty("y").GetDouble(),
                off.GetProperty("z").GetDouble());

            var frame = new LocalizationFrame(
                new Fix { Position = origin, HeadingDeg = headingDeg },
                new SessionPose(offset, 0));

            foreach (JsonElement s in c.GetProperty("samples").EnumerateArray())
            {
                GeoPoint target = Point(s.GetProperty("target"));
                Vec3 local = frame.ToLocal(target);
                JsonElement want = s.GetProperty("local");

                // Millimetres. Anything looser would hide a transform that is
                // subtly rotated rather than plainly wrong.
                Near($"frame {headingDeg}° local.x", local.X, want.GetProperty("x").GetDouble(), 1e-6);
                Near($"frame {headingDeg}° local.y", local.Y, want.GetProperty("y").GetDouble(), 1e-6);
                Near($"frame {headingDeg}° local.z", local.Z, want.GetProperty("z").GetDouble(), 1e-6);

                GeoPoint back = frame.ToGlobal(local);
                JsonElement wantBack = s.GetProperty("back");
                Near("frame back lat", back.Lat, wantBack.GetProperty("lat").GetDouble(), 1e-9);
                Near("frame back lon", back.Lon, wantBack.GetProperty("lon").GetDouble(), 1e-9);
            }
        }
    }

    private static void Walk(JsonElement cases)
    {
        foreach (JsonElement c in cases.EnumerateArray())
        {
            string name = c.GetProperty("name").GetString();
            BaselineSample a = Sample(c.GetProperty("a"));
            BaselineSample b = Sample(c.GetProperty("b"));

            BaselineHeading got = Baseline.FromWalk(a, b);
            JsonElement want = c.GetProperty("result");

            if (want.ValueKind == JsonValueKind.Null)
            {
                // The refusals matter as much as the answers: a bearing from a
                // walk that did not happen is worse than no bearing.
                Truthy($"refused: {name}", got == null);
                continue;
            }

            if (got == null)
            {
                Fail($"{name}: refused where the reference did not");
                continue;
            }

            Near($"{name} heading", got.HeadingDeg,
                want.GetProperty("headingDeg").GetDouble(), 1e-6);
            Near($"{name} session yaw", got.SessionYawDeg ?? -1,
                want.GetProperty("sessionYawDeg").GetDouble(), 1e-6);
            Near($"{name} separation", got.SeparationM,
                want.GetProperty("separationM").GetDouble(), 1e-6);
            Near($"{name} accuracy", got.AccuracyDeg,
                want.GetProperty("accuracyDeg").GetDouble(), 1e-6);
        }
    }

    // ── plumbing ─────────────────────────────────────────────

    private static GeoPoint Point(JsonElement e) => new GeoPoint(
        e.GetProperty("lat").GetDouble(),
        e.GetProperty("lon").GetDouble(),
        e.TryGetProperty("h", out JsonElement h) ? h.GetDouble() : 0);

    private static BaselineSample Sample(JsonElement e)
    {
        JsonElement local = e.GetProperty("local");
        return new BaselineSample
        {
            Position = Point(e.GetProperty("position")),
            AccuracyM = e.GetProperty("accuracy").GetProperty("positionM").GetDouble(),
            Local = new Vec3(
                local.GetProperty("x").GetDouble(),
                local.GetProperty("y").GetDouble(),
                local.GetProperty("z").GetDouble())
        };
    }

    private static void Section(string name, Action run)
    {
        Console.WriteLine($"\n  {name}");
        int before = Failures.Count;
        int passedBefore = _passed;
        run();
        int checks = (_passed - passedBefore) + (Failures.Count - before);
        Console.WriteLine($"    {checks - (Failures.Count - before)}/{checks} matched");
    }

    private static void Near(string what, double got, double want, double tolerance)
    {
        if (Math.Abs(got - want) <= tolerance) { _passed++; return; }
        Fail($"{what}: {got:R} vs {want:R} (off by {Math.Abs(got - want):E2})");
    }

    private static void Same(string what, string got, string want)
    {
        if (got == want) { _passed++; return; }
        Fail($"{what}: '{got}' vs '{want}'");
    }

    private static void Truthy(string what, bool ok)
    {
        if (ok) { _passed++; return; }
        Fail(what);
    }

    private static void Fail(string message) => Failures.Add(message);
}
