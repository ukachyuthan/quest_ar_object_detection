#!/usr/bin/env python3
"""Bake open-vocabulary prompts into a YOLO-World checkpoint and export to ONNX.

YOLO-World scores image regions against CLIP text embeddings. `set_classes()`
runs the text encoder once and freezes the resulting embeddings into the head,
which turns the open-vocab model into an ordinary fixed-class detector — so the
phone/headset never has to run a text encoder, and the exported graph is a plain
YOLO with len(prompts) outputs.

Change model/prompts.txt and re-run to retarget the app at different objects.
"""
import argparse
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent
ASSETS = ROOT.parent / "app" / "src" / "main" / "assets"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--weights", default="yolov8s-worldv2.pt",
                    help="YOLO-World checkpoint (yolov8s/m/l-worldv2.pt)")
    ap.add_argument("--imgsz", type=int, default=448,
                    help="Square input size baked into the ONNX graph")
    ap.add_argument("--opset", type=int, default=17)
    ap.add_argument("--prompts", type=Path, default=ROOT / "prompts.txt")
    args = ap.parse_args()

    prompts = [p.strip() for p in args.prompts.read_text().splitlines() if p.strip()]
    if not prompts:
        raise SystemExit(f"no prompts found in {args.prompts}")
    print(f"baking {len(prompts)} classes: {prompts}")

    from ultralytics import YOLOWorld

    model = YOLOWorld(args.weights)
    model.set_classes(prompts)

    out = model.export(format="onnx", imgsz=args.imgsz, opset=args.opset, simplify=True)
    out = Path(out)
    print(f"exported {out} ({out.stat().st_size / 1e6:.1f} MB)")

    ASSETS.mkdir(parents=True, exist_ok=True)
    shutil.copy(out, ASSETS / "detector.onnx")
    (ASSETS / "labels.json").write_text(json.dumps({
        "imgsz": args.imgsz,
        "classes": prompts,
        "weights": args.weights,
    }, indent=2))
    print(f"installed -> {ASSETS / 'detector.onnx'}")
    print(f"installed -> {ASSETS / 'labels.json'}")


if __name__ == "__main__":
    main()
