# Working without a signal

The app is used in places that do not have one. This is how it behaves there.

## No hardwired password, and none needed

A credential compiled into the app would not work and would not be safe.

It would not work because Firebase mints tokens on its servers: a password,
hardwired or typed, needs a round trip to `accounts:signInWithPassword`, and out
of coverage a correct password gets you exactly as far as no password.

It would not be safe because a secret in an app binary is not a secret. `strings`
on the IPA finds it, and it would be an admin credential for everybody who
installs the app.

**What is needed is already on the device.** Signing in once leaves the uid, the
account name and a refresh token in local storage. Out of coverage the app does
not need to authenticate — it needs to remember who it is, which it does. It
comes up as that account, marked offline.

That is not a security decision. The uid only decides what this app offers; the
rules judge every write when it eventually lands, so trusting a name kept on the
device costs nothing. Refusing to trust it cost the whole app: signing in was
made obligatory without anybody asking what happens when it cannot be done, and
the launch screen blocked for ever in a field.

The one case where being offline genuinely stops the app is a device nobody has
ever signed in on. There is nothing to be.

## Placing

Placements queue to a file and go when there is something to send them over.

Each is named on the device before it is stored, which is what makes a retry
safe. A create with no id appends a new document every time it is sent, so a
flush cut off between the request and the acknowledgement duplicates everything
it had already done. A create with an id is a put: sending it twice leaves one
document. A document that already exists therefore counts as sent, which is not
leniency but the whole point of the scheme.

⚠️ **An offline placement is inaccurate, and permanently so.** Geospatial needs
the network for VPS, so out of coverage the coordinates come from GPS alone —
±5–15 m rather than ±0.5 m. GPS itself works offline, so the placement happens;
it is just coarse, and it does not improve when the signal returns, because what
was written was written. The bar says `no visual fix — will place from GPS`
before you commit to it.

Something queued can be deleted while still queued. Something already sent
cannot, because deleting it needs the server's permission and showing it gone
would only mean watching it come back.

## Seeing

Everything read is kept, and offline the same query runs against what is kept.

The geohash bounds are applied as well as the distance, so a cached answer and a
served one contain the same placements. A mirror that quietly returned more than
the server would is one that makes things appear and disappear as the signal
comes and goes.

**Preloading is the same mechanism.** Somewhere visited while connected is
somewhere that works offline afterwards, so walking an area once with a signal is
the whole of "download this area".

An empty world and an unreachable one used to look identical, which is the worst
of it — and far commoner in exactly the places this gets used.

## On screen

The account chip says `offline` and how many placements are waiting, because the
readout starts shut and this is otherwise invisible: nothing new appears, nothing
is saved, and the world looks empty rather than unreachable.

## What is proven

The whole of it, against a stub Firestore — coming up on a remembered account,
refusing a device that never signed in, queueing, flushing, the mirror filtering
the way the server does, an interrupted write counting as sent, and that
generated ids do not collide. See `OfflineTests.cs`.
