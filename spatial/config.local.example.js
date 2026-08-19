/* Copy to spatial/config.local.js and fill in. That file is gitignored, so
 * your project settings stay out of version control and pulls stay clean.
 *
 * The web API key is not a secret — it identifies the project to Google's
 * endpoints and authorises nothing. What protects the data is
 * firestore.rules, plus App Check. Keeping it out of git is for tidiness,
 * not for safety.
 */
SpatialConfig.override({
  projectId: 'your-project-id',
  apiKey: 'AIza...',

  radiusM: 300,
  relocalizeAfterM: 25
});
