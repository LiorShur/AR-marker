namespace MarkerOne.Unity
{
    /// <summary>
    /// Which of three quite different problems the app is solving right now.
    ///
    /// They were previously one mode with the differences buried: geospatial
    /// indoors reported a ±9 m fix and placed accordingly, and the only sign
    /// anything was wrong was that objects came back somewhere else. Naming the
    /// three makes the app say what it can do before it fails to do it.
    /// </summary>
    public static class AppMode
    {
        public enum Working
        {
            /// <summary>Anchored to the Earth. Half a metre in a city, worse in
            /// open country, nothing at all indoors.</summary>
            Outdoors,

            /// <summary>This session only. Nothing is written down, because
            /// indoors without a marker there is no way to put anything back
            /// where it was and persistence would only promise drift.</summary>
            Indoors,

            /// <summary>Pinned by printed markers. Centimetres, repeatable, and
            /// the only accurate indoor option there is.</summary>
            Venue
        }

        public static Working Now = Working.Outdoors;

        /// <summary>Chosen once per launch. Not remembered across launches on
        /// purpose: which of these is right depends on where somebody is
        /// standing, and where they were standing yesterday is no guide.</summary>
        public static bool Chosen;

        public static string Called(Working what)
        {
            switch (what)
            {
                case Working.Indoors: return "Indoors, this session";
                case Working.Venue: return "Venue";
                default: return "Outdoors";
            }
        }
    }
}
