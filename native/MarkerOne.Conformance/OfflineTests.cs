using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using MarkerOne.Core;

namespace MarkerOne.Conformance
{
    /// <summary>
    /// Working without a network, which is where this app is most often used
    /// and least often tested.
    ///
    /// Every one of these was a real failure or a near miss. The app could not
    /// start at all in a field, because signing in was made obligatory without
    /// anybody asking what happens when it cannot be done. A queued write with
    /// no id duplicates itself on every retry. A mirror that returns more than
    /// the server would makes placements appear and disappear as the signal
    /// comes and goes.
    /// </summary>
    internal static class OfflineTests
    {
        internal static async Task Run(Action<string, bool, string> check)
        {
            // ── coming up with no signal ─────────────────────────

            var kept = new Store();
            var offline = new FirestorePlacementStore("p", "k", new HttpClient(new StubFirestore()))
            {
                Reachable = () => false,
                ReadAccount = () => "uid-7\tliorshur@gmail.com",
                ReadRefreshToken = () => "refresh-1",
                ReadOutbox = kept.ReadOutbox, WriteOutbox = kept.WriteOutbox,
                ReadMirror = kept.ReadMirror, WriteMirror = kept.WriteMirror
            };

            string token = await offline.SignInAsync();

            check("offline: no token is minted", token == null, token ?? "null");
            check("offline: says so", offline.Offline, "");
            check("offline: comes up as the remembered account",
                  offline.Signed == "liorshur@gmail.com", offline.Signed);
            check("offline: keeps the remembered uid", offline.Uid == "uid-7", offline.Uid);

            // Nobody has ever signed in on this device. The one case where
            // being offline genuinely stops the app: there is nothing to be.
            var stranger = new FirestorePlacementStore("p", "k", new HttpClient(new StubFirestore()))
            {
                Reachable = () => false,
                ReadAccount = () => ""
            };

            bool refused = false;
            try { await stranger.SignInAsync(); }
            catch (InvalidOperationException) { refused = true; }

            check("offline: a device that never signed in is refused", refused, "");

            // ── placing with no signal ───────────────────────────

            Placement made = await offline.PlaceAsync(Somewhere(51.5, -0.12));

            check("offline: a placement comes back", made != null, "");
            check("offline: named on the device", !string.IsNullOrEmpty(made.Id), made.Id);
            check("offline: owned by the remembered account", made.Owner == "uid-7", made.Owner);
            check("offline: one is waiting", offline.Waiting == 1, offline.Waiting.ToString());

            await offline.PlaceAsync(Somewhere(51.5001, -0.12));
            check("offline: two are waiting", offline.Waiting == 2, offline.Waiting.ToString());

            // Placed while out of signal, then found again in the same place.
            IReadOnlyList<Placement> near = await offline.NearbyAsync(51.5, -0.12, 100);
            check("offline: what was placed can be seen", near.Count == 2, near.Count.ToString());

            // The mirror filters the way the server does. Somewhere else is
            // somewhere else, signal or no signal.
            IReadOnlyList<Placement> far = await offline.NearbyAsync(48.85, 2.35, 100);
            check("offline: the mirror is not a free-for-all", far.Count == 0, far.Count.ToString());

            // ── the signal comes back ────────────────────────────

            var server = new StubFirestore();
            var online = new FirestorePlacementStore("p", "k", new HttpClient(server))
            {
                Reachable = () => true,
                ReadAccount = () => "uid-7\tliorshur@gmail.com",
                ReadOutbox = kept.ReadOutbox, WriteOutbox = kept.WriteOutbox,
                ReadMirror = kept.ReadMirror, WriteMirror = kept.WriteMirror
            };

            int sent = await online.FlushAsync();

            check("offline: everything waiting is sent", sent == 2, sent.ToString());
            check("offline: nothing is left waiting", online.Waiting == 0, online.Waiting.ToString());

            // The id travels with it, which is what makes a retry safe. A
            // create with no id appends a new document every time it is sent.
            int named = 0;
            foreach ((string method, string url, string _) in server.Calls)
            {
                if (method == "POST" && url.Contains("documentId=")) { named++; }
            }

            check("offline: each was sent under its own id", named == 2, named.ToString());

            check("offline: flushing an empty queue does nothing",
                  await online.FlushAsync() == 0, "");

            // ── a write that was already through ─────────────────

            // The failure this whole scheme exists for: the request arrived,
            // the acknowledgement did not, and the queue entry survived. The
            // second attempt must agree it worked rather than refusing forever.
            var twice = new Store();
            var interrupted = new FirestorePlacementStore("p", "k",
                new HttpClient(new StubFirestore { Refuse = "ALREADY_EXISTS" }))
            {
                Reachable = () => true,
                ReadAccount = () => "uid-7\tliorshur@gmail.com",
                ReadOutbox = twice.ReadOutbox, WriteOutbox = twice.WriteOutbox,
                ReadMirror = twice.ReadMirror, WriteMirror = twice.WriteMirror
            };

            twice.Outbox = kept.Outbox;
            var stale = new FirestorePlacementStore("p", "k", new HttpClient(new StubFirestore()))
            {
                Reachable = () => false,
                ReadAccount = () => "uid-7\tliorshur@gmail.com",
                ReadOutbox = twice.ReadOutbox, WriteOutbox = twice.WriteOutbox
            };
            await stale.SignInAsync();
            await stale.PlaceAsync(Somewhere(51.5, -0.12));

            int through = await interrupted.FlushAsync();
            check("offline: a document already there counts as sent", through == 1,
                  through.ToString());
            check("offline: and stops waiting", interrupted.Waiting == 0,
                  interrupted.Waiting.ToString());

            // ── ids ──────────────────────────────────────────────

            var seen = new HashSet<string>();
            for (int i = 0; i < 500; i++) { seen.Add(FirestorePlacementStore.NewId()); }

            check("offline: ids do not collide", seen.Count == 500, seen.Count.ToString());
            check("offline: ids are Firestore-shaped",
                  FirestorePlacementStore.NewId().Length == 20, "");
        }

        private static Placement Somewhere(double lat, double lon) => new Placement
        {
            Position = new GeoPoint(lat, lon, 0),
            Scene = "beacon",
            Scale = 1,
            Visibility = "public"
        };

        /// <summary>Somewhere for the host to keep things, which on a device is
        /// a file and here is a string.</summary>
        private sealed class Store
        {
            public string Outbox = "";
            public string Mirror = "";

            public string ReadOutbox() => Outbox;
            public void WriteOutbox(string it) => Outbox = it;
            public string ReadMirror() => Mirror;
            public void WriteMirror(string it) => Mirror = it;
        }
    }
}
