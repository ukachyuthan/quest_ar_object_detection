using System;
using UnityEngine;

namespace QuestObjectMemory
{
    /// <summary>
    /// The contents of labels.json, written by model/export.py alongside the ONNX.
    ///
    /// Keeping the class list and input size in the exported artefact — rather
    /// than duplicated in a Unity inspector field — means editing
    /// model/prompts.txt and re-exporting is the only step needed to retarget the
    /// app. A mismatch between the two is caught in DetectionDecoder.Decode.
    /// </summary>
    [Serializable]
    public class LabelSet
    {
        [SerializeField] private int imgsz;
        [SerializeField] private string[] classes;
        [SerializeField] private string weights;

        /// <summary>Square input size baked into the ONNX graph, e.g. 448.</summary>
        public int InputSize => imgsz;

        public string[] Classes => classes;

        /// <summary>Checkpoint the model was exported from, for diagnostics.</summary>
        public string Weights => weights;

        public static LabelSet Parse(TextAsset json)
        {
            if (json == null)
                throw new ArgumentNullException(nameof(json), "labels.json TextAsset is not assigned");

            var parsed = JsonUtility.FromJson<LabelSet>(json.text);

            if (parsed == null)
                throw new FormatException($"could not parse {json.name} as labels.json");
            if (parsed.classes == null || parsed.classes.Length == 0)
                throw new FormatException($"{json.name} lists no classes");
            if (parsed.imgsz <= 0)
                throw new FormatException($"{json.name} has a non-positive imgsz ({parsed.imgsz})");

            return parsed;
        }
    }
}
