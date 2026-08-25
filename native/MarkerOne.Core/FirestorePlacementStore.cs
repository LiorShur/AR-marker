using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MarkerOne.Core
{
    /// <summary>
    /// Placements over the Firestore REST API — the same two endpoints the web
    /// app uses, so both read and write the same documents.
    ///
    /// REST rather than the Firebase Unity SDK on purpose: the SDK is a large
    /// native dependency per platform, and this needs an HTTP client and
    /// nothing else. If push notifications or realtime listeners are wanted
    /// later, that is the moment to take the dependency, not before.
    /// </summary>
    public sealed class FirestorePlacementStore : IPlacementStore
    {
        private const string Identity = "https://identitytoolkit.googleapis.com/v1";
        private const string Firestore = "https://firestore.googleapis.com/v1";
        private const int MaxPerRange = 200;

        private readonly HttpClient _http;
        private readonly string _projectId;
        private readonly string _apiKey;
        private readonly string _collection;
        private readonly Func<CancellationToken, Task<string>> _appCheck;

        private string _idToken;
        private DateTimeOffset _tokenExpires = DateTimeOffset.MinValue;

        public string Uid { get; private set; }

        public FirestorePlacementStore(
            string projectId,
            string apiKey,
            HttpClient http = null,
            string collection = "placements",
            Func<CancellationToken, Task<string>> appCheck = null)
        {
            _projectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _http = http ?? new HttpClient();
            _collection = collection;
            _appCheck = appCheck;
        }

        private string Documents =>
            $"{Firestore}/projects/{_projectId}/databases/(default)/documents";

        // ── identity ─────────────────────────────────────────────

        /// <summary>Anonymous sign-in gives every device a stable uid without
        /// asking anyone for anything. It is not a security boundary — anyone
        /// can mint one — so it answers "who wrote this", never "who is
        /// allowed".</summary>
        public async Task<string> SignInAsync(CancellationToken cancel = default)
        {
            if (_idToken != null && DateTimeOffset.UtcNow < _tokenExpires.AddMinutes(-1))
            {
                return _idToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{Identity}/accounts:signUp?key={Uri.EscapeDataString(_apiKey)}")
            {
                Content = Body(Json.Object().Set("returnSecureToken", Json.Of(true)))
            };

            Json root = await SendAsync(request, cancel).ConfigureAwait(false);
            _idToken = root["idToken"].AsString;
            Uid = root["localId"].AsString;

            // expiresIn arrives as a string of seconds.
            double seconds = 3600;
            string raw = root["expiresIn"].AsString;
            if (raw != null && double.TryParse(raw, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out double parsed))
            {
                seconds = parsed;
            }
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(seconds);

            return _idToken;
        }

        // ── reading ──────────────────────────────────────────────

        public async Task<IReadOnlyList<Placement>> NearbyAsync(
            double lat, double lon, double radiusM, CancellationToken cancel = default)
        {
            await SignInAsync(cancel).ConfigureAwait(false);

            var found = new Dictionary<string, Placement>();

            foreach ((string start, string end) in Geodesy.GeohashQueryBounds(lat, lon, radiusM))
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{Documents}:runQuery")
                {
                    Content = Body(RangeQuery(start, end))
                };
                await AuthorizeAsync(request, cancel).ConfigureAwait(false);

                Json rows = await SendAsync(request, cancel).ConfigureAwait(false);

                foreach (Json row in rows.Items)
                {
                    // A query that matched nothing answers with a single
                    // element carrying no document at all.
                    if (!row.Has("document")) { continue; }

                    Placement p = FromDocument(row["document"]);
                    if (p?.Id == null || found.ContainsKey(p.Id)) { continue; }

                    p.DistanceM = Geodesy.Haversine(lat, lon, p.Position.Lat, p.Position.Lon);
                    // The ranges over-select by design: a geohash cell is a
                    // rectangle and the query is a circle, so the corners come
                    // back too.
                    if (p.DistanceM <= radiusM) { found[p.Id] = p; }
                }
            }

            return found.Values.OrderBy(p => p.DistanceM).ToList();
        }

        private Json RangeQuery(string start, string end)
        {
            // Rules are not filters. The read rule turns on visibility, and
            // Firestore permits a query only if the rules can prove from the
            // query's own constraints that everything it might return is
            // readable. Without this equality the whole query is refused — not
            // narrowed, refused — and a refusal looks exactly like an empty
            // world.
            Json filters = Json.Array_()
                .Add(Filter("visibility", "EQUAL", "public"))
                .Add(Filter("geohash", "GREATER_THAN_OR_EQUAL", start))
                .Add(Filter("geohash", "LESS_THAN_OR_EQUAL", end));

            Json order = Json.Array_().Add(Json.Object()
                .Set("field", Json.Object().Set("fieldPath", "geohash"))
                .Set("direction", "ASCENDING"));

            return Json.Object().Set("structuredQuery", Json.Object()
                .Set("from", Json.Array_().Add(Json.Object().Set("collectionId", _collection)))
                .Set("where", Json.Object().Set("compositeFilter", Json.Object()
                    .Set("op", "AND")
                    .Set("filters", filters)))
                .Set("orderBy", order)
                .Set("limit", MaxPerRange));
        }

        private static Json Filter(string path, string op, string value) =>
            Json.Object().Set("fieldFilter", Json.Object()
                .Set("field", Json.Object().Set("fieldPath", path))
                .Set("op", op)
                .Set("value", Json.Object().Set("stringValue", value)));

        // ── writing ──────────────────────────────────────────────

        public async Task<Placement> PlaceAsync(Placement placement, CancellationToken cancel = default)
        {
            IReadOnlyList<string> problems = placement.Problems();
            if (problems.Count > 0)
            {
                throw new ArgumentException("invalid placement: " + string.Join("; ", problems));
            }

            await SignInAsync(cancel).ConfigureAwait(false);
            placement.Owner = Uid;
            placement.CreatedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Documents}/{_collection}")
            {
                Content = Body(ToDocument(placement))
            };
            await AuthorizeAsync(request, cancel).ConfigureAwait(false);

            Json saved = await SendAsync(request, cancel).ConfigureAwait(false);
            return FromDocument(saved);
        }

        /// <summary>Correct a placement already saved. One written while the
        /// session was still settling carries that session's error for good —
        /// the local point it was dropped at is exact, the mapping to the globe
        /// was not. PATCH with an updateMask, so the owner, the label and the
        /// time it was left all stay as they were.</summary>
        public async Task MoveAsync(string id, GeoPoint position, double headingDeg,
            double groundOffset, CancellationToken cancel = default)
        {
            await SignInAsync(cancel).ConfigureAwait(false);

            string mask = "updateMask.fieldPaths=geopose&updateMask.fieldPaths=geohash" +
                          "&updateMask.fieldPaths=groundOffset";

            Json fields = Json.Object()
                .Set("geopose", GeoPose(position, Geodesy.HeadingToQuaternion(headingDeg)))
                .Set("geohash", Wrap(Geodesy.Geohash(position.Lat, position.Lon, 10)))
                .Set("groundOffset", Wrap(groundOffset));

            using var request = new HttpRequestMessage(
                new HttpMethod("PATCH"),
                $"{Documents}/{_collection}/{Uri.EscapeDataString(id)}?{mask}")
            {
                Content = Body(Json.Object().Set("fields", fields))
            };
            await AuthorizeAsync(request, cancel).ConfigureAwait(false);

            await SendAsync(request, cancel).ConfigureAwait(false);
        }

        public async Task RemoveAsync(string id, CancellationToken cancel = default)
        {
            await SignInAsync(cancel).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Delete,
                $"{Documents}/{_collection}/{Uri.EscapeDataString(id)}");
            await AuthorizeAsync(request, cancel).ConfigureAwait(false);

            await SendAsync(request, cancel).ConfigureAwait(false);
        }

        // ── the wire ─────────────────────────────────────────────

        private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _idToken);

            if (_appCheck == null) { return; }
            try
            {
                string token = await _appCheck(cancel).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers.TryAddWithoutValidation("X-Firebase-AppCheck", token);
                }
            }
            catch
            {
                // Carry on unattested. With enforcement off this changes
                // nothing; with it on the server refuses and says so, which is
                // a better error than one invented here.
            }
        }

        /// <summary>How many times to send a request before giving up.</summary>
        public int Attempts { get; set; } = 3;

        /// <summary>Milliseconds before the second attempt; doubled thereafter.</summary>
        public int BackoffMs { get; set; } = 400;

        /// <summary>
        /// Send, and try again when the failure was the network rather than the
        /// answer.
        ///
        /// A phone being carried around loses its connection constantly, and a
        /// dropped request was costing a placement outright — "could not place"
        /// on something the user had just aimed at and committed to. One retry
        /// makes almost all of those succeed.
        ///
        /// Retryable means the transport failed, the request timed out, or the
        /// server said 5xx or 429. A 400 or a 403 will say exactly the same
        /// thing the second time, and repeating it only delays the report.
        /// </summary>
        private async Task<Json> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            // Everything needed to build the request again. A
            // HttpRequestMessage cannot be sent twice, and its content stream
            // is consumed by the first attempt.
            HttpMethod method = request.Method;
            Uri uri = request.RequestUri;
            var headers = new List<KeyValuePair<string, IEnumerable<string>>>(request.Headers);
            string body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync().ConfigureAwait(false);

            Exception last = null;

            for (int attempt = 0; attempt < Math.Max(1, Attempts); attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(BackoffMs << (attempt - 1), cancel).ConfigureAwait(false);
                }

                using var send = new HttpRequestMessage(method, uri);
                foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
                {
                    send.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (body != null)
                {
                    send.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(send, cancel).ConfigureAwait(false);
                }
                catch (HttpRequestException e)
                {
                    last = e;
                    continue;
                }
                catch (TaskCanceledException e) when (!cancel.IsCancellationRequested)
                {
                    last = e;
                    continue;
                }

                using (response)
                {
                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        return Json.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
                    }

                    string detail = text;
                    try
                    {
                        string message = Json.Parse(text)["error"]["message"].AsString;
                        if (message != null) { detail = message; }
                    }
                    catch { /* the body was not JSON; the status will do */ }

                    var failed = new HttpRequestException(
                        $"{method} {uri?.AbsolutePath} failed: " +
                        $"{(int)response.StatusCode} {detail}");

                    int status = (int)response.StatusCode;
                    if (status < 500 && status != 429) { throw failed; }

                    last = failed;
                }
            }

            throw last ?? new HttpRequestException($"{method} {uri?.AbsolutePath} failed");
        }

        private static StringContent Body(Json value) =>
            new StringContent(value.ToString(), Encoding.UTF8, "application/json");

        // ── the REST value encoding ──────────────────────────────
        // Firestore types every scalar on the wire. Numbers are the trap: an
        // integer-valued double comes back as integerValue, and reading it
        // without care turns 1.0 into a string.

        private static Json ToDocument(Placement p)
        {
            Json fix = Json.Object()
                .Set("provider", Wrap(p.Fix.Provider))
                .Set("positionM", Wrap(p.Fix.PositionM))
                .Set("headingDeg", Wrap(p.Fix.HeadingDeg));

            Json fields = Json.Object()
                .Set("geopose", GeoPose(p.Position, p.Orientation))
                .Set("geohash", Wrap(p.Geohash))
                .Set("scene", Wrap(p.Scene))
                .Set("scale", Wrap(p.Scale))
                .Set("groundOffset", Wrap(p.GroundOffset))
                .Set("label", Wrap(p.Label ?? ""))
                .Set("visibility", Wrap(p.Visibility))
                .Set("owner", Wrap(p.Owner))
                .Set("createdAt", Wrap(p.CreatedAt))
                .Set("fix", Map(fix));

            return Json.Object().Set("fields", fields);
        }

        private static Json GeoPose(GeoPoint position, Quat q)
        {
            Json pos = Json.Object()
                .Set("lat", Wrap(position.Lat))
                .Set("lon", Wrap(position.Lon))
                .Set("h", Wrap(position.Height));

            Json orientation = Json.Object()
                .Set("x", Wrap(q.X))
                .Set("y", Wrap(q.Y))
                .Set("z", Wrap(q.Z))
                .Set("w", Wrap(q.W));

            return Map(Json.Object()
                .Set("position", Map(pos))
                .Set("quaternion", Map(orientation)));
        }

        private static Json Map(Json fields) =>
            Json.Object().Set("mapValue", Json.Object().Set("fields", fields));

        private static Json Wrap(string value) =>
            Json.Object().Set("stringValue", value ?? "");

        private static Json Wrap(double value) =>
            Json.Object().Set("doubleValue", value);

        private static Placement FromDocument(Json doc)
        {
            if (!doc.Has("fields")) { return null; }
            Json fields = doc["fields"];

            string name = doc["name"].AsString;
            var p = new Placement
            {
                Id = name?.Split('/').Last(),
                Scene = Str(fields, "scene"),
                Scale = Num(fields, "scale", 1),
                GroundOffset = Num(fields, "groundOffset", 0),
                Label = Str(fields, "label") ?? "",
                Owner = Str(fields, "owner"),
                CreatedAt = Str(fields, "createdAt"),
                Visibility = Str(fields, "visibility") ?? "public"
            };

            Json pose = Inner(fields, "geopose");
            Json position = Inner(pose, "position");
            p.Position = new GeoPoint(Num(position, "lat", 0), Num(position, "lon", 0), Num(position, "h", 0));

            Json q = Inner(pose, "quaternion");
            p.Orientation = new Quat(Num(q, "x", 0), Num(q, "y", 0), Num(q, "z", 0), Num(q, "w", 1));

            Json fix = Inner(fields, "fix");
            p.Fix = new FixQuality
            {
                Provider = Str(fix, "provider") ?? "unknown",
                PositionM = Num(fix, "positionM", 0),
                HeadingDeg = Num(fix, "headingDeg", 0)
            };

            return p;
        }

        private static Json Inner(Json fields, string name) => fields[name]["mapValue"]["fields"];

        private static string Str(Json fields, string name) => fields[name]["stringValue"].AsString;

        /// <summary>Firestore types every scalar, and numbers are the trap: an
        /// integer-valued double comes back as integerValue, and as a string at
        /// that. Reading it without care turns 1.0 into nothing.</summary>
        private static double Num(Json fields, string name, double fallback)
        {
            Json value = fields[name];

            if (value.Has("doubleValue")) { return value["doubleValue"].AsNumber; }

            if (value.Has("integerValue"))
            {
                Json raw = value["integerValue"];
                if (raw.Type == Json.Kind.Number) { return raw.AsNumber; }
                if (raw.AsString != null &&
                    double.TryParse(raw.AsString, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out double parsed))
                {
                    return parsed;
                }
            }

            return fallback;
        }
    }
}
