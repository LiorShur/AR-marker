# Niantic Spatial SDK — what it would take

Not installed. This is the assessment made before deciding whether to, written
down because the reasoning is more useful than the conclusion and because the
conclusion may change.

## Why consider it

ARCore Geospatial is bounded by Street View coverage. Measured in Cape Town:
half a metre to a metre between sessions downtown, five to fifteen on a
mountainside with nothing to see. No work on this end moves that — the input is
the limit.

NSDK localizes against scans you make yourself with Scaniverse, and reaches
roughly ten centimetres. Only where you have scanned, which is the trade: a
handful of places done properly, against everywhere done adequately.

## What it would cost

| | Here | NSDK wants |
|---|---|---|
| Unity | 6000.0.82f1 | 6000.0.74f1 |
| AR Foundation | 6.0.8 | ≥ 6.3.0, recommended 6.4.1 |
| ARCore / ARKit XR | 6.0.8 | move with AR Foundation |
| URP | 17.0.4 | a separate setup page |
| ARCore Extensions | `#arf6` | — |

The last row is the risk. `#arf6` is a branch, not a version: the build gets
whatever is on it, compiled against some AR Foundation 6.x that nobody pinned.
Moving AR Foundation three minor versions under it is the change most likely to
break the thing that currently works.

The Unity mismatch is real but softer. Niantic name one patch of the 6000.0
line and warn that others "can introduce conflicts"; that is not the same as
"will not work", and downgrading Unity has its own cost — this project already
lost a day to a Unity version fight. Try 82f1, keep 74f1 as the fallback.

## Answered: they coexist

Tested on an iPhone 12 Pro, AR Foundation 6.4.1, Unity 6000.0.82f1, NSDK
installed with its XR loader left off.

    Items   8 found, 8 shown, nearest 2.5m
    Anchor  8/8 (arcore)
    Earth   tracking ±0.5m ±2.1°
    MarkerOne: converting the placement point
    MarkerOne: converted — writing

Everything works: reads, writes, Geospatial, anchors, Convert. The version gap
in the table below is real on paper and cost nothing in practice.

An earlier run of this same experiment appeared to show the opposite — reads
returning nothing, writes vanishing, and the interface freezing on Place — and
produced three separate theories about duplicate TLS libraries, protobuf symbol
collisions and Firebase swizzling NSURLSession. All three were wrong, and each
was checked and refuted by a single command against the built binary. The
framework exports no TLS symbols, is dynamically rather than statically linked,
and contains no Firebase at all; the Firebase in the app comes from ARCore's own
iOS SDK and always did.

What the failing build actually was is not established. The most likely
explanation is that the copy had not had install.sh run into it and was
compiling older sources. Worth remembering: three plausible mechanisms, argued
from real evidence, and the answer was none of them.

## The one that decided it, before it was decided

NSDK's XR loader is "Niantic Spatial Development Kit + Apple ARKit" — a
combined loader that inserts NSDK as the AR provider. ARCore Extensions also
sits on ARKit and starts a session of its own. Both want to own it. If they
cannot share, this stops being "add a provider" and becomes "maintain two
applications", which is a different project.

## How it was found out cheaply

In a copy of the Unity project, which is gitignored and costs only disk:

1. **AR Foundation alone.** Move arfoundation, arcore and arkit to 6.4.1.
   Install nothing else. Build and run.
   - Works → continue.
   - ARCore Extensions breaks → decided, and no NSDK work would have changed it.
2. **NSDK, only then.** The loader question answers itself at runtime.

One build cycle for the answer to the whole question, and the working project
is never touched.

## What integration would look like

NSDK anchors are not coordinates. From the Unity API index:

    XRVps2AnchorPayload -- Represents the payload for a persistent anchor.
    XRVps2Subsystem.TryCreateAnchor / TryTrackAnchor / GetAnchorPayload

You create an anchor at a scanned place, receive an opaque payload, store it,
and restore the anchor from it later. Geospatial is latitude and longitude;
NSDK is a blob.

That suits the existing design rather than fighting it. A placement would gain
an optional `payload` beside its coordinates, and the renderer would prefer the
payload anchor where NSDK is localized and fall back to latitude and longitude
everywhere else. Everything already stored keeps working, every placement stays
readable by a device without NSDK, and a scanned location quietly gets ten
times the accuracy.

It is a schema change and a rules change, not only a new provider class.

## Setting it up

`https://www.nianticspatial.com/docs/nsdk/setup/?platform=unity` — and Niantic
publish a Claude Code skill that reads their live docs rather than working from
memory, which is the right way to do this. Run it locally: the docs are
unreachable from a sandboxed session, and the skill needs to read the real
Unity project.
