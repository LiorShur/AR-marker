# Android

The same app, the same code, the same Firestore. What differs is entirely
platform plumbing, and most of it is scripted: **MarkerOne → Configure Android**
sets the player settings, and a build post-processor writes the manifest.

Everything below assumes iOS already works, because it is the same scene.

## 1. Unity

Install **Android Build Support** if the editor does not have it — Unity Hub →
the installed editor → *Add modules*, including the SDK, NDK and OpenJDK
sub-components. Missing those is discovered at build time rather than now.

Then, in the project:

    MarkerOne → Configure Android
    File → Build Profiles → Android → Switch Platform

It also creates `Assets/Plugins/Android`, because Google's Android Resolver
copies a Gradle template in there with `File.Copy`, which does not create
directories. On a project that has never built for Android the folder does not
exist and the resolver throws a `DirectoryNotFoundException` from inside itself
— accurate, and no help at all in saying the fix is one empty folder.

The menu item sets four things:

| | Value | Why |
|---|---|---|
| Graphics API | **OpenGLES3 only** | ARCore does not support Vulkan on every device, and Unity puts Vulkan first. A Vulkan build shows a black camera feed with no error. |
| Minimum API | 24 | ARCore's floor. Newer editors enforce higher; use what yours accepts. |
| Scripting backend | IL2CPP | Required for ARM64. |
| Architecture | ARM64 | What ARCore needs. |

The graphics API is the one that matters. It is the Android equivalent of the
missing URP renderer feature that gave a yellow screen on iOS — the same class
of failure, the same absence of any error explaining it.

## 2. XR Plug-in Management

**Edit → Project Settings → XR Plug-in Management → Android** → tick
**Google ARCore**.

## 3. ARCore Extensions authentication

**XR Plug-in Management → ARCore Extensions → Android Authentication Strategy.**

Unlike iOS, Android offers **Keyless**, which authenticates through Google Play
services and needs no key in the app at all. It is the better option, and it
costs a step: the OAuth client must know the signing certificate's SHA-1.

    keytool -list -v -keystore <your-keystore> -alias <your-alias>

Then in **Google Cloud → Credentials → Create credentials → OAuth client ID →
Android**, with the package name and that SHA-1.

**API Key** also works and is simpler. If you use it, note that the key
restricted to your iOS bundle id will not work here: an API key is restricted
per platform, so either add an Android restriction (package name + SHA-1) to the
existing key or make a second one.

⚠️ Debug and release builds are signed with different certificates, so a key or
OAuth client registered against one will fail for the other. The symptom is
`ErrorNotAuthorized` and no Geospatial at all.

## 4. The manifest

Written by `MarkerOneAndroidPostprocess` on every build. Nothing to do, but
worth knowing what it adds and why:

**Location permissions.** Geospatial needs location. On iOS the app asks and
that is enough; on Android an unrequested permission does not exist, and Unity
only adds the entry when it notices the API being used — which it does not
reliably do through an assembly definition. Without it: Earth enabled, never
tracking, nothing saying why. This project already spent a day on exactly that
failure once.

**The OAuth redirect.** Google returns the sign-in code by opening a URL, and
Android delivers it only to an activity that has claimed the scheme. The filter
goes on whichever activity carries the LAUNCHER category — found by looking
rather than by name, because which activity that is has changed between Unity
versions.

## 5. Signing

**Player Settings → Publishing Settings → Keystore Manager** to make one, or
point at an existing keystore. A debug build will install without it, but
remember §3: the certificate is part of what authenticates ARCore.

## 6. Build and run

Developer options and USB debugging on the phone, then **File → Build and Run**.

## What to expect

The same readout, and mostly better numbers. ARCore is Google's own platform
here — no cross-platform shim, no Swift package, no CocoaPods — so Geospatial
tends to acquire faster and track more steadily than it does on iOS.

Two differences worth knowing:

- **Depth and occlusion** need a device that supports the ARCore Depth API.
  Most recent phones do; some do not, and there `AROcclusionManager` simply
  reports nothing rather than failing. The `Depth on/off` button will have
  nothing to switch.
- **No LiDAR**, so depth is inferred from motion rather than measured. Planes
  and depth raycasts are a little less reliable on featureless ground than they
  are on the iPhone.

## A namespace clash on newer AR Foundation

    Namespace 'com.google.ar.core' is used in multiple modules and/or libraries:
      :arcore_client:, :unityandroidpermissions:

Both AARs come from Google and Unity — ARCore Extensions ships one
`arcore_client.aar` and `com.unity.xr.arcore` ships another alongside
`unityandroidpermissions.aar`. Newer Android Gradle Plugin requires unique
namespaces and refuses to merge them.

Seen on AR Foundation 6.4.1 and not on 6.0.8. See [NSDK.md](NSDK.md) for what is
and is not established about why.

## What is not done

**Google sign-in is untested on Android.** The redirect filter is written and
the flow is the same, but nobody has run it. If it fails, the device identity
still works and everything except ownership is unaffected.
