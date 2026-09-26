# Ghost-Hand Assembly Guide (Unity)

AR assembly guide: camera tracks an Arduino + breadboard, a 3D part shows where the next
component goes, hidden under the user's hand. Tracking is our own code (ORB/SIFT +
Lucas-Kanade + homography via OpenCvSharp), no AR SDK.

## Common base - everyone does this before splitting

1. Install **Unity 2022.3 LTS** (same version for all four - different versions corrupt scenes).
2. Clone this repo, open it in Unity, press Play. You should see the camera feed and the HUD
   showing "SEARCHING" - the stubs return nothing, that is correct.
3. Set `cameraIndex` on ARController: 0 = laptop cam, 1 = phone via Iriun/DroidCam over USB.
4. Take `Assets/StreamingAssets/reference.jpg` - one clean top-down photo of the board,
   locked focus and exposure. Commit it. Then fix the `box` values in `steps.json`.
5. Read the contracts below and confirm in the group that everyone agrees.

**No plugins needed to compile.** The interfaces use plain Unity types, so only Member 1
installs OpenCvSharp4 (NuGetForUnity) and only Member 3 installs MediaPipeUnityPlugin.

## Contracts - do not change without telling the group

| Owner | Method | Returns |
|---|---|---|
| Member 1 | `Tracker.GetHomography(Texture2D frame)` | `float[9]` row-major 3x3, or `null` if board not found |
| Member 2 | `StepManager.ShowStep(int index, float[] H)` | places the 3D part; `Hide()` clears it |
| Member 3 | `HandOcclusion.GetHandHull(Texture2D frame)` | `Vector2[]` hull in frame pixels, empty if no hand |
| Member 4 | `ARController` | owns the loop, keys, FPS |

Rules:
- **H maps `reference.jpg` pixels -> current frame pixels.** Always this direction.
- `steps.json` boxes are `[x, y, w, h]` in reference.jpg pixels.
- Frame pixel coords are **top-left origin** (Unity screen coords are bottom-left - convert, don't argue about it later).
- A module must never throw: return `null` / empty on failure.

## Who owns what

| Member | Role | Files |
|---|---|---|
| 1 | Tracking core | `Tracker.cs` |
| 2 | Steps + UI + 3D parts | `StepManager.cs`, `steps.json`, `Prefabs/` |
| 3 | Hand occlusion | `HandOcclusion.cs`, stencil shader |
| 4 | Setup + integration | `ARController.cs`, main scene, calibration, build, report |

## Scene rule
Only **Member 4** edits `Assets/Scenes/Main.unity`. Everyone else works in their own test
scene (`Scenes/Test_Member1.unity` etc.) and hands over scripts and prefabs. Unity scene
files do not merge in Git.

## Git
Branch per member: `feat/tracker`, `feat/steps`, `feat/occlusion`, `feat/integration`.
Pull before you start, PR into `main`, `main` must always Play without errors.

## Day 1 spike (one person, before we commit to this plan)
Blank Unity project -> install OpenCvSharp4 via NuGetForUnity -> install MediaPipeUnityPlugin
-> show a webcam feed with ORB keypoints drawn. If this works, we are clear. If it fights us
all day, we fall back to a Python backend + Unity frontend.
