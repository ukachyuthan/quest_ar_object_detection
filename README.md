# quest_ar_object_detection

Live object detection on the Meta Quest 3 / 3S passthrough cameras. The app opens
the headset's forward-facing camera through Camera2, runs an open-vocabulary
YOLO-World detector on every frame it can keep up with, and draws labelled boxes
over the live feed in a 2D panel floating in your room.

Out of the box it looks for **speakers, subwoofers, TVs, computer monitors and PC
cases**. Those classes are not hard-coded into the model — see
[Retargeting the detector](#retargeting-the-detector).

## Why an Android app and not WebXR

Quest Browser still does not implement the WebXR `camera-access` module, and
Meta's Passthrough Camera API is native-only. A web page on the Quest can render
passthrough, but it cannot read the passthrough pixels, so browser-side detection
is not possible today. A plain Gradle Android app can read them.

## Requirements

- Quest 3 or Quest 3S on Horizon OS v74+ (v76+ for the Camera2 path), with
  passthrough enabled and Developer Mode on.
- macOS with Homebrew. `tools/env.sh` points at a JDK 17 and an Android SDK; both
  were installed into `/opt/homebrew/opt/openjdk@17` and `~/Library/Android/sdk`.
- Python 3.9+ only if you want to re-export the model.

## Build and install

```bash
tools/install.sh
```

That assembles the debug APK and `adb install`s it. On the headset it shows up
under **Library › Unknown Sources › Quest Object Detect**. Launch it, accept the
camera permission, and the feed appears with boxes on it.

To watch what it is doing (including the camera enumeration dump, which is the
first thing to check if no passthrough camera is found):

```bash
tools/logcat.sh
```

The same APK also installs on an ordinary Android phone — it falls back to the
rear camera when Meta's vendor tags are absent, which is a quick way to sanity
check the detector without the headset.

## How it works

```
Camera2 passthrough camera (1280x960 YUV_420_888, 60Hz)
   ├─ preview surface ─────────────────► TextureView          (zero CPU cost)
   └─ ImageReader ─► YuvLetterbox ─► YOLO-World ONNX ─► NMS ─► OverlayView
                     rotate+scale+       ONNX Runtime
                     RGB+normalise       (arm64, 4 threads)
                     in one pass
```

| File | Role |
| --- | --- |
| [PassthroughCamera.kt](app/src/main/java/com/questdetect/ar/PassthroughCamera.kt) | Finds the passthrough camera via Meta's vendor tags, opens the Camera2 session |
| [YuvLetterbox.kt](app/src/main/java/com/questdetect/ar/YuvLetterbox.kt) | Fuses rotate + downscale + YUV→RGB + normalise into one nearest-neighbour pass |
| [YoloDetector.kt](app/src/main/java/com/questdetect/ar/YoloDetector.kt) | ONNX Runtime session, box decode, two-stage NMS |
| [OverlayView.kt](app/src/main/java/com/questdetect/ar/OverlayView.kt) | Draws boxes, labels and the stats readout |
| [MainActivity.kt](app/src/main/java/com/questdetect/ar/MainActivity.kt) | Permissions, wiring, frame scheduling |

Two details worth knowing:

- **Frames are dropped, not queued.** While the detector is busy, arriving frames
  are closed immediately. The boxes therefore describe the most recent view of
  the room rather than drifting further behind the longer the app runs.
- **The preview surface is sized before the session is configured.** Camera2
  derives the stream resolution from the surface's buffer size, so
  `PassthroughCamera` resolves the camera and its size first
  (`onCameraResolved`), waits for the host to size the `SurfaceTexture`, and only
  then opens the device.

## Retargeting the detector

YOLO-World matches image regions against CLIP text embeddings. `model/export.py`
runs the text encoder once at export time and freezes the result into the
detection head, which turns the open-vocabulary model into an ordinary
fixed-class YOLO — so the headset never runs a text encoder, and inference is as
cheap as a normal YOLOv8s.

Edit [model/prompts.txt](model/prompts.txt), one phrase per line, then:

```bash
model/.venv/bin/python model/export.py     # writes app/src/main/assets/detector.onnx
tools/install.sh
```

Prompts work best as short noun phrases ("a subwoofer" and "subwoofer" both work;
"the big black speaker in the corner" does not). `--weights yolov8m-worldv2.pt`
and `--imgsz 640` trade speed for accuracy.

To check a change without deploying, run the exported graph against photos on the
desktop — `model/validate.py` mirrors the Kotlin letterbox, decode and NMS exactly:

```bash
model/.venv/bin/python model/validate.py photo1.jpg photo2.jpg
CONF=0.1 model/.venv/bin/python model/validate.py photo1.jpg
```

## Tuning

| Knob | Where | Default |
| --- | --- | --- |
| Confidence threshold | `YoloDetector.CONFIDENCE_THRESHOLD` | `0.25` |
| Cross-class box merging | `YoloDetector.IOU_CROSS_CLASS` | `0.75` |
| Inference threads | `YoloDetector.THREADS` | `4` |
| Which eye's camera | `PassthroughCamera.PREFERRED_POSITION` | left |
| Input resolution | `model/export.py --imgsz` | `448` |

YOLO-World's scores are strongly bimodal — real hits land above ~0.4 and noise
below ~0.02 — so moving the threshold between 0.1 and 0.3 changes very little in
practice.

Note that "tv" and "computer monitor" fire on the same object, hard. The
second NMS pass collapses them into one box carrying the higher-scoring label,
which means a desk monitor is sometimes labelled "tv". Drop one of the two
prompts if you want that decided differently.

## Licence note

Ultralytics YOLO-World is AGPL-3.0, which extends to the exported weights. Fine
for personal use; relevant if this is ever distributed.
