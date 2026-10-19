# Ghost-Hand: A Vision-Based AR Assembly Guide for Arduino Circuits

Ghost-Hand helps a beginner build a circuit on a breadboard. A phone camera watches an Arduino and a breadboard, and a Unity app draws the next step on the live video: a 3D part, rings on the exact holes, labels and wire arrows. The parts hide under the user's hand. Tracking is our own code (ORB, RANSAC homography and Lucas-Kanade optical flow with OpenCvSharp), and no AR SDK such as Vuforia or ARCore is used.

Group 10 | Virtual Reality / Image Processing CA-3 | B.Tech CSE, Symbiosis Institute of Technology, Pune
Faculty guide: Dr. Shwetambari Chivane

![Step 1: LED](docs/images/step1.jpg)

## Features

- Finds and follows the board using ORB matching, a RANSAC homography and Lucas-Kanade optical flow
- Draws 3D parts, target rings, labels and wire arrows on the exact holes, one step at a time
- Hides the virtual parts under the user's hand (skin colour test plus a difference from the reference photo)
- Moves to the next step automatically when the hand places a part and leaves (A key turns this on or off)
- Runs at about 30 FPS, locks onto the board in under 2 s and recovers in about 2 s

| Step 3: jumper wire              | Hand occlusion                          | Circuit complete                      |
| -------------------------------- | --------------------------------------- | ------------------------------------- |
| ![Step 3](docs/images/step3.jpg) | ![Hand](docs/images/hand-occlusion.jpg) | ![Complete](docs/images/complete.jpg) |

## The circuit

A blink circuit, so the demo ends with working hardware.

| Step | Part           | Connection                        |
| ---- | -------------- | --------------------------------- |
| 1    | Red LED        | Long leg row 10, short leg row 12 |
| 2    | 220 Ω resistor | Row 12 to the negative rail       |
| 3    | Jumper wire    | Arduino pin 13 to row 10          |
| 4    | Jumper wire    | Arduino GND to the negative rail  |

After step 4 the app shows "Circuit complete!". Upload the Arduino **Blink** example (pin 13) and the LED blinks.

![Real circuit](docs/images/real-circuit.jpg)

## Hardware and software

- Arduino Uno, breadboard, red LED, 220 Ω resistor, 3 jumper wires
- A printed A4 collage sheet. The Uno and breadboard are taped on it, because a bare breadboard has almost no features for ORB to match
- A phone camera used as a webcam (Iriun Webcam over USB, 640 × 480) and a Windows laptop
- Unity **2022.3.9f1 LTS** (C#, Built-in Render Pipeline), OpenCvSharp4 (NuGetForUnity), Arduino IDE

## How to run

**From source**

1. Install Unity 2022.3.9f1 LTS (use the same version, because different versions can corrupt scenes).
2. Clone this repo and open the folder in Unity. Let the packages restore.
3. Install Iriun Webcam on the phone and the PC, connect over USB, and keep the phone above the board.
4. Open `Assets/Scenes/Main.unity` and press **Play**. The Console should say `Using camera 0: Iriun Webcam`.
5. Set `cameraIndex` on the ARController if your phone is not camera 0.

**From the executable**
Unzip the build folder, connect the phone with Iriun Webcam, and run `GhostHand.exe`. Keep the `GhostHand_Data` folder next to the `.exe`.

**Controls**

| Key | Action              |
| --- | ------------------- |
| N   | Next step           |
| B   | Previous step       |
| R   | Restart             |
| A   | Auto-advance on/off |

## Architecture

One frame loop passes the same homography H to every module.

![Architecture](docs/images/architecture.png)

| Member | Module         | Files                                                                 | Job                                                      |
| ------ | -------------- | --------------------------------------------------------------------- | -------------------------------------------------------- |
| 1      | Tracking       | `Tracker.cs`                                                          | ORB, ratio test, RANSAC, Lucas-Kanade; returns H or null |
| 2      | Step guidance  | `StepManager.cs`, `StepData.cs`, `steps.json`, `Editor/StepPicker.cs` | 3D parts, rings, arrows, spotlight, ghosts, labels       |
| 3      | Hand occlusion | `HandOcclusion.cs`, `HandPatch.cs`                                    | Hand mask, convex hull, hull patch over the parts        |
| 4      | Integration    | `ARController.cs`                                                     | Camera, main loop, keys, HUD, auto-advance               |

### Contracts between modules

| Method                                       | Returns                                                       |
| -------------------------------------------- | ------------------------------------------------------------- |
| `Tracker.GetHomography(Texture2D frame)`     | `float[9]` row-major 3×3, or `null` if the board is not found |
| `StepManager.ShowStep(int index, float[] H)` | Places the 3D part. `Hide()` clears it                        |
| `HandOcclusion.GetHandHull(Texture2D frame)` | `Vector2[]` hull in frame pixels, empty if no hand            |

Rules:

- **H maps `reference.jpg` pixels to live frame pixels**, always in this direction, with the origin at the top-left.
- `steps.json` stores each step's `from` and `to` points in `reference.jpg` pixels (clicked with the StepPicker editor tool).
- A module never throws. It returns `null` or an empty result on failure.
- The interfaces use plain Unity types, so only the tracker and occlusion code need OpenCvSharp.

## How tracking works

1. **DETECT:** 800 ORB keypoints are matched to `reference.jpg` (Hamming distance), filtered with Lowe's ratio test (0.75), and a RANSAC homography is fitted (3 px threshold).
2. **TRACK:** Lucas-Kanade optical flow follows the inlier points and refits H. This is faster and steadier than matching every frame.
3. **RECOVER:** it returns to DETECT when fewer than 8 points are tracked, when H fails the sanity check, or every 180 frames.

H is smoothed (factor 0.5). One frame takes about 5 ms.

## Results

| Criterion             | Target                             | Result                                   |
| --------------------- | ---------------------------------- | ---------------------------------------- |
| Board detected        | Within 2 s                         | Under 2 s                                |
| Overlay stays aligned | While the board moves or tilts     | Stable up to about 45° tilt              |
| Tracking recovers     | After the board leaves and returns | About 2 s                                |
| Assembly steps        | At least 3                         | 4 steps, restart and a completion screen |
| Hand occlusion        | Overlay hidden under the hand      | Hidden under the hand and single fingers |
| Frame rate            | 15+ FPS                            | About 30 FPS                             |

## Limitations

- Tracking is lost beyond about 60° tilt.
- The boards must not move on the sheet. If they do, retake `reference.jpg` and re-click the steps.
- Auto-advance only sees that a hand visited the target holes and left. It does not check that the part is in the right holes.
- The skin colour range may need tuning for different lighting or users.
- Desktop only (Windows), with the phone used as a webcam.

## Future work

- Hand landmarks (MediaPipe) for more precise finger occlusion
- Automatic check that a part is in the right holes
- An Android build that runs on the phone, with gyroscope fusion for steadier tracking
- More circuits (push button, capacitor, 555 timer) through new `steps.json` entries

## References

1. E. Rublee et al., "ORB: An efficient alternative to SIFT or SURF," ICCV 2011.
2. B. D. Lucas, T. Kanade, "An iterative image registration technique with an application to stereo vision," IJCAI 1981.
3. M. A. Fischler, R. C. Bolles, "Random sample consensus," Communications of the ACM, 1981.
4. D. G. Lowe, "Distinctive image features from scale-invariant keypoints," IJCV 2004.
5. OpenCV and OpenCvSharp4 (github.com/shimat/opencvsharp); Unity 2022.3 LTS documentation.
