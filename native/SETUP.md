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

Add **AR Plane Manager** to the XR Origin if you want `FloorProbe` to learn the
floor from detected planes as well as from taps.

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

## 7. Building to the iPhone

1. **File → Build Settings → iOS → Switch Platform**.
2. **Build** to a folder — Unity produces an Xcode project, not an app.
3. Open the generated `.xcworkspace`, **not** the `.xcodeproj`. ARCore arrives
   through CocoaPods and only the workspace has it.
4. Signing → your team. A free Apple ID works for a seven-day build; your paid
   account gives a year.
5. Run to the device.

⚠️ **The workspace, not the project.** Opening the `.xcodeproj` gives linker
errors about missing ARCore symbols, which look like a broken package and are
not.

⚠️ If CocoaPods has not run, `pod install` in the build folder. Unity usually
does this itself, and silently does not when CocoaPods is not on the PATH that
the Unity process inherited — which on a Mac with Homebrew Ruby is common.

## 8. Knowing whether it works

Geospatial needs a view of the sky and VPS coverage to do its best work.
Indoors it will localize and be honest that the result is poor.

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
