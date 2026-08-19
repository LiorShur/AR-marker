/* Firebase project settings.
 *
 * The web API key is not a secret. It identifies the project to Google's
 * endpoints; it authorises nothing. Anyone can read it out of any Firebase web
 * app, which is why what actually protects the data is firestore.rules, plus
 * App Check to make abuse cost something. Committing it is correct — treating
 * it as a credential is the mistake.
 *
 * Leave projectId empty and the app runs exactly as it did before: local
 * content.json, no network, no accounts.
 */
(function (root) {
  'use strict';

  root.SpatialConfig = {
    projectId: '',                 // e.g. 'markerone1965'
    apiKey: '',                    // Project settings -> Your apps -> Web API Key

    // Placements further than this from the viewer are not fetched. Loading a
    // city's worth of content to render the three things you can actually see
    // is the obvious mistake.
    radiusM: 300,

    // How far the device may travel before the local-to-global transform is
    // re-derived. WebXR tracking drifts, slowly and without saying so.
    relocalizeAfterM: 25
  };

  root.SpatialConfig.enabled = !!(root.SpatialConfig.projectId && root.SpatialConfig.apiKey);
}(typeof self !== 'undefined' ? self : this));
