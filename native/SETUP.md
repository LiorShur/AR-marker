# Setting up the Unity project

Written for Unity 6 LTS, an iPhone 12 Pro and a Mac with Xcode. Android is the
same until step 6.

Take the steps in order. Several of them fail in ways that look like something
else entirely, and each of those has a note saying so.

---

## 1. The project

New project, **Universal 3D** template, Unity 6 LTS.

**Window → Package Manager**, and install in this order:

| Package | How |
|---|---|
| **AR Foundation** | Unity Registry |
| **Google ARCore XR Plugin** | Unity Registry |
| **Apple ARKit XR Plugin** | Unity Registry |
| **ARCore Extensions** | `+` → *Add package from git URL* → `https://github.com/google-ar/arcore-unity-extensions.git#arf6` |

⚠️ **The `#arf6` matters.** Without it you get the branch built for AR
Foundation 5, and it fails at compile time with errors that read like a broken
install rather than a version mismatch. Use `#arf5` only if you deliberately
pinned AR Foundation 5.

Then **Edit → Project Settings → XR Plug-in Management**:

- *Android* tab → tick **Google ARCore**
- *iOS* tab → tick **Apple ARKit**

## 2. Google Cloud, and the ARCore API

The Geospatial API's runtime is free, but it does have to be switched on, and
it authenticates.

1. **console.cloud.google.com** → the project you want billing and quota
   attributed to. It can be the same project as Firebase; nothing requires it.
2. **APIs & Services → Enable APIs** → enable **ARCore API**.
3. In Unity: **Edit → Project Settings → XR Plug-in Management → ARCore
   Extensions**.
4. Set **Geospatial** to enabled.
5. **Android authentication**: **Keyless**. It signs with the app's own signing
   identity rather than shipping a key in the binary.
6. **iOS authentication**: **API Key**. Keyless does not exist on iOS — it works
   by registering an Android signing certificate with Google, and iOS has no
   equivalent. The alternative, an Authentication Key, is a service-account JWT
   and needs a token server to be worth anything.

   Create it in **APIs & Services → Credentials → Create Credentials → API
   key**, then restrict it: *Application restrictions* → iOS apps → your bundle
   id, and *API restrictions* → ARCore API only. An unrestricted key is a bill
   waiting to happen. It is a different key from the Firebase web one, even
   though both live in the same Cloud project.
7. Tick **iOS Support Enabled** in the same panel. Without it the build
   succeeds, installs and runs, and EarthState never leaves
   ErrorGeospatialModeDisabled — because the ARCore iOS pod was never added.

⚠️ **Keyless on Android needs the signing certificate registered.** A debug
build signed with the debug keystore and a release build signed with yours are
two different identities to Google. If Geospatial returns
`EarthState.ErrorAPIKeyInvalid` on a build that worked yesterday, this is almost
always why.

⚠️ **Enabling the API takes a few minutes to propagate.** A refusal in the first
five minutes after enabling means nothing.

## 3. The core, and assembly definitions

```
Assets/
  MarkerOne/
    Core/                  ← copy native/MarkerOne.Core/*.cs here
      MarkerOne.Core.asmdef
    Unity/                 ← copy native/unity/MarkerOne/*.cs here
      MarkerOne.Unity.asmdef
```

Both `.asmdef` files are in `native/unity/MarkerOne/`. Copy each into the
directory it belongs to.

The Core one sets **`noEngineReferences: true`**, which is the point of the
whole arrangement: it makes the compiler refuse any accidental `using
UnityEngine` in the tested half. If someone adds one later, it fails to build
here rather than quietly becoming untestable.

Keep `native/MarkerOne.Core/` as the source of truth and copy from it, so
`dotnet run --project native/MarkerOne.Conformance` keeps proving the same code
the app runs. It is not much use proving a copy that has since diverged.

`PlacementCaption.cs` needs **TextMeshPro** — *Window → TextMeshPro → Import
TMP Essential Resources*. Skip the file if you would rather draw captions
another way.

## 4. URP needs the AR background feature

**This is why the camera feed is missing and the screen is a flat colour.**

The Universal Render Pipeline does not draw the camera passthrough by itself.
AR Foundation supplies a renderer feature that does, and installing the package
does not add it — it has to be put on the renderer by hand.

1. **Edit → Project Settings → Graphics** → note which *Render Pipeline Asset*
   is in use.
2. Find that asset in the Project window, and the **Universal Renderer Data**
   asset it points at — usually `PC_Renderer` or `Mobile_Renderer` beside it.
3. Select the renderer data asset → **Add Renderer Feature** → **AR Background
   Renderer Feature**.

Without it ARKit runs, tracking works, planes are found, and the screen shows
the camera's clear colour instead of the world — which looks like a broken
build rather than a missing render feature.

## 5. The scene

```
AR Session
XR Origin (Mobile AR)
  Camera Offset
    Main Camera            ← tagged MainCamera
ARCore Extensions          ← the component from the Extensions package
Marker One
  MarkerOneRig
  FloorProbe
  AREarthManager
  GeospatialFixSource
```

### MarkerOne → Set up scene

Run this menu item once. It creates a prefab and a material per scene id in
`Assets/MarkerOne/Prefabs`, fills in the rig's *Scenes* list, and adds
**AR Raycast Manager** and **AR Plane Manager** to the XR Origin.

All of that can be done by hand, and each step has a way of appearing to
succeed without succeeding. A prefab field can be set to a *scene object*
rather than an asset — it looks identical in the inspector and empties itself
the moment that object is deleted. A drag into the Project window can fail to
create anything at all, silently. A manager added to the wrong GameObject does
nothing and says nothing.

The menu item is not a shortcut past understanding the scene. It is a refusal
to keep losing to the same ambiguity.

Afterwards, *Scenes* on the rig should list one entry per id in `content.json`,
each pointing at something under `Assets/`. Verify from a terminal if the
inspector has misled you before:

```bash
ls Assets/MarkerOne/Prefabs
```

### ARCore Extensions

This one is easy to leave out, because nothing complains at edit time and the
build succeeds. Create an empty GameObject, add the **ARCore Extensions**
component, and fill in all three references:

| Field | Value |
|---|---|
| Session | the `AR Session` GameObject |
| Origin | the `XR Origin` GameObject |
| Camera Manager | the camera carrying `ARCameraManager` |
| Config | the `ARCoreExtensionsConfig` asset, with *Geospatial Mode* Enabled |

**Origin is not optional**, however much it looks it. Localization works
without it — fixes arrive, accuracy converges, placements render. It is read by
exactly one line, at the end of `AddAnchor`:

```csharp
anchor.transform.SetParent(ARCoreExtensions._instance.Origin.TrackablesParent, false);
```

So an empty Origin is a `NullReferenceException` thrown from inside the package
at the moment the first geospatial anchor is created, long after anything that
could have explained it. `MarkerOne → Set up scene` fills it in.

Without it, `AREarthManager.EarthState` throws a `NullReferenceException`
rather than reporting a state — it dereferences a session that was never
started. `GeospatialFixSource` catches that and says so, but the app otherwise
looks like a working AR build that simply never finds anything.

Worth verifying from a terminal rather than from the Inspector, since a
reference can be silently emptied by deleting the object it pointed at:

```bash
G=$(grep -m1 guid Library/PackageCache/com.google.ar.core.arfoundation.extensions*/Runtime/Scripts/ARCoreExtensions.cs.meta | awk '{print $2}')
grep -A12 "$G" Assets/Scenes/SampleScene.unity
```

A block should come back, and none of its references should read
`{fileID: 0}`, which is how Unity serializes "empty".

On **MarkerOneRig**:

- *Project Id* and *Api Key* — the same Firebase values the web app uses. The
  key is not a secret; `firestore.rules` is what protects the data.
- *Scenes* — one entry per scene id in `content.json`: `rotary-phone`,
  `beacon`. Each points at a prefab.
- *Floor* — the `FloorProbe`.

On **GeospatialFixSource**, set *Rig* to the `MarkerOneRig`.

## 6. Player settings

| | Android | iOS |
|---|---|---|
| Minimum version | API 24 | 12.0 |
| Graphics API | remove Vulkan, leave OpenGLES3 | Metal |
| Scripting backend | IL2CPP, ARM64 only | IL2CPP |
| Camera usage | — | *"Shows what the camera sees, so objects can be placed in it."* |
| Location usage | — | *"Finds where you are, so objects stay where they were left."* |

⚠️ ARCore does not support Vulkan on all devices. Leaving it first in the list
produces a black camera feed with no error.

⚠️ **Location usage is not optional on iOS.** Leave the description empty and
iOS terminates the app the instant it asks for the permission — not a dialog,
not a warning, the process is killed. It is under *Player Settings → Other
Settings → Location Usage Description*, and it is worth confirming it survived
into the build:

```bash
grep -A1 NSLocationWhenInUseUsageDescription iOSBuild/Info.plist
```

### Why location, when ARKit never asks for it

Geospatial needs it and ARCore does not request it; ARKit does not need it and
so never prompts. Nothing asks unless the app does, and the failure is quiet:
the session fails to configure with `ErrorLocationPermissionNotGranted`, Earth
stays at `ErrorEarthNotReady`, and what you see is an AR view that tracks
perfectly and never finds anything — indistinguishable from standing somewhere
with no VPS coverage.

`GeospatialFixSource` handles this: it starts the location service, waits for
the answer, and then cycles the `ARCoreExtensions` component. That last part
matters. Extensions configures its session once, early, and that attempt has
already failed by the time anyone taps Allow — without the cycle, granting
permission changes nothing until the app is restarted.

## 7. Building to the iPhone

1. **File → Build Settings → iOS → Switch Platform**.
2. **Build** to a folder — Unity produces an Xcode project, not an app.
3. Open the generated `.xcworkspace`, **not** the `.xcodeproj`.
4. Signing → your team. A free Apple ID works for a seven-day build; your paid
   account gives a year.
5. Run to the device.

Choose **Replace** rather than **Append** whenever packages, player settings or
plugins have changed. Append refreshes Unity's own output and leaves the
dependency wiring as it found it. Append is fine for script and asset edits.

### How ARCore actually reaches the build

Extensions 1.54 ships its iOS dependency as a **Swift package**, not a pod:

```xml
<remoteSwiftPackage url="https://github.com/google-ar/arcore-ios-sdk.git" version="1.54.0">
    <swiftPackage name="ARCoreGeospatial" replacesPod="ARCore/Geospatial"/>
```

`replacesPod` is the operative word. The External Dependency Manager adds the
package to the Xcode project and deliberately leaves the Podfile alone, so
**the generated Podfile has empty targets and that is correct**. It is a
convincing false lead when hunting a missing ARCore session — the reflex is to
read the Podfile, find nothing, and conclude iOS support is off.

What actually tells you ARCore is in the build:

```bash
grep -c ARCoreGeospatial Unity-iPhone.xcodeproj/project.pbxproj
```

Non-zero means linked. Zero means the resolver did not run, and then the
Podfile is worth looking at — check *Assets → External Dependency Manager →
iOS Resolver → Settings*.

Settings themselves can be read without the GUI:

```bash
cat ProjectSettings/ARCoreExtensionsProjectSettings.json
```

`IsIOSSupportEnabled` and `GeospatialEnabled` should both be `true`, and
`IOSAuthenticationStrategySetting` should be `2` for API Key — iOS has no
Keyless option.

## 8. Knowing whether it works

Geospatial needs a view of the sky and VPS coverage to do its best work.
Indoors it will localize and be honest that the result is poor. Which means
every real test happens outdoors, and reading the state from a tethered Mac
turns each one into a trip to the car park with a laptop.

So the state is on the phone. `MarkerOneHud` installs itself after the scene
loads — there is nothing to add to the scene and nothing to wire — and draws:

```
AR     SessionTracking
Earth  enabled, waiting for a fix
Rig    Locating
Fixes  3
Fix    ±0.8m ±1° via geospatial/direct
Items  4
```

plus the last few warnings, which is usually the line that explains the other
six. `×` hides it; the `state` button brings it back. To leave it out of a
build, add `MARKERONE_NO_HUD` to *Player Settings → Scripting Define Symbols*.

Placement is on the same principle — `PlacementInput` installs itself too. A
crosshair at the centre of the screen, and a bar along the bottom with the
scene id, a name field and **Place**. The crosshair turns green on a detected
surface and the bar reads `surface · 1.4m`; white and `mid-air` means no plane
was found and the object will hang where the reticle is.

Aiming with the phone rather than tapping is deliberate: it is steadier at
arm's length, and a fixed reticle can say what it is over *before* anything is
committed. A tap that silently lands on nothing is the most confusing thing an
AR app can do.

The first two lines localize the common failures without a debugger:

| Line | Means |
|---|---|
| `AR` stuck below `SessionTracking` | ARKit has not started — camera permission, or no `ARSession` in the scene |
| `Earth  no GeospatialFixSource in scene` | The component was never added |
| `Earth  ARCore Extensions has not started a session…` | The `ARCoreExtensions` component is missing or unassigned — see §5 |
| `Earth  Geospatial unavailable: <state>` | Reached ARCore and was refused; the detail names which setting |
| `Earth  fix too poor to use: ±40m` | Working, but indoors or without sky |
| `Rig    no session — check Project Id and Api Key` | Firebase fields empty on `MarkerOneRig` |

If you do want the console, you do not need the cable: **Window → Devices and
Simulators → Connect via network** keeps Xcode attached over Wi-Fi. Out of
range, `log collect --device --last 30m` retrieves what the device recorded
while you were away.

The rig reports its state. Wire `StateChanged` to a label:

| State | Meaning |
|---|---|
| `Locating` | No usable fix yet |
| `Calibrating` | A position but no bearing — will not happen with Geospatial, which reports one directly |
| `Ready` | `Session.Frame.Fix` carries both accuracies |
| `Error` | The detail says what; a failed read is not an empty world |

`Session.Frame.Fix.ToString()` gives `±0.8m ±1° via geospatial/direct`. Outdoors
in a covered area expect sub-metre and about a degree — against the 5–10 m and
±25° the web version manages, which is the entire reason for going native.

## What is proven and what is not

`MarkerOne.Core` has 1527 assertions behind it, run by
`dotnet run --project native/MarkerOne.Conformance`, including a check that the
C# reproduces the JavaScript exactly on 274 generated cases.

`native/unity/` has none. It cannot be compiled without Unity, let alone run
without a device, so it is written to be as thin as possible — read a pose,
hand it over, instantiate what comes back — with everything that could be
subtly and silently wrong kept on the other side of the line.

Expect to fix something here. The one I would look at first is
`GeospatialFixSource.SessionYaw`: EunRotation is East-Up-North and Unity is
East-Up-South, and a sign error there puts the world 180° out in a way that
looks exactly like a compass problem. If everything appears mirrored or
reversed, that method is the reason.
