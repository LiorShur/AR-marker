namespace MarkerOne.Unity
{
    /// <summary>
    /// Which build this is, on the screen.
    ///
    /// Exists because "is the fix in the build you are holding?" is otherwise
    /// unanswerable from a photograph, and answering it wrongly costs a whole
    /// round of testing: a fix that is working looks exactly like a fix that
    /// was never installed. Bumped by hand whenever anything under this folder
    /// changes, which is the point — a stamp generated automatically from the
    /// commit would also be right, and would need a build pipeline to be right,
    /// and there is no build pipeline here.
    /// </summary>
    public static class Build
    {
        public const string Stamp = "b1 · aim at what is drawn";
    }
}
