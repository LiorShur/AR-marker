# From this repo to an app on your phone

Every command, in order. `SETUP.md` explains *why* each step is what it is and
lists the failure modes; this is the shortest path through.

**Do this on the Mac.** iOS builds need Xcode, and Xcode only exists there.
Android could be done on either, but keeping one machine avoids a whole class of
"works on the laptop" confusion.

**Paste one line at a time.** Nothing below carries a trailing comment, and it
should stay that way: zsh does not treat `#` as a comment interactively unless
`interactive_comments` is set, so a `# note with an apostrophe` does not become
a note — it becomes an unterminated quote and a `quote>` prompt. `Ctrl-C` if
that happens.

---

## 1. Get the repo onto the Mac

```bash
cd ~
git clone https://github.com/LiorShur/AR-marker.git
cd AR-marker
git checkout claude/ar-web-app-setup-417jgy
```

If you already cloned it there:

```bash
cd ~/AR-marker
git checkout claude/ar-web-app-setup-417jgy
git pull origin claude/ar-web-app-setup-417jgy
```

## 2. Check the core is sound before building anything on top of it

Optional, and worth the two minutes.

Install the .NET SDK if you do not have it:

```bash
brew install --cask dotnet-sdk
```

Then:

```bash
cd ~/AR-marker
dotnet run --project native/MarkerOne.Conformance -- native/vectors/core.json
```

Expect `1527/1527 vectors matched`. If that passes, every coordinate transform
the app depends on is proven before a single line of Unity exists, and anything
that goes wrong from here is in the Unity layer.

Homebrew installs the newest SDK, which as of writing is .NET 10, and a major
runtime does not carry its predecessors. The project targets net8.0 with
`RollForward` set to `LatestMajor` so it runs on whatever is installed. If you
are on an older checkout and see *"You must install or update .NET"* after a
build that plainly succeeded, that is what it means — pull, or run it with
`DOTNET_ROLL_FORWARD=LatestMajor`.

## 3. Make the Unity project

**Unity Hub → New project → Universal 3D → Unity 6 LTS.**

Name it `MarkerOneApp`, location `~/AR-marker/native/unity/`.

That puts it inside the repo, which is what you want — `.gitignore` already
excludes `Library/`, `Temp/`, `Build/` and the rest, so only your own work gets
committed.

Wait for it to finish opening, then quit Unity for the next step.

## 4. Copy the core in

```bash
cd ~/AR-marker
./native/unity/install.sh native/unity/MarkerOneApp
```

It prints what it copied. **Re-run it whenever the core changes** — the copy in
`Assets/` is what the app compiles, and the copy in `native/MarkerOne.Core/` is
what the conformance suite proves. They have to be the same file.

## 5. Packages

Reopen the project. **Window → Package Manager**, then:

- *Unity Registry* → **AR Foundation** → Install
- *Unity Registry* → **Google ARCore XR Plugin** → Install
- *Unity Registry* → **Apple ARKit XR Plugin** → Install
- **+** (top left) → *Add package from git URL* → paste:

```
https://github.com/google-ar/arcore-unity-extensions.git#arf6
```

Then **Window → TextMeshPro → Import TMP Essential Resources**.

⚠️ The `#arf6` is load-bearing. Without it you get the AR Foundation 5 branch
and a wall of compile errors that read like a broken install.

**Edit → Project Settings → XR Plug-in Management**: tick **Google ARCore**
under *Android*, **Apple ARKit** under *iOS*.

## 6. Google Cloud

1. **console.cloud.google.com** → pick or create a project.
2. **APIs & Services → Library** → search *ARCore API* → **Enable**.
3. Wait five minutes. Refusals before that mean nothing.
4. Unity: **Edit → Project Settings → XR Plug-in Management → ARCore
   Extensions** → *Geospatial* **enabled**, both authentication strategies
   **Keyless**.

## 7. The scene

**GameObject → XR → AR Session**, then **GameObject → XR → XR Origin (Mobile
AR)**. Delete the default `Main Camera` that came with the scene — the XR Origin
brings its own, and two cameras tagged MainCamera is a confusing afternoon.

Add an empty GameObject called `Marker One`, and on it:

| Component | Set |
|---|---|
| `MarkerOneRig` | *Project Id*, *Api Key* — the same values as `spatial/config.local.js`. *Scenes*: two entries, `rotary-phone` and `beacon`, each with a prefab. *Floor*: the FloorProbe below. |
| `FloorProbe` | — |
| `AREarthManager` | from ARCore Extensions |
| `GeospatialFixSource` | *Rig* → the MarkerOneRig |

Also add **ARCore Extensions** (the component) to any object in the scene, and
**AR Plane Manager** to the XR Origin.

For the prefabs, anything visible will do to start — a cube a third of a metre
across proves the pipeline as well as a model does, and rules out the model
being the problem.

## 8. Player settings

**Edit → Project Settings → Player → iOS**:

- *Other Settings → Minimum iOS Version*: `12.0`
- *Other Settings → Camera Usage Description*:
  `Shows what the camera sees, so objects can be placed in it.`
- *Other Settings → Location Usage Description*:
  `Finds where you are, so objects stay where they were left.`

Under *Android*, if you build it later: minimum **API 24**, remove **Vulkan**
from the graphics APIs, ARM64 only, IL2CPP.

## 9. Build to the iPhone

**File → Build Settings → iOS → Switch Platform**, then **Build**. Choose
`~/AR-marker/native/unity/iOSBuild` — already gitignored.

```bash
cd ~/AR-marker/native/unity/iOSBuild
open Unity-iPhone.xcworkspace
```

⚠️ **The `.xcworkspace`, not the `.xcodeproj`.** ARCore arrives through
CocoaPods and only the workspace includes it. Opening the project gives linker
errors that look like a missing package.

If there is no workspace, CocoaPods did not run:

```bash
cd ~/AR-marker/native/unity/iOSBuild
pod install
```

In Xcode: select the `Unity-iPhone` target → *Signing & Capabilities* → your
team. Plug the phone in, pick it as the destination, **⌘R**.

## 10. Committing your Unity work

```bash
cd ~/AR-marker
git status
git add native/unity/MarkerOneApp
git commit -m "Unity project"
git push origin claude/ar-web-app-setup-417jgy
```

`git status` should not list `Library/` or `Temp/`.

If `git status` shows thousands of files, the Unity ignores are not being
applied — check you are in the repo root and that `.gitignore` contains the
`[Ll]ibrary/` block.

## When it runs

Outdoors, with a view of the sky. The rig's state goes `Locating` → `Ready`,
usually within a few seconds, and `Session.Frame.Fix` reads something like
`±0.8m ±1° via geospatial/direct`.

Against the web version's 5–10 m and ±25°, that difference is the entire reason
for doing this.
