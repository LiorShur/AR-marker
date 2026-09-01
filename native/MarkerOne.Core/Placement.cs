using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarkerOne.Core
{
    /// <summary>How a placement's pose was obtained, and how well. Recorded
    /// rather than inferred: a placement made by GPS and one made by a visual
    /// fix are the same shape and nothing like the same thing.</summary>
    public sealed class FixQuality
    {
        public string Provider = "unknown";
        public double PositionM;
        public double HeadingDeg;
    }

    public sealed class Placement
    {
        public string Id;
        public GeoPoint Position;
        public Quat Orientation = Quat.Identity;
        public string Scene;
        public double Scale = 1;

        /// <summary>Metres above the floor of the session it was placed in.
        /// Stored apart from the ellipsoidal height because GPS altitude is the
        /// least reliable number a receiver reports — two or three times worse
        /// than the horizontal fix, and sometimes simply absent. Reading
        /// vertical position back from it puts things underground or in the
        /// air, and either is invisible.</summary>
        public double GroundOffset;

        /// <summary>What the thing is called. "the old oak", not a person.</summary>
        public string Label = "";

        /// <summary>
        /// Who left it, as a name rather than as a uid.
        ///
        /// Owner is an identity and answers "may this be edited". This answers
        /// "who should be credited", which is a different question and cannot
        /// be derived from the first: a uid is meaningless to everybody except
        /// the person it belongs to, and looking one up would mean a directory
        /// of users that this does not have and does not want.
        ///
        /// So it is written down at the time, by the only party who knows it.
        /// Unverified, like the label — nothing stops somebody claiming to be
        /// anyone, and nothing here pretends otherwise.
        /// </summary>
        public string Author = "";
        public string Owner;
        public string CreatedAt;
        public string Visibility = "public";
        public FixQuality Fix = new FixQuality();

        /// <summary>Filled in by the query, not stored.</summary>
        public double DistanceM;

        public string Geohash => Geodesy.Geohash(Position.Lat, Position.Lon, 10);

        public IReadOnlyList<string> Problems()
        {
            var bad = new List<string>();

            if (double.IsNaN(Position.Lat) || Position.Lat < -90 || Position.Lat > 90) { bad.Add("latitude"); }
            if (double.IsNaN(Position.Lon) || Position.Lon < -180 || Position.Lon > 180) { bad.Add("longitude"); }
            if (double.IsNaN(Position.Height) || Math.Abs(Position.Height) > 20000) { bad.Add("height"); }
            if (string.IsNullOrEmpty(Scene)) { bad.Add("scene is required"); }
            if (Scene != null && Scene.Length > 64) { bad.Add("scene name too long"); }
            if (double.IsNaN(Scale) || Scale <= 0 || Scale > 1000) { bad.Add("scale"); }
            if (Author != null && Author.Length > 40) { bad.Add("author too long"); }
            if (Math.Abs(GroundOffset) > 100) { bad.Add("ground offset"); }

            double length = Math.Sqrt(
                Orientation.X * Orientation.X + Orientation.Y * Orientation.Y +
                Orientation.Z * Orientation.Z + Orientation.W * Orientation.W);
            if (double.IsNaN(length) || Math.Abs(length - 1) > 0.01) { bad.Add("quaternion is not a unit"); }

            return bad;
        }
    }

    public interface IPlacementStore
    {
        string Uid { get; }
        Task<IReadOnlyList<Placement>> NearbyAsync(double lat, double lon, double radiusM,
            CancellationToken cancel = default);
        Task<Placement> PlaceAsync(Placement placement, CancellationToken cancel = default);
        /// <summary>Move a placement. When claim is true the caller also takes
        /// ownership and marks the pose as no longer coming from a map, which
        /// is what correcting a map seed in the field amounts to.</summary>
        Task MoveAsync(string id, GeoPoint position, double headingDeg, double groundOffset,
            bool claim = false, CancellationToken cancel = default);
        Task RemoveAsync(string id, CancellationToken cancel = default);
    }
}
