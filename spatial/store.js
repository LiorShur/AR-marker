/* Placements, over the Firestore REST API.
 *
 * No Firebase SDK. The modular SDK is an ES module tree that wants a bundler,
 * and every hosted copy of it is a CDN request — this project's whole claim is
 * that it fetches nothing at runtime it did not ship. The REST API needs
 * neither: two endpoints for an anonymous token, one for a query.
 *
 * What that costs is snapshot listeners. REST has no streaming reads, so
 * "someone else placed something just now" needs a poll or a refresh here.
 * When shared sessions need to be live rather than merely shared, that is the
 * point to vendor the SDK — not before.
 *
 * Firestore has no geospatial index, so proximity is done the usual way: a
 * geohash string per placement, and a circle becomes a handful of string
 * ranges. See spatial/geo.js.
 */
(function (root, factory) {
  var api = factory(typeof module === 'object' && module.exports
    ? require('./geo.js')
    : root.SpatialGeo);
  if (typeof module === 'object' && module.exports) { module.exports = api; }
  else { root.SpatialStore = api; }
}(typeof self !== 'undefined' ? self : this, function (geo) {
  'use strict';

  var IDENTITY = 'https://identitytoolkit.googleapis.com/v1';
  var SECURETOKEN = 'https://securetoken.googleapis.com/v1';
  var FIRESTORE = 'https://firestore.googleapis.com/v1';

  var MAX_PER_RANGE = 200;

  function createStore(config) {
    var projectId = config.projectId;
    var apiKey = config.apiKey;
    var fetchImpl = config.fetch || (typeof fetch !== 'undefined' ? fetch.bind(null) : null);
    var endpoints = config.endpoints || {};
    var identity = endpoints.identity || IDENTITY;
    var securetoken = endpoints.securetoken || SECURETOKEN;
    var firestore = endpoints.firestore || FIRESTORE;
    var collection = config.collection || 'placements';

    var session = null;          // { idToken, refreshToken, uid, expiresAt }
    var pending = null;
    // Optional. When enforcement is on in the console, requests without a
    // valid token are refused outright — which is the entire point, and also
    // why a failure here has to be reported as itself and not as a network
    // problem.
    var appCheck = config.appCheck || null;

    function docs() {
      return firestore + '/projects/' + projectId + '/databases/(default)/documents';
    }

    /* ── identity ──────────────────────────────────────────────
       Anonymous sign-in gives every visitor a stable uid without asking them
       for anything. It is not a security boundary — anyone can mint one — so
       the rules treat it as "who wrote this", never as "who is allowed". */

    function signIn() {
      if (session && Date.now() < session.expiresAt - 60000) {
        return Promise.resolve(session);
      }
      if (pending) { return pending; }

      var flow = session && session.refreshToken ? refresh() : fresh();
      pending = flow.then(function (s) { pending = null; return s; },
        function (err) { pending = null; throw err; });
      return pending;
    }

    function fresh() {
      return post(identity + '/accounts:signUp?key=' + encodeURIComponent(apiKey),
        { returnSecureToken: true }
      ).then(function (r) {
        session = {
          idToken: r.idToken,
          refreshToken: r.refresh_token || r.refreshToken,
          uid: r.localId,
          expiresAt: Date.now() + (Number(r.expiresIn || 3600) * 1000)
        };
        return session;
      });
    }

    function refresh() {
      return post(securetoken + '/token?key=' + encodeURIComponent(apiKey),
        { grant_type: 'refresh_token', refresh_token: session.refreshToken }
      ).then(function (r) {
        session = {
          idToken: r.id_token || r.access_token,
          refreshToken: r.refresh_token || session.refreshToken,
          uid: r.user_id || session.uid,
          expiresAt: Date.now() + (Number(r.expires_in || 3600) * 1000)
        };
        return session;
      }).catch(function () {
        // A refresh token can be revoked or simply expire. Falling back to a
        // new anonymous identity loses ownership of anything placed before,
        // which is the honest consequence of never asking who anyone is.
        session = null;
        return fresh();
      });
    }

    function post(url, body, token) {
      return attest().then(function (attestation) {
        var headers = { 'Content-Type': 'application/json' };
        if (token) { headers.Authorization = 'Bearer ' + token; }
        if (attestation) { headers['X-Firebase-AppCheck'] = attestation; }
        return send(url, headers, body);
      });
    }

    function attest() {
      if (!appCheck || !appCheck.enabled) { return Promise.resolve(null); }
      return appCheck.get().catch(function (err) {
        // Carry on unattested rather than block. With enforcement off this
        // changes nothing; with it on the server refuses and says so, which
        // is a better error than one invented here.
        if (typeof console !== 'undefined' && console.warn) {
          console.warn('App Check token unavailable: ' + err.message);
        }
        return null;
      });
    }

    function send(url, headers, body) {
      return fetchImpl(url, { method: 'POST', headers: headers, body: JSON.stringify(body) })
        .then(function (res) {
          return res.text().then(function (text) {
            var data = text ? JSON.parse(text) : null;
            if (!res.ok) {
              var message = (data && data.error && (data.error.message || data.error.status)) ||
                            ('HTTP ' + res.status);
              var err = new Error(message);
              err.status = res.status;
              throw err;
            }
            return data;
          });
        });
    }

    /* ── reading ───────────────────────────────────────────────
       One query per geohash range, in parallel, then merged and filtered by
       true distance. The ranges over-select by design: a geohash cell is a
       rectangle and the query is a circle, so the corners come back too. */

    function nearby(lat, lon, radiusM, options) {
      options = options || {};
      var ranges = geo.geohashQueryBounds(lat, lon, radiusM);

      return signIn().then(function (s) {
        return Promise.all(ranges.map(function (range) {
          return runQuery(rangeQuery(range, options), s.idToken);
        }));
      }).then(function (pages) {
        var seen = {};
        var out = [];

        pages.forEach(function (page) {
          page.forEach(function (placement) {
            if (seen[placement.id]) { return; }
            seen[placement.id] = true;

            placement.distance = geo.haversine(lat, lon,
              placement.geopose.position.lat, placement.geopose.position.lon);
            if (placement.distance <= radiusM) { out.push(placement); }
          });
        });

        return out.sort(function (a, b) { return a.distance - b.distance; });
      });
    }

    function rangeQuery(range, options) {
      var filters = [
        // Rules are not filters. The read rule turns on
        // resource.data.visibility, and Firestore permits a query only if the
        // rules can prove from the query's own constraints that every document
        // it could return is readable. Without this equality the entire query
        // is refused — not filtered down, refused — and the refusal looks
        // exactly like there being nothing there.
        fieldFilter('visibility', 'EQUAL', { stringValue: options.visibility || 'public' }),
        fieldFilter('geohash', 'GREATER_THAN_OR_EQUAL', { stringValue: range[0] }),
        fieldFilter('geohash', 'LESS_THAN_OR_EQUAL', { stringValue: range[1] })
      ];
      if (options.scene) {
        filters.push(fieldFilter('scene', 'EQUAL', { stringValue: options.scene }));
      }

      return {
        structuredQuery: {
          from: [{ collectionId: collection }],
          where: { compositeFilter: { op: 'AND', filters: filters } },
          orderBy: [{ field: { fieldPath: 'geohash' }, direction: 'ASCENDING' }],
          limit: options.limit || MAX_PER_RANGE
        }
      };
    }

    function fieldFilter(path, op, value) {
      return { fieldFilter: { field: { fieldPath: path }, op: op, value: value } };
    }

    function runQuery(query, token) {
      return post(docs() + ':runQuery', query, token).then(function (rows) {
        // runQuery returns one element per match, plus possibly a single
        // element with no document at all when nothing matched.
        return (rows || [])
          .filter(function (row) { return row && row.document; })
          .map(function (row) { return fromDocument(row.document); });
      });
    }

    /* ── writing ─────────────────────────────────────────────── */

    function place(placement) {
      var record = normalise(placement);
      var problems = validate(record);
      if (problems.length) {
        return Promise.reject(new Error('invalid placement: ' + problems.join('; ')));
      }

      return signIn().then(function (s) {
        record.owner = s.uid;
        record.createdAt = new Date().toISOString();
        return post(docs() + '/' + collection, { fields: toFields(record) }, s.idToken);
      }).then(fromDocument);
    }

    function normalise(p) {
      var pos = (p.geopose && p.geopose.position) || {};
      var quat = (p.geopose && p.geopose.quaternion) || { x: 0, y: 0, z: 0, w: 1 };

      return {
        geopose: {
          position: { lat: Number(pos.lat), lon: Number(pos.lon), h: Number(pos.h || 0) },
          quaternion: {
            x: Number(quat.x || 0), y: Number(quat.y || 0),
            z: Number(quat.z || 0), w: quat.w === undefined ? 1 : Number(quat.w)
          }
        },
        geohash: geo.geohash(Number(pos.lat), Number(pos.lon), 10),
        scene: String(p.scene || ''),
        scale: Number(p.scale === undefined ? 1 : p.scale),
        // How the pose was obtained, and how well. Stored rather than inferred
        // because a placement made by GPS and one made by a visual fix are the
        // same shape and nothing like the same thing.
        fix: {
          provider: String((p.fix && p.fix.provider) || 'unknown'),
          positionM: Number((p.fix && p.fix.positionM) || 0),
          headingDeg: Number((p.fix && p.fix.headingDeg) || 0)
        },
        visibility: p.visibility === 'private' ? 'private' : 'public'
      };
    }

    function validate(r) {
      var bad = [];
      var pos = r.geopose.position;

      if (!isFinite(pos.lat) || pos.lat < -90 || pos.lat > 90) { bad.push('latitude'); }
      if (!isFinite(pos.lon) || pos.lon < -180 || pos.lon > 180) { bad.push('longitude'); }
      if (!isFinite(pos.h) || Math.abs(pos.h) > 20000) { bad.push('height'); }
      if (!r.scene) { bad.push('scene is required'); }
      if (r.scene.length > 64) { bad.push('scene name too long'); }
      if (!isFinite(r.scale) || r.scale <= 0 || r.scale > 1000) { bad.push('scale'); }

      var q = r.geopose.quaternion;
      var len = Math.hypot(q.x, q.y, q.z, q.w);
      if (!isFinite(len) || Math.abs(len - 1) > 0.01) { bad.push('quaternion is not a unit'); }

      return bad;
    }

    function remove(id) {
      return Promise.all([signIn(), attest()]).then(function (parts) {
        var s = parts[0];
        var headers = { Authorization: 'Bearer ' + s.idToken };
        if (parts[1]) { headers['X-Firebase-AppCheck'] = parts[1]; }

        return fetchImpl(docs() + '/' + collection + '/' + encodeURIComponent(id), {
          method: 'DELETE',
          headers: headers
        }).then(function (res) {
          if (!res.ok) { throw new Error('could not delete: HTTP ' + res.status); }
          return true;
        });
      });
    }

    /* ── the REST value encoding ───────────────────────────────
       Firestore types every scalar in the wire format. Numbers are the trap:
       an integer-valued double comes back as integerValue and reading it as a
       number without care turns 1.0 into a string. */

    function toFields(obj) {
      var fields = {};
      Object.keys(obj).forEach(function (key) { fields[key] = toValue(obj[key]); });
      return fields;
    }

    function toValue(v) {
      if (v === null || v === undefined) { return { nullValue: null }; }
      if (typeof v === 'boolean') { return { booleanValue: v }; }
      if (typeof v === 'number') { return { doubleValue: v }; }
      if (typeof v === 'string') { return { stringValue: v }; }
      if (Array.isArray(v)) { return { arrayValue: { values: v.map(toValue) } }; }
      return { mapValue: { fields: toFields(v) } };
    }

    function fromValue(v) {
      if (!v) { return null; }
      if ('nullValue' in v) { return null; }
      if ('booleanValue' in v) { return v.booleanValue; }
      if ('stringValue' in v) { return v.stringValue; }
      if ('doubleValue' in v) { return Number(v.doubleValue); }
      if ('integerValue' in v) { return Number(v.integerValue); }
      if ('timestampValue' in v) { return v.timestampValue; }
      if ('arrayValue' in v) { return ((v.arrayValue && v.arrayValue.values) || []).map(fromValue); }
      if ('mapValue' in v) { return fromFields((v.mapValue && v.mapValue.fields) || {}); }
      return null;
    }

    function fromFields(fields) {
      var out = {};
      Object.keys(fields).forEach(function (key) { out[key] = fromValue(fields[key]); });
      return out;
    }

    function fromDocument(doc) {
      var out = fromFields(doc.fields || {});
      out.id = String(doc.name || '').split('/').pop();
      return out;
    }

    return {
      signIn: signIn,
      nearby: nearby,
      place: place,
      remove: remove,
      uid: function () { return session && session.uid; },
      // exposed for the tests, and for anyone debugging a wire format that
      // types every number twice
      _codec: { toFields: toFields, fromFields: fromFields, fromDocument: fromDocument },
      _validate: function (p) { return validate(normalise(p)); }
    };
  }

  return { createStore: createStore };
}));
