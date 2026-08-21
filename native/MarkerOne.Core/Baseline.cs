using System;

namespace MarkerOne.Core
{
    /// <summary>One position paired with where the session thought the device
    /// was at that moment.</summary>
    public sealed class BaselineSample
    {
        public GeoPoint Position;
        public double AccuracyM = 30;
        public Vec3 Local;
        public long AtMs;
    }

    public sealed class BaselineHeading
    {
        public double HeadingDeg;
        public double? SessionYawDeg;
        public double SeparationM;
        public double AccuracyDeg;
    }

    /// <summary>
    /// Heading from a walked baseline.
    ///
    /// The cheapest real fix for the compass problem, and the one that works in
    /// open country where there is nothing to look at. Two positions a few
    /// metres apart give a bearing; the session's own tracking says which way
    /// that was in local terms, and the difference is the yaw offset — with no
    /// magnetometer anywhere in it.
    ///
    /// It also scales with whatever is feeding it. At five-metre GPS fixes over
    /// thirty metres this is about nine degrees. At two-centimetre RTK fixes
    /// over ten metres it is a tenth of one.
    /// </summary>
    public static class Baseline
    {
        public static BaselineHeading FromWalk(BaselineSample a, BaselineSample b)
        {
            if (a == null || b == null) { return null; }

            double separation = Geodesy.Haversine(a.Position, b.Position);

            // Too short a baseline and the position noise dominates the
            // bearing: at five metres apart, a two-metre error is twenty
            // degrees.
            double noise = Math.Max(a.AccuracyM, b.AccuracyM);
            if (separation < Math.Max(5, noise * 2)) { return null; }

            Enu enu = Geodesy.ToEnu(
                new GeoPoint(b.Position.Lat, b.Position.Lon, 0),
                new GeoPoint(a.Position.Lat, a.Position.Lon, 0));

            double worldBearing = (Math.Atan2(enu.East, enu.North) * 180 / Math.PI + 360) % 360;

            double dx = b.Local.X - a.Local.X;
            double dz = b.Local.Z - a.Local.Z;
            double travelled = Math.Sqrt(dx * dx + dz * dz);

            // The session has to have seen the same walk. If it did not —
            // tracking lost, or the device moved without the camera agreeing,
            // as in a vehicle — then the two bearings describe different
            // journeys and subtracting them produces a confident, meaningless
            // number.
            if (travelled < separation * 0.5 || travelled > separation * 2) { return null; }

            Enu local = Geodesy.RenderToEnu(new Vec3(dx, 0, dz));
            double localBearing = (Math.Atan2(local.East, local.North) * 180 / Math.PI + 360) % 360;

            return new BaselineHeading
            {
                HeadingDeg = worldBearing,
                SessionYawDeg = (worldBearing - localBearing + 360) % 360,
                SeparationM = separation,
                // Bearing error from position noise, small-angle.
                AccuracyDeg = Math.Atan2(noise, separation) * 180 / Math.PI
            };
        }
    }
}
