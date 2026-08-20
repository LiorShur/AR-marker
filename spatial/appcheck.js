/* App Check tokens, over REST.
 *
 * Anonymous auth costs an attacker one HTTP request, and firestore.rules
 * cannot rate-limit. App Check is the control that makes abuse expensive: the
 * request has to come from a page that passed a reCAPTCHA challenge, and
 * Firestore refuses it otherwise.
 *
 * Two honest caveats.
 *
 * First, this is the one thing in the project that loads a script from
 * somewhere else. reCAPTCHA has to run Google's code from Google's servers;
 * there is no offline attestation. It loads only when App Check is configured,
 * so the app without placements still fetches nothing at runtime that it did
 * not ship — but if that property matters more to you than write abuse does,
 * leave this off.
 *
 * Second, once enforcement is switched on in the console, a client that cannot
 * get a token cannot write at all. That is the point, and it means a
 * misconfiguration here looks exactly like the database being down. The errors
 * below say which it is.
 */
(function (root, factory) {
  var api = factory();
  if (typeof module === 'object' && module.exports) { module.exports = api; }
  else { root.SpatialAppCheck = api; }
}(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  /* Two different products with the same name.
   *
   * reCAPTCHA v3 ("classic") keys come from google.com/recaptcha/admin, have a
   * site key and a secret, and exchange at :exchangeRecaptchaV3Token.
   *
   * reCAPTCHA Enterprise keys come from the Google Cloud console, are listed
   * with an "ID" and a type of "Website / Score", load a different script, and
   * exchange at :exchangeRecaptchaEnterpriseToken.
   *
   * Sending one to the other's endpoint fails in a way that says nothing
   * useful, so the provider is declared rather than guessed.
   */
  var SCRIPTS = {
    v3: 'https://www.google.com/recaptcha/api.js',
    enterprise: 'https://www.google.com/recaptcha/enterprise.js'
  };
  var EXCHANGE_PATH = {
    v3: ':exchangeRecaptchaV3Token',
    enterprise: ':exchangeRecaptchaEnterpriseToken'
  };
  var TOKEN_FIELD = {
    v3: 'recaptcha_v3_token',
    enterprise: 'recaptcha_enterprise_token'
  };

  var EXCHANGE = 'https://firebaseappcheck.googleapis.com/v1';

  // Renew a little early. A token that expires between being fetched and being
  // used is a write that fails for no reason the user can act on.
  var EARLY_MS = 5 * 60 * 1000;

  function create(config) {
    var settings = (config && config.appCheck) || {};
    var siteKey = settings.recaptchaSiteKey;
    var appId = settings.appId;
    // The console gives a project *number* here, not the project id. They are
    // different, and the wrong one returns a flat 404.
    var project = settings.projectNumber || (config && config.projectId);
    var apiKey = config && config.apiKey;

    var kind = settings.provider === 'enterprise' ? 'enterprise' : 'v3';
    var enabled = !!(siteKey && appId && project && apiKey);
    var fetchImpl = (config && config.fetch) ||
      (typeof fetch !== 'undefined' ? fetch.bind(null) : null);

    var token = null;          // { value, expiresAt }
    var pending = null;
    var recaptcha = null;

    function loadRecaptcha() {
      if (recaptcha) { return recaptcha; }

      recaptcha = new Promise(function (resolve, reject) {
        var existing = api();
        if (existing && existing.execute) { return resolve(existing); }

        var script = document.createElement('script');
        script.src = SCRIPTS[kind] + '?render=' + encodeURIComponent(siteKey);
        script.async = true;
        script.onload = function () {
          var g = api();
          if (!g) { return reject(new Error('reCAPTCHA loaded but did not register')); }
          g.ready(function () { resolve(g); });
        };
        script.onerror = function () {
          reject(new Error('reCAPTCHA could not be loaded — App Check needs network access to google.com'));
        };
        document.head.appendChild(script);
      }).catch(function (err) { recaptcha = null; throw err; });

      return recaptcha;
    }

    // Enterprise hangs its API off grecaptcha.enterprise; classic is
    // grecaptcha itself. Reading the wrong one gives undefined and a stack
    // trace three frames from anything relevant.
    function api() {
      if (!root.grecaptcha) { return null; }
      return kind === 'enterprise' ? root.grecaptcha.enterprise : root.grecaptcha;
    }

    function mint() {
      return loadRecaptcha()
        .then(function (g) { return g.execute(siteKey, { action: 'placement' }); })
        .then(function (recaptchaToken) {
          return fetchImpl(
            EXCHANGE + '/projects/' + encodeURIComponent(project) +
            '/apps/' + encodeURIComponent(appId) +
            EXCHANGE_PATH[kind] + '?key=' + encodeURIComponent(apiKey),
            {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(body(recaptchaToken))
            }
          );
        })
        .then(function (res) {
          return res.text().then(function (text) {
            var data = text ? JSON.parse(text) : null;
            if (!res.ok) {
              var detail = (data && data.error && data.error.message) || ('HTTP ' + res.status);
              throw new Error('App Check exchange refused (' + kind + '): ' + detail +
                ' — check the project number, the app id, the site key, and that ' +
                'appCheck.provider matches the kind of key you actually made.');
            }
            return data;
          });
        })
        .then(function (data) {
          // ttl comes back as a duration string, "3600s".
          var ttl = parseInt(String(data.ttl || '3600s'), 10) * 1000;
          token = {
            value: data.token,
            expiresAt: Date.now() + Math.max(60000, ttl) - EARLY_MS
          };
          return token.value;
        });
    }

    function body(token) {
      var out = {};
      out[TOKEN_FIELD[kind]] = token;
      return out;
    }

    function get() {
      if (!enabled) { return Promise.resolve(null); }
      if (token && Date.now() < token.expiresAt) { return Promise.resolve(token.value); }
      if (pending) { return pending; }

      pending = mint().then(function (value) {
        pending = null;
        return value;
      }, function (err) {
        pending = null;
        token = null;
        throw err;
      });

      return pending;
    }

    return {
      enabled: enabled,
      get: get,
      // For anyone wondering why writes started failing the day enforcement
      // was switched on.
      describe: function () {
        if (enabled) { return 'App Check on, reCAPTCHA ' + kind; }
        var missing = [];
        if (!siteKey) { missing.push('recaptchaSiteKey'); }
        if (!appId) { missing.push('appId'); }
        if (!project) { missing.push('projectNumber'); }
        return 'App Check off — missing ' + missing.join(', ');
      }
    };
  }

  return { create: create };
}));
