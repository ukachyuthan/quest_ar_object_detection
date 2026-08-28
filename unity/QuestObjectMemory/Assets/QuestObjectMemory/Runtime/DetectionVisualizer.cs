using System.Collections.Generic;
using UnityEngine;

namespace QuestObjectMemory
{
    /// <summary>
    /// Milestone 1 output: draws a live marker for every current detection.
    ///
    /// These markers are transient — they follow whatever the detector sees right
    /// now and vanish when it looks away. Making them persist is Milestone 2's
    /// job (MemoryReconciler), and deliberately not this component's.
    /// </summary>
    public class DetectionVisualizer : MonoBehaviour
    {
        [SerializeField] private YoloWorldDetector detector;
        [SerializeField] private DetectionProjector projector;

        [Tooltip("Markers placed at the fallback distance because depth was unavailable.")]
        [SerializeField] private bool showDepthlessDetections = true;

        private readonly List<DetectionMarker> _pool = new();

        private void OnEnable()
        {
            if (detector != null) detector.FrameDecoded += OnFrameDecoded;
        }

        private void OnDisable()
        {
            if (detector != null) detector.FrameDecoded -= OnFrameDecoded;
        }

        private void OnFrameDecoded(DetectionFrame frame)
        {
            var projected = projector.Project(frame);

            var shown = 0;
            foreach (var p in projected)
            {
                if (!p.HasDepth && !showDepthlessDetections) continue;

                MarkerAt(shown++).Show(p, ColorForClass(p.Detection.ClassIndex));
            }

            // Markers are pooled rather than destroyed: at 10-20Hz, allocating and
            // collecting a fresh set every frame is a steady GC drip the headset
            // does not need.
            for (var i = shown; i < _pool.Count; i++) _pool[i].Hide();
        }

        private DetectionMarker MarkerAt(int index)
        {
            while (_pool.Count <= index) _pool.Add(DetectionMarker.Create(transform));

            return _pool[index];
        }

        /// <summary>
        /// Spreads classes around the hue wheel by golden ratio, so adjacent class
        /// indices never land on similar colours.
        /// </summary>
        private static Color ColorForClass(int classIndex) =>
            Color.HSVToRGB(Mathf.Repeat(classIndex * 0.618f, 1f), 0.75f, 1f);
    }
}
