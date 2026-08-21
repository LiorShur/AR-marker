using System;
using System.Collections.Generic;

namespace MarkerOne.Core
{
    /// <summary>
    /// Where the session's origin is, according to every fix rather than the
    /// last one.
    ///
    /// Using only the latest means a session inherits that one reading's error
    /// whole, and the next visit inherits a different one — which is what
    /// placements shifting several metres between sessions actually is, twice.
    ///
    /// Each sample says: the device was at global position P when it was at
    /// local position L. Given the session's yaw, that pins the origin at
    /// P - R⁻¹L. Averaging those, weighted by how good each fix claimed to be,
    /// is correct whether the user stood still or walked.
    /// </summary>
    public static class OriginEstimator
    {
        public static GeoPoint? Estimate(IReadOnlyList<BaselineSample> samples, double sessionYawDeg)
        {
            if (samples == null || samples.Count == 0) { return null; }
            if (samples.Count == 1) { return samples[0].Position; }

            GeoPoint reference = samples[0].Position;
            double yaw = Geodesy.HeadingToYaw(sessionYawDeg);
            double cos = Math.Cos(yaw);
            double sin = Math.Sin(yaw);

            double sumX = 0, sumY = 0, sumZ = 0, sumWeight = 0;

            foreach (BaselineSample s in samples)
            {
                Vec3 here = Geodesy.EnuToRender(Geodesy.ToEnu(s.Position, reference));

                // R⁻¹ applied to the local offset: where the origin sits
                // relative to where the device was standing.
                double backX = s.Local.X * cos + s.Local.Z * sin;
                double backZ = -s.Local.X * sin + s.Local.Z * cos;

                // A fix admitting to fifty metres should not weigh the same as
                // one claiming three.
                double sigma = Math.Max(1, s.AccuracyM <= 0 ? 30 : s.AccuracyM);
                double weight = 1 / (sigma * sigma);

                sumX += (here.X - backX) * weight;
                sumY += (here.Y - s.Local.Y) * weight;
                sumZ += (here.Z - backZ) * weight;
                sumWeight += weight;
            }

            Enu enu = Geodesy.RenderToEnu(
                new Vec3(sumX / sumWeight, sumY / sumWeight, sumZ / sumWeight));

            return Geodesy.FromEnu(enu, reference);
        }

        /// <summary>
        /// What the averaging actually bought.
        ///
        /// The textbook answer is sigma over root n, and it is wrong here: GPS
        /// error is strongly correlated minute to minute — the same satellites,
        /// the same atmosphere, the same reflections off the same wall — so the
        /// samples are nothing like independent. The floor at half the best
        /// single fix is a deliberate refusal to claim the improvement the
        /// arithmetic offers.
        /// </summary>
        public static double Accuracy(IReadOnlyList<BaselineSample> samples)
        {
            if (samples == null || samples.Count == 0) { return 30; }

            double best = double.PositiveInfinity;
            double sumWeight = 0;

            foreach (BaselineSample s in samples)
            {
                double sigma = Math.Max(1, s.AccuracyM <= 0 ? 30 : s.AccuracyM);
                best = Math.Min(best, sigma);
                sumWeight += 1 / (sigma * sigma);
            }

            return Math.Max(best * 0.5, 1 / Math.Sqrt(sumWeight));
        }
    }
}
