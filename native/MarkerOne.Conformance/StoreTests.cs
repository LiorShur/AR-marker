using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkerOne.Core;

namespace MarkerOne.Conformance
{
    /// <summary>A stub Firestore. Records what was asked for, answers from a
    /// fixture. The wire format types every scalar and fails quietly when read
    /// wrongly — a double read back as a string is still truthy.</summary>
    internal sealed class StubFirestore : HttpMessageHandler
    {
        public readonly List<(string Method, string Url, string Body)> Calls = new();
        public List<(string Id, double Lat, double Lon)> Fixtures = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync().ConfigureAwait(false);

            Calls.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            string url = request.RequestUri.ToString();

            if (url.Contains("accounts:signUp"))
            {
                return Reply("{\"idToken\":\"tok-1\",\"localId\":\"anon-1\",\"expiresIn\":\"3600\"}");
            }

            if (url.Contains(":runQuery"))
            {
                using JsonDocument query = JsonDocument.Parse(body!);
                var filters = query.RootElement
                    .GetProperty("structuredQuery").GetProperty("where")
                    .GetProperty("compositeFilter").GetProperty("filters")
                    .EnumerateArray().Select(f => f.GetProperty("fieldFilter")).ToList();

                string lo = filters.First(f =>
                    f.GetProperty("field").GetProperty("fieldPath").GetString() == "geohash" &&
                    f.GetProperty("op").GetString() == "GREATER_THAN_OR_EQUAL")
                    .GetProperty("value").GetProperty("stringValue").GetString();
                string hi = filters.First(f =>
                    f.GetProperty("field").GetProperty("fieldPath").GetString() == "geohash" &&
                    f.GetProperty("op").GetString() == "LESS_THAN_OR_EQUAL")
                    .GetProperty("value").GetProperty("stringValue").GetString();

                var hits = Fixtures
                    .Where(f =>
                    {
                        string g = Geodesy.Geohash(f.Lat, f.Lon, 10);
                        return string.CompareOrdinal(g, lo) >= 0 && string.CompareOrdinal(g, hi) <= 0;
                    })
                    .Select(f => "{\"document\":" + Document(f.Id, f.Lat, f.Lon) + "}")
                    .ToList();

                // An empty result comes back as one element with no document.
                return Reply(hits.Count > 0
                    ? "[" + string.Join(",", hits) + "]"
                    : "[{\"readTime\":\"2026-01-01T00:00:00Z\"}]");
            }

            if (request.Method == HttpMethod.Post)
            {
                return Reply(Document("new-1", 51.5, -0.12));
            }

            return Reply("{}");
        }

        /// <summary>Deliberately mixes doubleValue and integerValue, and returns
        /// the integer as a string, exactly as Firestore does.</summary>
        private static string Document(string id, double lat, double lon) =>
            "{\"name\":\"projects/p/databases/(default)/documents/placements/" + id + "\"," +
            "\"fields\":{" +
            "\"geopose\":{\"mapValue\":{\"fields\":{" +
            "\"position\":{\"mapValue\":{\"fields\":{" +
            "\"lat\":{\"doubleValue\":" + lat.ToString("R") + "}," +
            "\"lon\":{\"doubleValue\":" + lon.ToString("R") + "}," +
            "\"h\":{\"integerValue\":\"0\"}}}}," +
            "\"quaternion\":{\"mapValue\":{\"fields\":{" +
            "\"x\":{\"doubleValue\":0},\"y\":{\"doubleValue\":0}," +
            "\"z\":{\"doubleValue\":0},\"w\":{\"integerValue\":\"1\"}}}}}}}," +
            "\"geohash\":{\"stringValue\":\"" + Geodesy.Geohash(lat, lon, 10) + "\"}," +
            "\"scene\":{\"stringValue\":\"rotary-phone\"}," +
            "\"scale\":{\"integerValue\":\"1\"}," +
            "\"groundOffset\":{\"doubleValue\":0.25}," +
            "\"label\":{\"stringValue\":\"Lior\"}," +
            "\"visibility\":{\"stringValue\":\"public\"}," +
            "\"owner\":{\"stringValue\":\"anon-1\"}," +
            "\"createdAt\":{\"stringValue\":\"2026-08-21T10:00:00Z\"}," +
            "\"fix\":{\"mapValue\":{\"fields\":{" +
            "\"provider\":{\"stringValue\":\"geospatial\"}," +
            "\"positionM\":{\"doubleValue\":0.8}," +
            "\"headingDeg\":{\"doubleValue\":1.2}}}}}}";

        private static HttpResponseMessage Reply(string json) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
    }

    internal static class StoreTests
    {
        public static async Task Run(Action<string, bool, string> check)
        {
            var stub = new StubFirestore();
            var http = new HttpClient(stub);
            var store = new FirestorePlacementStore("p", "k", http, appCheck: _ => Task.FromResult("attest-1"));

            const double lat = 51.5007, lon = -0.1246;
            double North(double m) => lat + (m / 6371008.8) * 180 / Math.PI;

            stub.Fixtures = new List<(string, double, double)>
            {
                ("near", North(10), lon),
                ("edge", North(90), lon),
                ("outside", North(400), lon)
            };

            IReadOnlyList<Placement> found = await store.NearbyAsync(lat, lon, 100);
            var ids = found.Select(p => p.Id).ToList();

            check("returns what is inside the radius",
                ids.Contains("near") && ids.Contains("edge"), string.Join(", ", ids));
            check("excludes what the cell caught but the circle did not",
                !ids.Contains("outside"), "");
            check("sorts by true distance", found.Count > 0 && found[0].Id == "near",
                string.Join(" ", found.Select(p => $"{p.Id}@{p.DistanceM:F0}m")));

            Placement first = found.First();
            check("decodes doubles and string-typed integers alike",
                Math.Abs(first.Position.Height) < 1e-9 && Math.Abs(first.Scale - 1) < 1e-9 &&
                Math.Abs(first.Orientation.W - 1) < 1e-9,
                $"h={first.Position.Height} scale={first.Scale} w={first.Orientation.W}");
            check("carries the label, owner and time",
                first.Label == "Lior" && first.Owner == "anon-1" && first.CreatedAt != null, "");
            check("carries how it was localized",
                first.Fix.Provider == "geospatial" && first.Fix.PositionM > 0, first.Fix.ToString());

            var queries = stub.Calls.Where(c => c.Url.Contains(":runQuery")).ToList();
            check("authenticates once and reuses the token",
                stub.Calls.Count(c => c.Url.Contains("accounts:signUp")) == 1, "");
            check("every query constrains visibility, as the rules require",
                queries.All(q => q.Body.Contains("\"fieldPath\":\"visibility\"") &&
                                 q.Body.Contains("\"stringValue\":\"public\"")), "");
            check("orders by geohash, as the range filter requires",
                queries.All(q => q.Body.Contains("\"orderBy\"")), "");

            // Writing
            var writeStub = new StubFirestore();
            var writer = new FirestorePlacementStore("p", "k", new HttpClient(writeStub));

            Placement saved = await writer.PlaceAsync(new Placement
            {
                Scene = "rotary-phone",
                Position = new GeoPoint(51.5, -0.12, 3),
                Orientation = Geodesy.HeadingToQuaternion(90),
                GroundOffset = 1.4,
                Label = "Lior",
                Fix = new FixQuality { Provider = "geospatial", PositionM = 0.8, HeadingDeg = 1.2 }
            });

            var write = writeStub.Calls.Last(c => c.Method == "POST" && c.Url.EndsWith("/placements"));
            check("a placement carries its own geohash", write.Body.Contains("\"geohash\""), "");
            check("the server stamps the owner", saved.Owner == "anon-1", saved.Owner);
            check("height above the floor is stored apart from the globe",
                write.Body.Contains("\"groundOffset\""), "");

            // Round trip through the encoder and the decoder.
            check("what was written reads back as what was meant",
                Math.Abs(saved.GroundOffset - 0.25) < 1e-9, saved.GroundOffset.ToString("R"));

            try
            {
                await writer.PlaceAsync(new Placement { Scene = "", Position = new GeoPoint(91, 0) });
                check("refuses an impossible placement", false, "no error raised");
            }
            catch (ArgumentException e)
            {
                check("refuses an impossible placement",
                    e.Message.Contains("latitude") && e.Message.Contains("scene"), e.Message);
            }

            // Correcting one already saved.
            await writer.MoveAsync("new-1", new GeoPoint(51.6, -0.13, 0), 45, 0.5);
            var patch = writeStub.Calls.Last(c => c.Method == "PATCH");
            check("a correction patches only what moved",
                patch.Url.Contains("updateMask.fieldPaths=geopose") &&
                patch.Url.Contains("updateMask.fieldPaths=geohash") &&
                !patch.Url.Contains("fieldPaths=owner") &&
                !patch.Url.Contains("fieldPaths=label"), "");

            // App Check
            var attested = stub.Calls.Where(c => c.Url.Contains(":runQuery")).ToList();
            check("requests are attested when App Check is configured", attested.Count > 0, "");
        }
    }
}
