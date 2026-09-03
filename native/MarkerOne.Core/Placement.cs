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

    /// <summary>Where something sits in its parent's frame: metres right, up
    /// and forward of the parent, and how it is turned relative to it.</summary>
    public sealed class Attachment
    {
        public double X;
        public double Y;
        public double Z;
        public Quat Rotation = Quat.Identity;
    }

    /// <summary>
    /// One thing left somewhere, and possibly a piece of something larger.
    ///
    /// Position means two things depending on Parent. For a root it is where
    /// the thing is, full stop. For a child it is a cache — where the thing was
    /// last computed to be — kept only so the child can still be indexed by
    /// geohash, still be found by a nearby query, and still be drawn roughly
    /// right when its parent is missing, deleted or not yet located. Whenever
    /// the parent is there, Offset wins and the cache is ignored.
    ///
    /// That the cache goes stale is deliberate. Moving somebody else's baseplate
    /// would otherwise mean writing to their children's documents, which the
    /// rules refuse and should refuse; a stale fallback that is never consulted
    /// costs nothing.
    /// </summary>
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

        /// <summary>
        /// What this hangs off, or null for something standing on its own.
        ///
        /// The reason anything can be built out of more than one piece. Two
        /// placements anchored separately are corrected separately, and drift
        /// apart by tens of centimetres — enough that a stack of bricks comes
        /// apart and a doorway stops lining up with its wall. A structure has
        /// to be one anchored thing with everything else measured from it.
        /// </summary>
        public string Parent;

        /// <summary>
        /// Where this sits in the parent's frame. Null for a root.
        ///
        /// This is the truth for anything with a parent; Position is kept
        /// beside it as a fallback rather than as a fact — see above.
        /// </summary>
        public Attachment Offset;

        public bool IsChild => !string.IsNullOrEmpty(Parent) && Offset != null;

        /// <summary>
        /// Which venue this belongs to, or null for something in the world.
        ///
        /// A venue is a room, a hall, or a building full of both, and the point
        /// of it is that nothing inside one has coordinates. Geospatial does not
        /// work indoors — no sky, no VPS imagery — so a venue is pinned by
        /// printed markers instead, and everything in it is measured from the
        /// venue's own origin rather than from the Earth.
        /// </summary>
        public string Venue;

        /// <summary>Where this sits in the venue's frame: metres east, up and
        /// north of the venue origin, and how it is turned. Null for anything
        /// that is not in a venue.</summary>
        public Attachment At;

        /// <summary>
        /// Which printed image pins this, for the handful of placements that
        /// are markers rather than things.
        ///
        /// A marker is stored the same way as everything else because it is the
        /// same thing: something at a known pose in the venue. The difference is
        /// only that this one is also findable by a camera, which is what makes
        /// every other pose in the venue reachable.
        /// </summary>
        public string Marker;

        public bool InVenue => !string.IsNullOrEmpty(Venue) && At != null;

        public bool IsMarker => InVenue && !string.IsNullOrEmpty(Marker);

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
            if (Parent != null && Parent.Length > 64) { bad.Add("parent id too long"); }
            if (Parent == Id && Id != null) { bad.Add("cannot hang off itself"); }

            if (Offset != null)
            {
                // A hundred metres from the thing it is attached to is not an
                // offset, it is a mistake with a parent field on it.
                if (double.IsNaN(Offset.X) || Math.Abs(Offset.X) > 100) { bad.Add("offset x"); }
                if (double.IsNaN(Offset.Y) || Math.Abs(Offset.Y) > 100) { bad.Add("offset y"); }
                if (double.IsNaN(Offset.Z) || Math.Abs(Offset.Z) > 100) { bad.Add("offset z"); }

                double local = Math.Sqrt(
                    Offset.Rotation.X * Offset.Rotation.X + Offset.Rotation.Y * Offset.Rotation.Y +
                    Offset.Rotation.Z * Offset.Rotation.Z + Offset.Rotation.W * Offset.Rotation.W);
                if (double.IsNaN(local) || Math.Abs(local - 1) > 0.01)
                {
                    bad.Add("offset rotation is not a unit");
                }

                if (string.IsNullOrEmpty(Parent)) { bad.Add("an offset needs a parent"); }
            }
            if (Math.Abs(GroundOffset) > 100) { bad.Add("ground offset"); }
            if (Venue != null && (Venue.Length == 0 || Venue.Length > 64)) { bad.Add("venue id"); }
            if (Marker != null && (Marker.Length == 0 || Marker.Length > 64)) { bad.Add("marker name"); }
            if (Marker != null && Venue == null) { bad.Add("a marker needs a venue"); }

            if (At != null)
            {
                if (string.IsNullOrEmpty(Venue)) { bad.Add("a venue pose needs a venue"); }

                // A kilometre is a campus, not a venue, and past that the
                // accumulated drift between markers is larger than the things
                // being placed.
                if (double.IsNaN(At.X) || Math.Abs(At.X) > 1000) { bad.Add("venue x"); }
                if (double.IsNaN(At.Y) || Math.Abs(At.Y) > 1000) { bad.Add("venue y"); }
                if (double.IsNaN(At.Z) || Math.Abs(At.Z) > 1000) { bad.Add("venue z"); }

                double turn = Math.Sqrt(
                    At.Rotation.X * At.Rotation.X + At.Rotation.Y * At.Rotation.Y +
                    At.Rotation.Z * At.Rotation.Z + At.Rotation.W * At.Rotation.W);
                if (double.IsNaN(turn) || Math.Abs(turn - 1) > 0.01)
                {
                    bad.Add("venue rotation is not a unit");
                }
            }

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
