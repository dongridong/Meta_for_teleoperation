# Hand Landmark + Passthrough Visualization — Setup Guide

This adds hand-tracking visualization on top of Quest 3 passthrough:

- **Passthrough** background (camera feed).
- **21 MediaPipe landmarks** per hand — spheres (joints) + lines (bones), colored per hand
  (right = cyan, left = magenta).
- **Palm middle pose** drawn as a coordinate frame (X = red, Y = green, Z = blue, 5 cm axes).

All poses are computed in **unity_world** (left-handed, Y-up, quaternion order x,y,z,w).

> Scope: this task is **visualization only**. No ZMQ / networking. The calculation code
> (`HandLandmarkProvider`) is deliberately separated from rendering so it can be reused by the
> future publish stage.

---

## 1. Files added

```
Assets/Scripts/HandTrackingViz/
  HandTrackingViz.asmdef      # assembly def; references Unity.XR.Hands
  HandLandmarkData.cs         # data types + MediaPipe<->OpenXR mapping + bone list
  HandLandmarkProvider.cs     # REUSABLE calc: OpenXR joints -> unity_world landmarks + palm pose
  HandVisual.cs               # rendering of one hand (spheres, bones, palm axes)
  HandVisualizer.cs           # MonoBehaviour that ties calc + rendering together
  PassthroughSetup.cs         # makes the camera transparent for passthrough
```

`Packages/manifest.json` was updated with the XR packages listed below.

---

## 2. Install / verify packages

Open **Window ▸ Package Manager** and confirm these resolve (versions may auto-adjust to what is
compatible with Unity 6000.2.12f1 — that is expected, let Package Manager pick):

- `com.unity.xr.openxr` (OpenXR Plugin)
- `com.unity.xr.hands` (XR Hands — OpenXR hand tracking, provides `XRHandJointID.Palm`)
- `com.unity.xr.arfoundation` (AR Foundation)
- `com.unity.xr.meta-openxr` (Unity Meta OpenXR — Quest passthrough support)
- `com.unity.xr.management`

If a version in `manifest.json` fails to resolve, delete just that version string and install the
package from Package Manager; it will write the correct version back.

---

## 3. XR Plug-in Management (Project Settings)

**Edit ▸ Project Settings ▸ XR Plug-in Management**

1. Install XR Plug-in Management if prompted.
2. Under the **Android** tab, enable **OpenXR**.
3. Open **XR Plug-in Management ▸ OpenXR** (Android tab):
   - **Interaction Profiles**: add *Oculus Touch Controller Profile* (or your preferred profile).
   - **OpenXR Feature Groups**: enable **Meta Quest** (from `com.unity.xr.meta-openxr`).
   - **Enabled features** — turn ON:
     - **Hand Tracking Subsystem** (Unity XR Hands / `XR_EXT_hand_tracking`).
     - **Meta Hand Tracking Aim** (optional).
     - **Meta Quest: Passthrough** (or "Passthrough" feature under the Meta group).
4. Resolve any yellow/red validation issues via the **Fix All** button in the OpenXR settings.

---

## 4–6. Automated setup (one click)

Editor automation was added under `Assets/Editor/HandTrackingViz/`. After the packages import
(section 2), use the menu:

- **Tools ▸ Hand Tracking Viz ▸ Setup Everything (Scene + Android)** — does both steps below, or run
  them individually:
  - **1. Setup Scene** — creates the XR Origin rig (HMD-tracked camera), makes the camera transparent
    for passthrough (adds `PassthroughSetup`), removes the stray default Main Camera, and creates a
    `HandVisualizer` with **Tracking Origin** already wired to the XR Origin.
  - **2. Configure Android Player Settings** — switches platform to Android, sets IL2CPP + ARM64,
    min SDK 32, and Linear color space.

The hand-tracking Android permission (`com.oculus.permission.HAND_TRACKING`) and the
`oculus.software.handtracking` feature are injected automatically at build time by
`HandTrackingHandPermissionPostprocessor` — no manual AndroidManifest editing needed.

`HandVisualizer` defaults: right = cyan, left = magenta, **Axis Length** = 0.05 (5 cm),
**Palm Pose Source** = `OpenXRPalmJoint` (uses `XR_HAND_JOINT_PALM_EXT`; switch to `WristAndMiddleMcp`
for the wrist+middle-MCP definition). Spheres, bone lines and palm axes are created procedurally, so
nothing else needs wiring.

`HandVisualizer` is compiled out of the controller APK (`-handVisualization off`, see
`Assets/Editor/BuildAndroid.cs`): hand tracking stays on there, so without it the skeleton would
appear whenever a controller is put down. That switch affects rendering only — `PoseFramePublisher`
publishes the hands identically in both builds.

> Still manual (Unity has no stable public API for it): enable the OpenXR features in
> **Project Settings ▸ XR Plug-in Management ▸ OpenXR (Android)** — the **Meta Quest** feature group,
> **Hand Tracking Subsystem**, and **Passthrough**. See section 3.

---

## 7. Coordinate-system notes (for future publish)

- Everything the provider outputs is in **unity_world**: left-handed, Y-up.
- Rotations are Unity `Quaternion`, memory order **(x, y, z, w)** — the same order the publisher expects.
- Landmarks follow the **MediaPipe 21** ordering (see `HandLandmark` enum). The OpenXR→MediaPipe
  joint mapping and the bone-connection list live in `HandLandmarkMapping`.
- Palm pose definition is documented inline in `HandLandmarkProvider.PalmPoseSource`.
- The palm coordinate frame is drawn in unity_world, so you can visually compare it against whatever
  pose the publish stage emits later.

To reuse for publishing later: call `HandLandmarkProvider.TryUpdate(side, trackingOrigin, frame)`
and read `frame.positions` / `frame.rotations` / `frame.palmPose` — no rendering involved.
