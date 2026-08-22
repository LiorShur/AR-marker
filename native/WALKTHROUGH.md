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

## 3. Get Unity Hub and an editor

Unity Hub is a separate application that manages editor versions and projects —
it is not Unity itself, and installing it does not install an editor. Check
whether it is already here:

```bash
ls /Applications | grep -i unity
```

Nothing listed:

```bash
brew install --cask unity-hub
open -a "Unity Hub"
```

Then, inside the Hub:

1. **Sign in.** A Unity account is free and the Hub will not proceed without
   one.
2. **Preferences → Licenses → Add → Get a free personal license.** That is the
   tier you are entitled to below the revenue threshold.
3. **Installs → Install Editor → Unity 6.0 LTS.**

   The Hub will offer newer versions and mark the newest "recommended". Take
   6.0 LTS anyway. ARCore Extensions is Google's package, not Unity's, and its
   `arf6` branch was built against AR Foundation 6.0 — which is what 6.0 LTS
   ships. A newer editor brings a newer AR Foundation that Google has not
   necessarily tested against, and the failure mode is compile errors inside a
   package you cannot patch. "Recommended" means newest stable Unity; it knows
   nothing about your third-party dependencies.

   The asymmetry is the argument: choosing 6.0 and being wrong costs editor
   features this project never uses, while choosing the newest and being wrong
   costs an unfixable build. Upgrading later is one click; downgrading means
   recreating the project.
4. On the modules page, tick **iOS Build Support**. Tick **Android Build
   Support** too if you want that later — it brings its own SDK, NDK and JDK.

The editor is a large download; with iOS support it is comfortably over ten
gigabytes and takes a while. Nothing below can start until it finishes.

⚠️ Ticking the build support modules **now** is worth the disk. Adding them
afterwards means the Hub re-runs the installer, and it is the single most
common reason *Switch Platform* is greyed out later with no explanation.

## 4. Make the Unity project

**Unity Hub → New project → Universal 3D → Unity 6 LTS.**

Name it `MarkerOneApp`, location `~/AR-marker/native/unity/`.

That puts it inside the repo, which is what you want — `.gitignore` already
excludes `Library/`, `Temp/`, `Build/` and the rest, so only your own work gets
committed.

Wait for it to finish opening, and leave it open — the packages go in next.

The packages have to be installed **before** the core is copied in.
`MarkerOne.Unity.asmdef` names AR Foundation and the ARCore Extensions among
its references, and an assembly definition whose references do not exist yet
reports every type in every file as missing. Nothing is wrong when that
happens, but it looks alarming enough to send anyone hunting.

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

## 6. Copy the core in

Quit Unity first — it imports more predictably from a cold start than while
watching the folder.

```bash
cd ~/AR-marker
./native/unity/install.sh native/unity/MarkerOneApp
```

It prints what it copied. **Re-run it whenever the core changes** — the copy in
`Assets/` is what the app compiles, and the copy in `native/MarkerOne.Core/` is
what the conformance suite proves. They have to be the same file.

## 7. Google Cloud

1. **console.cloud.google.com** → pick or create a project.
2. **APIs & Services → Library** → search *ARCore API* → **Enable**.
3. Wait five minutes. Refusals before that mean nothing.
4. Unity: **Edit → Project Settings → XR Plug-in Management → ARCore
   Extensions** → *Geospatial* **enabled**, both authentication strategies
   **Keyless**.

## 8. The scene

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

## 9. Player settings

**Edit → Project Settings → Player → iOS**:

- *Other Settings → Minimum iOS Version*: `12.0`
- *Other Settings → Camera Usage Description*:
  `Shows what the camera sees, so objects can be placed in it.`
- *Other Settings → Location Usage Description*:
  `Finds where you are, so objects stay where they were left.`

Under *Android*, if you build it later: minimum **API 24**, remove **Vulkan**
from the graphics APIs, ARM64 only, IL2CPP.

## 10. Build to the iPhone

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

## 11. Committing your Unity work

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
