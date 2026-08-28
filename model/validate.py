#!/usr/bin/env python3
"""Run the exported ONNX exactly the way the Kotlin app does, so the letterbox +
decode + NMS maths is verified on the desktop before trusting it on-device."""
import json
import sys
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image

ROOT = Path(__file__).resolve().parent
ASSETS = ROOT.parent / "app" / "src" / "main" / "assets"

CONF = float(__import__("os").environ.get("CONF", 0.25))
IOU_PER_CLASS = 0.45
IOU_AGNOSTIC = 0.75


def letterbox(img: Image.Image, size: int):
    w, h = img.size
    scale = min(size / w, size / h)
    nw, nh = round(w * scale), round(h * scale)
    pad_x, pad_y = (size - nw) / 2, (size - nh) / 2
    canvas = Image.new("RGB", (size, size), (114, 114, 114))
    canvas.paste(img.resize((nw, nh), Image.BILINEAR), (int(pad_x), int(pad_y)))
    return canvas, scale, pad_x, pad_y


def nms(boxes, iou_thr, per_class):
    kept = []
    for b in sorted(boxes, key=lambda d: -d["score"]):
        clash = False
        for k in kept:
            if per_class and k["cls"] != b["cls"]:
                continue
            x1, y1 = max(k["x1"], b["x1"]), max(k["y1"], b["y1"])
            x2, y2 = min(k["x2"], b["x2"]), min(k["y2"], b["y2"])
            inter = max(0.0, x2 - x1) * max(0.0, y2 - y1)
            union = ((k["x2"] - k["x1"]) * (k["y2"] - k["y1"])
                     + (b["x2"] - b["x1"]) * (b["y2"] - b["y1"]) - inter)
            if union > 0 and inter / union > iou_thr:
                clash = True
                break
        if not clash:
            kept.append(b)
    return kept


def main() -> None:
    meta = json.loads((ASSETS / "labels.json").read_text())
    size, classes = meta["imgsz"], meta["classes"]
    sess = ort.InferenceSession(str(ASSETS / "detector.onnx"), providers=["CPUExecutionProvider"])
    inp = sess.get_inputs()[0].name

    for path in sys.argv[1:]:
        img = Image.open(path).convert("RGB")
        canvas, scale, pad_x, pad_y = letterbox(img, size)
        x = np.asarray(canvas, dtype=np.float32).transpose(2, 0, 1)[None] / 255.0

        out = sess.run(None, {inp: x})[0]           # (1, 4+nc, N)
        pred = out[0]
        n = pred.shape[1]
        scores = pred[4:]                            # (nc, N)
        best_cls = scores.argmax(0)
        best = scores[best_cls, np.arange(n)]
        keep = best > CONF

        boxes = []
        for i in np.nonzero(keep)[0]:
            cx, cy, bw, bh = pred[0, i], pred[1, i], pred[2, i], pred[3, i]
            boxes.append({
                "cls": int(best_cls[i]), "score": float(best[i]),
                "x1": (cx - bw / 2 - pad_x) / scale, "y1": (cy - bh / 2 - pad_y) / scale,
                "x2": (cx + bw / 2 - pad_x) / scale, "y2": (cy + bh / 2 - pad_y) / scale,
            })
        boxes = nms(nms(boxes, IOU_PER_CLASS, True), IOU_AGNOSTIC, False)

        print(f"\n== {Path(path).name}  ({img.size[0]}x{img.size[1]})  {len(boxes)} detections")
        for b in boxes:
            print(f"   {classes[b['cls']]:<17} {b['score']:.3f}  "
                  f"[{b['x1']:7.1f} {b['y1']:7.1f} {b['x2']:7.1f} {b['y2']:7.1f}]")


if __name__ == "__main__":
    main()
