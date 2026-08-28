# quest_ar_object_detection

Find the speakers, subwoofers, TVs, monitors and PC cases in your room through
the Quest 3 / 3S passthrough cameras, place them where they actually are in 3D,
and **remember them per room** — name a room "bedroom" once, and every later
session the headset recognises it and puts the markers back on the real objects.

Unity + Meta XR SDK. The detector is a YOLO-World graph with your prompts baked
into it, running on-device through the Unity Inference Engine.

## Status

| Milestone | State |
| --- | --- |
| **1. Detect and place in 3D** | Built, not yet run on hardware |
| **2. Per-room spatial memory** | Designed, not yet implemented |

Milestone 1's decode/NMS maths is covered by EditMode tests and cross-checked
against a Python oracle. Everything touching the headset needs a real device pass.

## How the room memory works

The interesting design decision is what *doesn't* get built.

You might expect "know which room I'm in" to mean recognising the room from the
objects in it. It doesn't need to. Horizon OS already fingerprints every room you
scan in **Space Setup** and localises you into the right one automatically.
`MRUK.Instance.GetCurrentRoom()` hands you that room, and `room.Anchor.Uuid` is
stable across sessions.

So room identity is a dictionary lookup, not a perception problem:

```
Space Setup scan ──► room anchor UUID ──► rooms.json ──► "bedroom"
                          (Horizon OS)        (ours)
```

Object memory sits on top of that. Each detected object gets an
`OVRSpatialAnchor` saved to local storage; `objects.json` records what that
anchor *means* (class, score, which room, when last seen). On the next session
the anchors are reloaded by UUID and rebound, so the markers come back on the
real speakers rather than at remembered coordinates that drift.

## Requirements

- **Quest 3 or 3S on Horizon OS v81+.** `PassthroughCameraAccess` — the component
  this is built on — landed in v81. Check Settings › System › Software Update.
- **Unity 6 LTS (6000.0.x)** with the **Android Build Support** module, including
  the OpenJDK and Android SDK/NDK sub-modules. That bundles `adb`; you do not
  need a separate Android SDK install.
- Developer Mode on, and passthrough enabled.
- **Space Setup run for each room** you want remembered (Milestone 2).
- Python 3.9+ only if you want to re-export the model.

## Setup

### 1. Export the model

`detector.onnx` is gitignored — it is ~45MB and reproducible — so you need to
build it once:

```powershell
py -m venv model\.venv
model\.venv\Scripts\pip.exe install -r model\requirements.txt
model\.venv\Scripts\python.exe model\export.py
```

That writes `detector.onnx` and `labels.json` into
`unity/QuestObjectMemory/Assets/QuestObjectMemory/Models/`.

### 2. Open the project and install packages

Open `unity/QuestObjectMemory/` in Unity 6.

**The console will show compile errors on first open — that's expected.** The
scripts reference Meta and Inference Engine types that aren't installed yet. Run:

> **Tools › Quest Object Memory › 1. Install Dependencies**

which adds `com.unity.ai.inference`, `com.meta.xr.sdk.core` and
`com.meta.xr.mrutilitykit` by name, so UPM resolves whatever version is current
rather than a pinned one that rots.

That menu item lives in its own assembly precisely so it still works while the
rest of the project is failing to compile.

### 3. Configure and build the scene

> **Tools › Quest Object Memory › 2. Apply Player Settings**
> **Tools › Quest Object Memory › 3. Build Scene**

Then enable **Oculus** under Project Settings › XR Plug-in Management › Android —
the one step that has no reliable scripted equivalent.

`File › Build And Run` with the headset connected.

On first launch, accept the camera permission. If you deny it,
`PassthroughCameraAccess` returns a null texture forever and nothing detects;
re-grant it in Settings › Apps › Permissions and relaunch.

## How it works

```
PassthroughCameraAccess (MRUK)
   │  RGB texture, already on the GPU
   ▼
Letterbox.shader ──► 448x448 RenderTexture      (one blit; CPU never sees pixels)
   ▼
Unity Inference Engine ──► YOLO-World ONNX      (GPUCompute, split over frames)
   ▼
DetectionDecoder ──► boxes + two-stage NMS      (normalised, top-left origin)
   ▼
DetectionProjector ──► world position           (depth raycast, then room geometry)
   ▼
DetectionMarker                                 (outline welded to surface, billboarded caption)
```

| File | Role |
| --- | --- |
| [Core/Detection.cs](unity/QuestObjectMemory/Assets/QuestObjectMemory/Core/Detection.cs) | Box + letterbox transform types |
| [Core/DetectionDecoder.cs](unity/QuestObjectMemory/Assets/QuestObjectMemory/Core/DetectionDecoder.cs) | Tensor → boxes, two-stage NMS |
| [Core/LabelSet.cs](unity/QuestObjectMemory/Assets/QuestObjectMemory/Core/LabelSet.cs) | Reads `labels.json` |
| [Runtime/PassthroughFrameSource.cs](unity/QuestObjectMemory/Assets/QuestObjectMemory/Runtime/PassthroughFrameSource.cs) | Camera permission, lifecycle, pose and ray projections |
| [Runtime/YoloWorldDetector.cs](unity/QuestObjectMemory/Assets/QuestObjectMemory/Runtime/YoloWorldDetector.cs) | Inference Engine scheduling and readback |
| [Runtime/DetectionProjector.cs](unity/QuestObjectMemory/Assets/QuestObjectMemory/Runtime/DetectionProjector.cs) | 2D box → world pose |
| [Runtime/DetectionMarker.cs](unity/QuestObjectMemory/Assets/QuestObjectMemory/Runtime/DetectionMarker.cs) | Per-object visual, built in code |
| [Editor/SceneBuilder.cs](unity/QuestObjectMemory/Assets/QuestObjectMemory/Editor/SceneBuilder.cs) | Generates `Main.unity` |

Four details worth knowing:

- **Frames are dropped, not queued.** A new frame is only grabbed once the
  previous one has been decoded, so the boxes describe the most recent view of
  the room rather than drifting further behind the longer the app runs.
- **The camera pose is sampled at capture, not at readback.** Inference takes
  tens of milliseconds and your head keeps moving; projecting through the current
  pose instead makes boxes visibly swim when you turn.
- **Detections are top-left origin, `ViewportPointToRay` is bottom-left.**
  `DetectionProjector.ViewportRay` owns that Y flip. Getting it wrong puts boxes
  on their mirror image across the horizon rather than crashing.
- **Depth is monocular.** The passthrough API gives one eye's camera, so there is
  no stereo disparity. Depth comes from the Quest 3 depth sensor via
  `EnvironmentRaycastManager`, falling back to scanned room geometry, then to a
  fixed distance. Markers placed at the fallback are captioned `?` and must not
  be anchored.

## Nothing is hand-authored Unity YAML

Scenes, prefabs and `ProjectSettings.asset` are GUID-linked YAML: unreviewable in
a diff, and prone to breaking when an asset GUID changes. So the scene and the
project settings are *generated* by editor scripts, and the markers build their
own geometry at runtime. Rebuilding after an SDK upgrade is one menu click rather
than a manual reconnection pass.

## Retargeting the detector

YOLO-World matches image regions against CLIP text embeddings.
[model/export.py](model/export.py) runs the text encoder once at export time and
freezes the result into the detection head, which turns the open-vocabulary model
into an ordinary fixed-class YOLOv8 — so the headset never runs a text encoder,
and inference is as cheap as a normal YOLOv8s. It also means the exported graph
uses only common ops, which is why the Inference Engine imports it without fuss.

Edit [model/prompts.txt](model/prompts.txt), one phrase per line, then re-run
`export.py` and rebuild.

Prompts work best as short noun phrases ("a subwoofer" and "subwoofer" both work;
"the big black speaker in the corner" does not). `--weights yolov8m-worldv2.pt`
and `--imgsz 640` trade speed for accuracy.

To check a change without deploying, run the exported graph against photos on the
desktop — [model/validate.py](model/validate.py) mirrors the letterbox, decode
and NMS:

```powershell
model\.venv\Scripts\python.exe model\validate.py photo1.jpg photo2.jpg
$env:CONF=0.1; model\.venv\Scripts\python.exe model\validate.py photo1.jpg
```

## Tuning

| Knob | Where | Default |
| --- | --- | --- |
| Confidence threshold | `DecoderSettings.ConfidenceThreshold` | `0.25` |
| Cross-class box merging | `DecoderSettings.IouCrossClass` | `0.75` |
| Layers scheduled per frame | `YoloWorldDetector.layersPerFrame` | `25` |
| Inference backend | `YoloWorldDetector.backend` | `GPUCompute` |
| Which eye's camera | `PassthroughCameraAccess.CameraPosition` | Left |
| Input resolution | `model/export.py --imgsz` | `448` |

YOLO-World's scores are strongly bimodal — real hits land above ~0.4 and noise
below ~0.02 — so moving the threshold between 0.1 and 0.3 changes very little.

Note that "tv" and "computer monitor" fire on the same object, hard. The second
NMS pass collapses them into one box carrying the higher-scoring label, which
means a desk monitor is sometimes labelled "tv". Drop one of the two prompts if
you want that decided differently.

## Tests

`Window › General › Test Runner › EditMode` covers the decode and NMS maths,
including the letterbox inverse, the tv/monitor collapse, and rejection of a
stale `labels.json`. These run without a headset and without the Meta packages —
that is why the decoder lives in its own dependency-free assembly.

## Legacy Android app

`app/` is the original native Kotlin implementation: Camera2 + ONNX Runtime
drawing boxes on a flat 2D panel. It is kept as the reference the detector maths
was ported from, not as a build target. `model/export.py --out` can still target
its assets directory if you want to run it.

## Licence note

Ultralytics YOLO-World is AGPL-3.0, which extends to the exported weights. Fine
for personal use; relevant if this is ever distributed.
