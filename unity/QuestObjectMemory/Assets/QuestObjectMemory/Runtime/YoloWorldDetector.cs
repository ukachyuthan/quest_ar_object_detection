using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// com.unity.ai.inference 2.x. (Sentis 1.x used the Unity.Sentis namespace; if you
// end up on that version, this using is the only line that changes.)
using Unity.InferenceEngine;

namespace QuestObjectMemory
{
    /// <summary>
    /// Results of one inference pass, carrying everything needed to place the
    /// boxes in the world.
    /// </summary>
    public readonly struct DetectionFrame
    {
        public readonly IReadOnlyList<Detection> Detections;
        public readonly Letterbox Letterbox;

        /// <summary>
        /// Camera pose sampled when the frame was *captured*, not when inference
        /// finished. Inference takes tens of milliseconds and the head keeps
        /// moving; projecting through the current pose instead of this one makes
        /// boxes visibly swim whenever you turn your head.
        /// </summary>
        public readonly Pose CameraPose;

        public DetectionFrame(IReadOnlyList<Detection> detections, in Letterbox letterbox, in Pose cameraPose)
        {
            Detections = detections;
            Letterbox = letterbox;
            CameraPose = cameraPose;
        }
    }

    /// <summary>
    /// Runs the prompt-baked YOLO-World graph over passthrough frames.
    ///
    /// Frames are dropped, not queued: a new frame is only grabbed once the
    /// previous one has been decoded, so the boxes always describe the most
    /// recent view of the room rather than drifting further behind the longer the
    /// app runs. This is the same scheduling decision the Kotlin app made, for
    /// the same reason.
    /// </summary>
    public class YoloWorldDetector : MonoBehaviour
    {
        [Header("Model")]
        [Tooltip("detector.onnx, produced by model/export.py")]
        [SerializeField] private ModelAsset modelAsset;

        [Tooltip("labels.json, written alongside the ONNX by model/export.py")]
        [SerializeField] private TextAsset labelsJson;

        [Header("Input")]
        [SerializeField] private PassthroughFrameSource frameSource;
        [SerializeField] private Shader letterboxShader;

        [Header("Execution")]
        [Tooltip("GPUCompute keeps the frame on the GPU end to end. CPU is a fallback for the editor.")]
        [SerializeField] private BackendType backend = BackendType.GPUCompute;

        [Tooltip("Model layers scheduled per frame. Lower = smoother rendering, higher = lower detection latency.")]
        [SerializeField] private int layersPerFrame = 25;

        [Header("Decoding")]
        [SerializeField] private DecoderSettings decoderSettings = DecoderSettings.Default;

        private Worker _worker;
        private LabelSet _labels;
        private Material _letterboxMaterial;
        private RenderTexture _letterboxTarget;
        private int _inputSize;

        /// <summary>Raised on the main thread each time a frame finishes decoding.</summary>
        public event Action<DetectionFrame> FrameDecoded;

        /// <summary>Most recent decode, for HUD/debug readouts.</summary>
        public float LastInferenceMilliseconds { get; private set; }

        private void Awake()
        {
            _labels = LabelSet.Parse(labelsJson);
            _inputSize = _labels.InputSize;

            var model = ModelLoader.Load(modelAsset);
            _worker = new Worker(model, backend);

            _letterboxMaterial = new Material(letterboxShader);
            _letterboxTarget = new RenderTexture(_inputSize, _inputSize, 0, RenderTextureFormat.ARGB32)
            {
                name = "QuestObjectMemory/LetterboxTarget",
                filterMode = FilterMode.Bilinear,
            };
            _letterboxTarget.Create();

            Debug.Log($"[QuestObjectMemory] Detector ready: {_labels.Classes.Length} classes " +
                      $"({string.Join(", ", _labels.Classes)}) at {_inputSize}px, backend {backend}.");
        }

        private void OnEnable() => StartCoroutine(DetectionLoop());

        private IEnumerator DetectionLoop()
        {
            while (true)
            {
                if (frameSource == null || !frameSource.IsReady)
                {
                    yield return null;
                    continue;
                }

                var texture = frameSource.GetTexture();
                if (texture == null)
                {
                    yield return null;
                    continue;
                }

                // Sample the pose in the same frame we sample the pixels.
                var capturePose = frameSource.GetCameraPose();
                var letterbox = Letterbox.Fit(texture.width, texture.height, _inputSize);
                BlitLetterboxed(texture, letterbox);

                var started = Time.realtimeSinceStartup;

                using var input = TextureConverter.ToTensor(_letterboxTarget, _inputSize, _inputSize, 3);

                // Spread the graph across frames so a ~50ms inference never shows
                // up as a dropped render frame in the headset.
                var schedule = _worker.ScheduleIterable(input);
                var layer = 0;
                while (schedule.MoveNext())
                {
                    if (++layer % layersPerFrame == 0) yield return null;
                }

                var output = _worker.PeekOutput() as Tensor<float>;
                if (output == null)
                {
                    Debug.LogError("[QuestObjectMemory] Model produced no float output tensor.");
                    yield return null;
                    continue;
                }

                // Async readback: a blocking DownloadToArray() here would stall
                // the render thread for the full GPU round trip.
                output.ReadbackRequest();
                while (!output.IsReadbackRequestDone()) yield return null;

                using var cpu = output.ReadbackAndClone();
                LastInferenceMilliseconds = (Time.realtimeSinceStartup - started) * 1000f;

                var shape = cpu.shape;           // [1, 4 + numClasses, anchors]
                if (shape.rank != 3)
                {
                    Debug.LogError($"[QuestObjectMemory] Unexpected output rank {shape.rank}, expected 3.");
                    yield return null;
                    continue;
                }

                var detections = DetectionDecoder.Decode(
                    cpu.DownloadToArray(),
                    shape[1],
                    shape[2],
                    letterbox,
                    _labels.Classes,
                    decoderSettings);

                FrameDecoded?.Invoke(new DetectionFrame(detections, letterbox, capturePose));

                yield return null;
            }
        }

        /// <summary>
        /// Aspect-fits <paramref name="source"/> into the square model input.
        /// See Letterbox.shader for the mapping; this only supplies the
        /// source-to-destination UV transform.
        /// </summary>
        private void BlitLetterboxed(Texture source, in Letterbox letterbox)
        {
            // Fraction of the square input actually covered by image content.
            var fx = source.width * letterbox.Scale / _inputSize;
            var fy = source.height * letterbox.Scale / _inputSize;

            // srcUV = destUV * scale + offset
            var scale = new Vector4(1f / fx, 1f / fy, 0f, 0f);
            var offset = new Vector4(-(1f - fx) / (2f * fx), -(1f - fy) / (2f * fy), 0f, 0f);

            _letterboxMaterial.SetVector("_Scale", scale);
            _letterboxMaterial.SetVector("_Offset", offset);

            Graphics.Blit(source, _letterboxTarget, _letterboxMaterial);
        }

        private void OnDestroy()
        {
            _worker?.Dispose();

            if (_letterboxTarget != null)
            {
                _letterboxTarget.Release();
                Destroy(_letterboxTarget);
            }

            if (_letterboxMaterial != null) Destroy(_letterboxMaterial);
        }
    }
}
