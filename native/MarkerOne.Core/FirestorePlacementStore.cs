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
        private const string SecureToken = "https://securetoken.googleapis.com/v1";
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

            // A request that never returns is indistinguishable from one that
            // has not been sent, and both look like an empty world. The default
            // is a hundred seconds, which on a phone is a hang: the read comes
            // back with nothing, the write lands nowhere, the state stays
            // Ready, and nothing anywhere says so. Fifteen seconds is longer
            // than any of these calls should take on a bad connection and short
            // enough that a stall becomes an error somebody can read.
            //
            // Only set when the client was made here; one passed in belongs to
            // the caller.
            if (http == null) { _http.Timeout = TimeSpan.FromSeconds(15); }
            _collection = collection;
            _appCheck = appCheck;
        }

        private string Documents =>
            $"{Firestore}/projects/{_projectId}/databases/(default)/documents";

        // ── identity ─────────────────────────────────────────────

        /// <summary>Reads back whatever WriteRefreshToken last saved, or null.
        /// Supplied by the host, because where a device keeps a secret is not
        /// something this assembly can know.</summary>
        public Func<string> ReadRefreshToken { get; set; }

        public Action<string> WriteRefreshToken { get; set; }

        /// <summary>
        /// Where the account's name is kept, alongside the refresh token.
        ///
        /// Needed because a refresh restores the token and the uid but knows
        /// nothing about who they belong to — so without this, somebody signed
        /// in comes back from a relaunch looking anonymous and gets asked to
        /// sign in again every launch. It also covers Apple, which returns an
        /// email on the first authorization only and nothing at all on every
        /// one after it.
        /// </summary>
        public Func<string> ReadAccount { get; set; }

        public Action<string> WriteAccount { get; set; }

        /// <summary>
        /// Anonymous sign-in identifies the device without asking anyone for
        /// anything. It is not a security boundary — anyone can mint one — so
        /// it answers "who wrote this", never "who is allowed".
        ///
        /// accounts:signUp creates a *new* user every time it is called, so
        /// without somewhere to keep the refresh token the uid lasts exactly
        /// one launch. Everything the device placed yesterday then belongs to a
        /// stranger: it cannot be edited, cannot be deleted, and "the owner may
        /// remove their own placement" is a rule that can never once be
        /// satisfied. Which is what "0 removed, 5 refused" was.
        /// </summary>
        public async Task<string> SignInAsync(CancellationToken cancel = default)
        {
            if (_idToken != null && DateTimeOffset.UtcNow < _tokenExpires.AddMinutes(-1))
            {
                return _idToken;
            }

            string refresh = ReadRefreshToken?.Invoke();
            if (!string.IsNullOrEmpty(refresh) &&
                await TryRefreshAsync(refresh, cancel).ConfigureAwait(false))
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
            WriteRefreshToken?.Invoke(root["refreshToken"].AsString);

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

        /// <summary>
        /// Exchange a Google identity for a Firebase one.
        ///
        /// The uid this produces belongs to the Google account rather than to
        /// the device, which is the whole point: it is the same on the next
        /// phone, and it survives reinstalling the app. Anonymous sign-in
        /// cannot offer either, however carefully its token is kept.
        ///
        /// Everything downstream is unchanged. The rules ask who owns a
        /// placement and get a better answer.
        /// </summary>
        public Task<string> SignInWithGoogleAsync(string googleIdToken,
            CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(googleIdToken))
            {
                throw new ArgumentException("no Google id token", nameof(googleIdToken));
            }

            return SignInWithIdpAsync("id_token=" + googleIdToken + "&providerId=google.com",
                                      "google", cancel);
        }

        /// <summary>
        /// Sign in with Apple.
        ///
        /// The nonce is not optional and not decoration. Apple is given its
        /// SHA-256 and returns it inside the signed token; Firebase is given the
        /// original and checks that they match. Without it, a token captured
        /// from one sign-in could be replayed into another, and Firebase rejects
        /// the exchange rather than allow it.
        ///
        /// Apple also gives the email exactly once, on the very first sign-in
        /// for an account, and never again. Firebase keeps it, which is why this
        /// reads the address back from the response rather than from Apple.
        /// </summary>
        public Task<string> SignInWithAppleAsync(string appleIdToken, string rawNonce,
            CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(appleIdToken))
            {
                throw new ArgumentException("no Apple id token", nameof(appleIdToken));
            }

            string body = "id_token=" + appleIdToken + "&providerId=apple.com";
            if (!string.IsNullOrEmpty(rawNonce)) { body += "&nonce=" + rawNonce; }

            return SignInWithIdpAsync(body, "apple", cancel);
        }

        private async Task<string> SignInWithIdpAsync(string postBody, string provider,
            CancellationToken cancel)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{Identity}/accounts:signInWithIdp?key={Uri.EscapeDataString(_apiKey)}")
            {
                Content = Body(Json.Object()
                    .Set("postBody", Json.Of(postBody))
                    // Not used for anything by this flow, but the endpoint
                    // insists on one.
                    .Set("requestUri", Json.Of("http://localhost"))
                    .Set("returnSecureToken", Json.Of(true)))
            };

            Json root = await SendAsync(request, cancel).ConfigureAwait(false);

            // Apple hands over an email exactly once, at the first
            // authorization, and never again — so the name has to survive
            // somewhere, and "apple" is not a name.
            Adopt(root, root["email"].AsString ?? root["displayName"].AsString,
                  provider == "apple" ? "Apple account" : "Google account");

            return _idToken;
        }

        /// <summary>
        /// Make an account with an email and a password.
        ///
        /// Deliberately separate from signing in. A single "sign in or register"
        /// call is friendlier right up to the moment somebody mistypes an
        /// address they have used before, at which point it silently makes them
        /// a second empty account and everything they placed belongs to the
        /// first one.
        /// </summary>
        public async Task<string> RegisterAsync(string email, string password,
            CancellationToken cancel = default)
        {
            Json root = await PasswordAsync("accounts:signUp", email, password, cancel)
                              .ConfigureAwait(false);
            Adopt(root, email);
            return _idToken;
        }

        public async Task<string> SignInWithPasswordAsync(string email, string password,
            CancellationToken cancel = default)
        {
            Json root = await PasswordAsync("accounts:signInWithPassword", email, password,
                                            cancel).ConfigureAwait(false);
            Adopt(root, email);
            return _idToken;
        }

        /// <summary>Send a reset email. Nothing here can change a password, and
        /// an account nobody can get back into is a placement nobody can
        /// edit.</summary>
        /// <summary>
        /// Ask Firebase to send the confirmation link.
        ///
        /// Worth having because email_verified is what an admin rule turns on,
        /// and a password account arrives unverified: without this the only way
        /// to become verified is to abandon the account and sign in with a
        /// provider that vouches for the address.
        /// </summary>
        public async Task VerifyEmailAsync(CancellationToken cancel = default)
        {
            string token = await SignInAsync(cancel).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{Identity}/accounts:sendOobCode?key={Uri.EscapeDataString(_apiKey)}")
            {
                Content = Body(Json.Object()
                    .Set("requestType", Json.Of("VERIFY_EMAIL"))
                    .Set("idToken", Json.Of(token)))
            };

            await SendAsync(request, cancel).ConfigureAwait(false);
        }

        public async Task ResetPasswordAsync(string email, CancellationToken cancel = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{Identity}/accounts:sendOobCode?key={Uri.EscapeDataString(_apiKey)}")
            {
                Content = Body(Json.Object()
                    .Set("requestType", Json.Of("PASSWORD_RESET"))
                    .Set("email", Json.Of(email ?? "")))
            };

            await SendAsync(request, cancel).ConfigureAwait(false);
        }

        private async Task<Json> PasswordAsync(string endpoint, string email, string password,
            CancellationToken cancel)
        {
            if (string.IsNullOrEmpty(email)) { throw new ArgumentException("no email"); }
            if (string.IsNullOrEmpty(password)) { throw new ArgumentException("no password"); }

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{Identity}/{endpoint}?key={Uri.EscapeDataString(_apiKey)}")
            {
                Content = Body(Json.Object()
                    .Set("email", Json.Of(email))
                    .Set("password", Json.Of(password))
                    .Set("returnSecureToken", Json.Of(true)))
            };

            return await SendAsync(request, cancel).ConfigureAwait(false);
        }

        /// <summary>Take on the identity in a sign-in response. One place, so
        /// that a provider added later cannot forget the refresh token and
        /// silently become an identity that lasts one launch.</summary>
        private void Adopt(Json root, string signed, string fallback = null)
        {
            _idToken = root["idToken"].AsString;
            Uid = root["localId"].AsString;
            WriteRefreshToken?.Invoke(root["refreshToken"].AsString);

            double seconds = 3600;
            string raw = root["expiresIn"].AsString;
            if (raw != null && double.TryParse(raw, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out double parsed))
            {
                seconds = parsed;
            }
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(seconds);

            ReadClaims();

            if (string.IsNullOrEmpty(signed)) { signed = Remembered(Uid) ?? fallback; }

            Signed = signed;
            WriteAccount?.Invoke(Uid + "\t" + Signed);
        }

        /// <summary>The saved name, but only if it belongs to this uid. Signing
        /// in as somebody else and inheriting the last person's name is a worse
        /// failure than showing no name at all.</summary>
        private string Remembered(string uid)
        {
            string saved = ReadAccount?.Invoke();
            if (string.IsNullOrEmpty(saved) || string.IsNullOrEmpty(uid)) { return null; }

            int tab = saved.IndexOf('\t');
            if (tab <= 0 || saved.Substring(0, tab) != uid) { return null; }

            string name = saved.Substring(tab + 1);
            return string.IsNullOrEmpty(name) ? null : name;
        }

        /// <summary>Who is signed in, for showing. Null while anonymous.</summary>
        public string Signed { get; private set; }

        /// <summary>
        /// The email the token actually carries, and whether Firebase considers
        /// it proven.
        ///
        /// Read out of the token rather than out of the sign-in response,
        /// because this is precisely what a rule matching on email sees — and
        /// an admin rule that will never fire is otherwise indistinguishable
        /// from a broken button. Unverified means anyone could have claimed the
        /// address, so no rule should trust it, and knowing that on screen is
        /// the difference between a five-minute fix and an afternoon.
        /// </summary>
        public string Email { get; private set; }

        public bool EmailVerified { get; private set; }

        /// <summary>
        /// A JWT's middle segment is base64url-encoded JSON and needs no key to
        /// read — the signature is what needs the key, and verifying it is the
        /// server's job, not ours. Nothing here is trusted; it is shown.
        /// </summary>
        private void ReadClaims()
        {
            Email = null;
            EmailVerified = false;

            try
            {
                string[] parts = _idToken?.Split('.');
                if (parts == null || parts.Length < 2) { return; }

                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }

                Json claims = Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));

                Email = claims["email"].AsString;
                EmailVerified = claims["email_verified"].AsBool;
            }
            catch
            {
                // A token this cannot read is still a token that works. This is
                // for showing, so failing to show it is not worth an error.
            }
        }

        /// <summary>Forget the identity entirely — the token, the uid and
        /// whatever was persisted — so the next call signs in afresh.</summary>
        public void SignOut()
        {
            _idToken = null;
            Uid = null;
            Signed = null;
            Email = null;
            EmailVerified = false;
            _tokenExpires = DateTimeOffset.MinValue;
            WriteRefreshToken?.Invoke("");
            WriteAccount?.Invoke("");
        }

        /// <summary>Trade a saved refresh token for a live one, keeping the uid
        /// that came with it. False on any failure — a refresh token can be
        /// revoked or simply stale, and the remedy is a new anonymous user, not
        /// an error the caller has to think about.</summary>
        private async Task<bool> TryRefreshAsync(string refresh, CancellationToken cancel)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{SecureToken}/token?key={Uri.EscapeDataString(_apiKey)}")
                {
                    Content = Body(Json.Object()
                        .Set("grant_type", Json.Of("refresh_token"))
                        .Set("refresh_token", Json.Of(refresh)))
                };

                Json root = await SendAsync(request, cancel).ConfigureAwait(false);

                string token = root["id_token"].AsString;
                string uid = root["user_id"].AsString;
                if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(uid)) { return false; }

                _idToken = token;
                Uid = uid;

                ReadClaims();

                // Anonymous devices have nothing saved, so this leaves them
                // anonymous, which is right.
                Signed = Remembered(uid);

                double seconds = 3600;
                string raw = root["expires_in"].AsString;
                if (raw != null && double.TryParse(raw, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out double parsed))
                {
                    seconds = parsed;
                }
                _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(seconds);

                string rotated = root["refresh_token"].AsString;
                if (!string.IsNullOrEmpty(rotated)) { WriteRefreshToken?.Invoke(rotated); }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
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

        /// <summary>
        /// Everything in one venue.
        ///
        /// By name rather than by geohash, because nothing in a venue has
        /// coordinates worth querying on: the whole reason a venue exists is
        /// that indoors there is no fix to write down. The venue id is the
        /// index.
        /// </summary>
        public async Task<IReadOnlyList<Placement>> InVenueAsync(string venue,
            CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(venue)) { throw new ArgumentException("no venue"); }

            await SignInAsync(cancel).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Documents}:runQuery")
            {
                Content = Body(VenueQuery(venue))
            };
            await AuthorizeAsync(request, cancel).ConfigureAwait(false);

            Json rows = await SendAsync(request, cancel).ConfigureAwait(false);

            var found = new List<Placement>();
            foreach (Json row in rows.Items)
            {
                if (!row.Has("document")) { continue; }

                Placement p = FromDocument(row["document"]);
                if (p?.Id != null) { found.Add(p); }
            }

            return found;
        }

        private Json VenueQuery(string venue)
        {
            // The visibility equality for the same reason as the nearby query:
            // rules are not filters, and without it the whole query is refused
            // rather than narrowed.
            Json filters = Json.Array_()
                .Add(Filter("visibility", "EQUAL", "public"))
                .Add(Filter("venue", "EQUAL", venue));

            return Json.Object().Set("structuredQuery", Json.Object()
                .Set("from", Json.Array_().Add(Json.Object().Set("collectionId", _collection)))
                .Set("where", Json.Object().Set("compositeFilter", Json.Object()
                    .Set("op", "AND")
                    .Set("filters", filters)))
                .Set("limit", MaxPerRange));
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
            double groundOffset, bool claim = false, CancellationToken cancel = default)
        {
            await SignInAsync(cancel).ConfigureAwait(false);

            string mask = "updateMask.fieldPaths=geopose&updateMask.fieldPaths=geohash" +
                          "&updateMask.fieldPaths=groundOffset";

            Json fields = Json.Object()
                .Set("geopose", GeoPose(position, Geodesy.HeadingToQuaternion(headingDeg)))
                .Set("geohash", Wrap(Geodesy.Geohash(position.Lat, position.Lon, 10)))
                .Set("groundOffset", Wrap(groundOffset));

            if (claim)
            {
                // Ownership and provider have to travel with the position, and
                // in the same request. The rules judge the document that would
                // result, so a mask that leaves the owner out leaves it as
                // whoever wrote the seed — and the write is then refused for
                // being an edit to somebody else's placement, which is exactly
                // what it is not.
                mask += "&updateMask.fieldPaths=owner&updateMask.fieldPaths=fix";

                fields.Set("owner", Wrap(Uid))
                      .Set("fix", Json.Object().Set("mapValue", Json.Object().Set("fields",
                          Json.Object()
                              .Set("provider", Wrap("geospatial"))
                              .Set("positionM", Wrap(0.0))
                              .Set("headingDeg", Wrap(0.0)))));
            }

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

        /// <summary>How many times to send a request before giving up. Four
        /// attempts over about seven seconds, because the failures being
        /// retried here are a phone on a weak cellular link, and those come in
        /// runs of seconds rather than milliseconds.</summary>
        public int Attempts { get; set; } = 4;

        /// <summary>Milliseconds before the second attempt; doubled thereafter.</summary>
        public int BackoffMs { get; set; } = 1000;

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

                    // Status first, then the document, then the reason. The
                    // readout truncates a long line, and the full REST path is
                    // 90 characters of boilerplate that would push the status
                    // and the reason off the end of it — which is exactly what
                    // "could not remove … — DELETE /v1/projects/…" was.
                    var failed = new HttpRequestException(
                        $"{(int)response.StatusCode} on {method} {Tail(uri)}: {detail}");

                    int status = (int)response.StatusCode;
                    if (status < 500 && status != 429) { throw failed; }

                    last = failed;
                }
            }

            throw last ?? new HttpRequestException($"{method} {uri?.AbsolutePath} failed");
        }

        /// <summary>The last two segments of a REST path — the collection and
        /// the document — which is the whole of what a person reading an error
        /// needs from it.</summary>
        private static string Tail(Uri uri)
        {
            string path = uri?.AbsolutePath;
            if (string.IsNullOrEmpty(path)) { return "?"; }

            string[] parts = path.Split('/');
            return parts.Length < 2
                ? path
                : parts[parts.Length - 2] + "/" + parts[parts.Length - 1];
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
                .Set("author", Wrap(p.Author ?? ""))
                .Set("visibility", Wrap(p.Visibility))
                .Set("owner", Wrap(p.Owner))
                .Set("createdAt", Wrap(p.CreatedAt))
                .Set("fix", Map(fix));

            // Written only when there is one, so a root stays exactly the
            // document it was before any of this existed.
            if (p.IsChild)
            {
                fields.Set("parent", Wrap(p.Parent)).Set("local", Pose(p.Offset));
            }

            if (p.InVenue)
            {
                fields.Set("venue", Wrap(p.Venue)).Set("at", Pose(p.At));

                if (!string.IsNullOrEmpty(p.Marker)) { fields.Set("marker", Wrap(p.Marker)); }
            }

            return Json.Object().Set("fields", fields);
        }

        /// <summary>Somewhere and some way round, in whatever frame the field
        /// it is written to means.</summary>
        private static Json Pose(Attachment a) =>
            Map(Json.Object()
                .Set("x", Wrap(a.X))
                .Set("y", Wrap(a.Y))
                .Set("z", Wrap(a.Z))
                .Set("rotation", Map(Json.Object()
                    .Set("x", Wrap(a.Rotation.X))
                    .Set("y", Wrap(a.Rotation.Y))
                    .Set("z", Wrap(a.Rotation.Z))
                    .Set("w", Wrap(a.Rotation.W)))));

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
                Author = Str(fields, "author") ?? "",
                Owner = Str(fields, "owner"),
                CreatedAt = Str(fields, "createdAt"),
                Visibility = Str(fields, "visibility") ?? "public"
            };

            Json pose = Inner(fields, "geopose");
            Json position = Inner(pose, "position");
            p.Position = new GeoPoint(Num(position, "lat", 0), Num(position, "lon", 0), Num(position, "h", 0));

            Json q = Inner(pose, "quaternion");
            p.Orientation = new Quat(Num(q, "x", 0), Num(q, "y", 0), Num(q, "z", 0), Num(q, "w", 1));

            p.Parent = Str(fields, "parent");
            if (!string.IsNullOrEmpty(p.Parent) && fields.Has("local"))
            {
                p.Offset = ReadPose(Inner(fields, "local"));
            }

            p.Venue = Str(fields, "venue");
            p.Marker = Str(fields, "marker");
            if (!string.IsNullOrEmpty(p.Venue) && fields.Has("at"))
            {
                p.At = ReadPose(Inner(fields, "at"));
            }

            Json fix = Inner(fields, "fix");
            p.Fix = new FixQuality
            {
                Provider = Str(fix, "provider") ?? "unknown",
                PositionM = Num(fix, "positionM", 0),
                HeadingDeg = Num(fix, "headingDeg", 0)
            };

            return p;
        }

        private static Attachment ReadPose(Json pose)
        {
            Json turn = Inner(pose, "rotation");
            return new Attachment
            {
                X = Num(pose, "x", 0),
                Y = Num(pose, "y", 0),
                Z = Num(pose, "z", 0),
                Rotation = new Quat(Num(turn, "x", 0), Num(turn, "y", 0),
                                    Num(turn, "z", 0), Num(turn, "w", 1))
            };
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
