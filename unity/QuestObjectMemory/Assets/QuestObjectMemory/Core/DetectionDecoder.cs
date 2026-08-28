using System;
using System.Collections.Generic;

namespace QuestObjectMemory
{
    /// <summary>
    /// Tunables for <see cref="DetectionDecoder"/>. Defaults match the values the
    /// Kotlin implementation was tuned to on real footage.
    /// </summary>
    [Serializable]
    public struct DecoderSettings
    {
        /// <summary>
        /// YOLO-World scores are strongly bimodal — real hits land above ~0.4,
        /// noise below ~0.02 — so this sits in the empty middle, and moving it
        /// anywhere between 0.1 and 0.3 changes very little in practice.
        /// </summary>
        public float ConfidenceThreshold;

        /// <summary>IoU above which two boxes of the SAME class are duplicates.</summary>
        public float IouSameClass;

        /// <summary>IoU above which two boxes of DIFFERENT classes are the same object.</summary>
        public float IouCrossClass;

        public int MaxDetections;

        /// <summary>Boxes thinner than this (normalised) are noise.</summary>
        public float MinSize;

        public static DecoderSettings Default => new DecoderSettings
        {
            ConfidenceThreshold = 0.25f,
            IouSameClass = 0.45f,
            IouCrossClass = 0.75f,
            MaxDetections = 20,
            MinSize = 0.01f,
        };
    }

    /// <summary>
    /// Turns a raw YOLO output tensor into boxes.
    ///
    /// Port of YoloDetector.decode/suppress/iou from the Kotlin app, kept free of
    /// UnityEngine and Meta SDK types so it can be exercised in EditMode tests
    /// against the same fixtures model/validate.py uses.
    ///
    /// The graph is a plain fixed-class YOLOv8: model/export.py runs the CLIP
    /// text encoder once at export time and freezes the embeddings into the head,
    /// so there is no open-vocabulary work left to do at runtime.
    /// </summary>
    public static class DetectionDecoder
    {
        /// <summary>
        /// Decodes a <c>[1, 4 + numClasses, anchors]</c> tensor laid out
        /// channel-major.
        /// </summary>
        /// <param name="raw">Flattened output, length <c>channels * anchors</c>.</param>
        /// <param name="letterbox">How the frame was fitted, for the inverse map.</param>
        public static List<Detection> Decode(
            float[] raw,
            int channels,
            int anchors,
            in Letterbox letterbox,
            IReadOnlyList<string> labels,
            in DecoderSettings settings)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (labels == null) throw new ArgumentNullException(nameof(labels));
            if (raw.Length < channels * anchors)
            {
                throw new ArgumentException(
                    $"tensor holds {raw.Length} floats, expected at least {channels * anchors}", nameof(raw));
            }

            var numClasses = channels - 4;
            if (numClasses > labels.Count)
            {
                throw new ArgumentException(
                    $"model emits {numClasses} classes but labels.json lists {labels.Count}; " +
                    "re-run model/export.py so the two agree", nameof(labels));
            }

            var candidates = new List<Detection>();
            var width = letterbox.SourceWidth;
            var height = letterbox.SourceHeight;

            for (var i = 0; i < anchors; i++)
            {
                var bestScore = 0f;
                var bestClass = -1;
                for (var c = 0; c < numClasses; c++)
                {
                    var score = raw[(4 + c) * anchors + i];
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestClass = c;
                    }
                }

                if (bestClass < 0 || bestScore < settings.ConfidenceThreshold) continue;

                // Model space: centre-xywh in pixels of the square letterboxed input.
                var cx = raw[i];
                var cy = raw[anchors + i];
                var bw = raw[2 * anchors + i];
                var bh = raw[3 * anchors + i];

                var left = Clamp01((cx - bw * 0.5f - letterbox.PadX) / letterbox.Scale / width);
                var top = Clamp01((cy - bh * 0.5f - letterbox.PadY) / letterbox.Scale / height);
                var right = Clamp01((cx + bw * 0.5f - letterbox.PadX) / letterbox.Scale / width);
                var bottom = Clamp01((cy + bh * 0.5f - letterbox.PadY) / letterbox.Scale / height);

                if (right - left < settings.MinSize || bottom - top < settings.MinSize) continue;

                candidates.Add(new Detection(
                    bestClass, labels[bestClass], bestScore, left, top, right, bottom));
            }

            candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));

            // Two passes: the first drops duplicate boxes of the same class, the
            // second collapses near-synonyms across classes — "tv" and "computer
            // monitor" both fire hard on the same screen, and one screen should
            // get one box carrying the higher-scoring label.
            var sameClass = Suppress(candidates, settings.IouSameClass, true, settings.MaxDetections);
            return Suppress(sameClass, settings.IouCrossClass, false, settings.MaxDetections);
        }

        private static List<Detection> Suppress(
            List<Detection> sorted, float threshold, bool sameClassOnly, int maxDetections)
        {
            var kept = new List<Detection>(sorted.Count);

            foreach (var candidate in sorted)
            {
                var suppressed = false;
                foreach (var k in kept)
                {
                    if (sameClassOnly && k.ClassIndex != candidate.ClassIndex) continue;
                    if (Iou(k, candidate) > threshold)
                    {
                        suppressed = true;
                        break;
                    }
                }

                if (suppressed) continue;

                kept.Add(candidate);
                if (kept.Count >= maxDetections) break;
            }

            return kept;
        }

        private static float Iou(in Detection a, in Detection b)
        {
            var x1 = Math.Max(a.Left, b.Left);
            var y1 = Math.Max(a.Top, b.Top);
            var x2 = Math.Min(a.Right, b.Right);
            var y2 = Math.Min(a.Bottom, b.Bottom);

            var intersection = Math.Max(0f, x2 - x1) * Math.Max(0f, y2 - y1);
            if (intersection <= 0f) return 0f;

            var union = (a.Right - a.Left) * (a.Bottom - a.Top)
                        + (b.Right - b.Left) * (b.Bottom - b.Top)
                        - intersection;

            return union <= 0f ? 0f : intersection / union;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
