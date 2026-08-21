using System;

namespace MarkerOne.Core
{
    /// <summary>What a localization provider reports: where the device is, in
    /// the frame the content is stored in.</summary>
    public sealed class Fix
    {
        public GeoPoint Position;
        public double HeadingDeg;          // clockwise from north
        public double PositionAccuracyM;
        public double HeadingAccuracyDeg;
        public string Provider = "unknown";
        public string HeadingFrom = "unknown";

        /// <summary>Not decoration. A GPS fix and a visual fix are the same
        /// shape and nothing like the same thing, and everything downstream —
        /// what gets written, what the interface admits to — depends on being
        /// able to tell which one arrived.</summary>
        public override string ToString() =>
            $"±{PositionAccuracyM:F1}m ±{HeadingAccuracyDeg:F0}° via {Provider}/{HeadingFrom}";
    }

    /// <summary>Where the device is in the session's own frame, paired with the
    /// fix taken at that moment. A position on the globe is only half of a
    /// bearing.</summary>
    public readonly struct SessionPose
    {
        public readonly Vec3 Position;
        public readonly double YawDeg;

        public SessionPose(Vec3 position, double yawDeg = 0)
        {
            Position = position;
            YawDeg = yawDeg;
        }

        public static SessionPose Origin => new SessionPose(new Vec3(0, 0, 0), 0);
    }

    /// <summary>
    /// The seam the whole thing turns on.
    ///
    /// Content lives in one global frame. An AR session renders in its own local
    /// frame, in metres, whose origin is wherever the session happened to start
    /// and whose yaw is arbitrary. A provider's only job is to report, once,
    /// where the device is in the global frame. From that one observation the
    /// transform between the two frames falls out, and the platform's own
    /// tracking carries it from there.
    ///
    /// That "once" matters. Nothing here needs continuous global positioning,
    /// which is fortunate, because none of the accurate ways of getting it are
    /// cheap enough to run every frame.
    /// </summary>
    public sealed class LocalizationFrame
    {
        public GeoPoint Origin { get; }
        public double YawRad { get; }
        public Fix Fix { get; }

        private readonly Vec3 _offset;
        private readonly double _cos;
        private readonly double _sin;
        private readonly double _sessionYawDeg;

        public LocalizationFrame(Fix fix, SessionPose local)
        {
            Fix = fix ?? throw new ArgumentNullException(nameof(fix));
            Origin = fix.Position;
            _offset = local.Position;
            _sessionYawDeg = local.YawDeg;

            // Everything reduces to this one angle: the session's yaw offset.
            YawRad = Geodesy.HeadingToYaw(fix.HeadingDeg - local.YawDeg);
            _cos = Math.Cos(YawRad);
            _sin = Math.Sin(YawRad);
        }

        public LocalizationFrame(Fix fix) : this(fix, SessionPose.Origin) { }

        /// <summary>Global to the session's local metres.</summary>
        public Vec3 ToLocal(GeoPoint position)
        {
            Vec3 v = Geodesy.EnuToRender(Geodesy.ToEnu(position, Origin));

            return new Vec3(
                _offset.X + v.X * _cos - v.Z * _sin,
                _offset.Y + v.Y,
                _offset.Z + v.X * _sin + v.Z * _cos);
        }

        /// <summary>...and back, for placing something where the user stands.</summary>
        public GeoPoint ToGlobal(Vec3 v)
        {
            double dx = v.X - _offset.X;
            double dy = v.Y - _offset.Y;
            double dz = v.Z - _offset.Z;

            Enu enu = Geodesy.RenderToEnu(new Vec3(
                dx * _cos + dz * _sin,
                dy,
                -dx * _sin + dz * _cos));

            return Geodesy.FromEnu(enu, Origin);
        }

        /// <summary>A compass heading in the session's frame, for orienting
        /// content authored to face a particular way in the world.</summary>
        public double HeadingToLocalYaw(double headingDeg) =>
            Geodesy.HeadingToYaw(headingDeg - _sessionYawDeg);

        /// <summary>The inverse, for recording which way something was facing
        /// when the user put it down.</summary>
        public double LocalYawToHeading(double yawRad) =>
            ((-yawRad * 180.0 / Math.PI) + _sessionYawDeg + 360) % 360;
    }
}
