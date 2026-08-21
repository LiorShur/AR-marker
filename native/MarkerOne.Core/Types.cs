using System;

namespace MarkerOne.Core
{
    /// <summary>A position on the globe. Height is above the WGS84 ellipsoid,
    /// which is not height above sea level — the geoid separation is tens of
    /// metres in places. It matters the moment an elevation service is mixed
    /// in; it does not matter while placing and reading with one convention.
    /// </summary>
    public readonly struct GeoPoint
    {
        public readonly double Lat;
        public readonly double Lon;
        public readonly double Height;

        public GeoPoint(double lat, double lon, double height = 0)
        {
            Lat = lat;
            Lon = lon;
            Height = height;
        }

        public override string ToString() =>
            $"{Lat:F7}, {Lon:F7} @ {Height:F1}m";
    }

    /// <summary>Earth-centred, earth-fixed metres.</summary>
    public readonly struct Ecef
    {
        public readonly double X, Y, Z;
        public Ecef(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    /// <summary>Metres east, north and up from a local origin.</summary>
    public readonly struct Enu
    {
        public readonly double East, North, Up;
        public Enu(double east, double north, double up) { East = east; North = north; Up = up; }
    }

    /// <summary>A point in the render frame: Y up, right-handed, -Z forward.
    /// Double rather than float on purpose — a float loses centimetres a few
    /// hundred metres from the origin, which is the whole range this works in.
    /// Convert to UnityEngine.Vector3 at the boundary, not before.</summary>
    public readonly struct Vec3
    {
        public readonly double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
    }

    public readonly struct Quat
    {
        public readonly double X, Y, Z, W;
        public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
        public static Quat Identity => new Quat(0, 0, 0, 1);
    }
}
