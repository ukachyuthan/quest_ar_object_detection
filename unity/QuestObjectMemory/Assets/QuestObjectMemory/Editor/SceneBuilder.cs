using System.IO;
using System.Linq;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace QuestObjectMemory.EditorTools
{
    /// <summary>
    /// Constructs Main.unity from scratch.
    ///
    /// The scene is generated rather than checked in because .unity files are
    /// GUID-linked YAML: they cannot be meaningfully diffed, reviewed, or
    /// hand-edited, and they break whenever an asset GUID changes. Generating it
    /// means the scene's wiring is readable source, and rebuilding after an SDK
    /// upgrade is one menu click instead of a manual reconnection pass.
    /// </summary>
    public static class SceneBuilder
    {
        private const string ScenePath = "Assets/QuestObjectMemory/Scenes/Main.unity";

        [MenuItem("Tools/Quest Object Memory/3. Build Scene", priority = 3)]
        public static void BuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildRig();
            BuildSceneUnderstanding(out var raycastManager);
            BuildDetectionPipeline(raycastManager);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath)!);
            EditorSceneManager.SaveScene(scene, ScenePath);

            AddSceneToBuildSettings();

            Debug.Log($"[QuestObjectMemory] Built {ScenePath}. " +
                      "Check that XR Plug-in Management has Oculus enabled for Android, then File > Build And Run.");
        }

        private static void BuildRig()
        {
            var rig = new GameObject("OVRCameraRig");

            var manager = rig.AddComponent<OVRManager>();
            manager.isInsightPassthroughEnabled = true;

            // Passthrough camera access is off by default; without it
            // PassthroughCameraAccess never produces a texture. The backing field
            // has been renamed across SDK versions, so try the known spellings and
            // say so plainly if none match rather than shipping a scene that
            // silently sees nothing.
            var serialized = new SerializedObject(manager);
            if (!TrySetBool(serialized,
                    new[]
                    {
                        "_enablePassthroughCameraAccess",
                        "enablePassthroughCameraAccess",
                        "_passthroughCameraAccess",
                    }, true))
            {
                Debug.LogWarning(
                    "[QuestObjectMemory] Could not find the passthrough-camera-access field on OVRManager. " +
                    "Enable it by hand on the OVRCameraRig inspector, or the camera will return no frames.");
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();

            // OVRCameraRig rebuilds its own anchor hierarchy (TrackingSpace,
            // eye anchors) in Awake, so it does not need one authored here.
            rig.AddComponent<OVRCameraRig>();
        }

        private static void BuildSceneUnderstanding(out EnvironmentRaycastManager raycastManager)
        {
            var go = new GameObject("SceneUnderstanding");

            // Rooms come from the user's Space Setup scan, which is MRUK's default
            // data source — nothing to configure. Milestone 2 keys the object
            // memory off the room anchor UUID this loads.
            go.AddComponent<MRUK>();

            raycastManager = go.AddComponent<EnvironmentRaycastManager>();
        }

        private static void BuildDetectionPipeline(EnvironmentRaycastManager raycastManager)
        {
            var go = new GameObject("Detection");

            var access = go.AddComponent<PassthroughCameraAccess>();

            // Saved disabled. PassthroughCameraAccess initialises in OnEnable and
            // does not retry, so if it wakes before the camera permission is
            // granted it fails permanently. Disabling it here — rather than in
            // PassthroughFrameSource.Awake — is what actually wins the race:
            // component callbacks run in the order they were added, so this
            // component's OnEnable would otherwise fire first.
            access.enabled = false;

            var frameSource = go.AddComponent<PassthroughFrameSource>();
            var detector = go.AddComponent<YoloWorldDetector>();
            var projector = go.AddComponent<DetectionProjector>();
            var visualizer = go.AddComponent<DetectionVisualizer>();

            WireDetector(detector, frameSource);
            WireProjector(projector, frameSource, raycastManager);
            WireVisualizer(visualizer, detector, projector);
        }

        private static void WireDetector(YoloWorldDetector detector, PassthroughFrameSource frameSource)
        {
            var serialized = new SerializedObject(detector);

            serialized.FindProperty("frameSource").objectReferenceValue = frameSource;
            serialized.FindProperty("letterboxShader").objectReferenceValue =
                Shader.Find("QuestObjectMemory/Letterbox");

            var model = FindSingleAsset<ModelAsset>("detector");
            if (model == null)
            {
                Debug.LogWarning(
                    "[QuestObjectMemory] detector.onnx not found under Assets/. " +
                    "Run model/export.py, then re-run this menu item (or assign it by hand).");
            }

            serialized.FindProperty("modelAsset").objectReferenceValue = model;

            var labels = FindSingleAsset<TextAsset>("labels");
            if (labels == null)
            {
                Debug.LogWarning("[QuestObjectMemory] labels.json not found under Assets/.");
            }

            serialized.FindProperty("labelsJson").objectReferenceValue = labels;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void WireProjector(DetectionProjector projector,
                                          PassthroughFrameSource frameSource,
                                          EnvironmentRaycastManager raycastManager)
        {
            var serialized = new SerializedObject(projector);
            serialized.FindProperty("frameSource").objectReferenceValue = frameSource;
            serialized.FindProperty("raycastManager").objectReferenceValue = raycastManager;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void WireVisualizer(DetectionVisualizer visualizer,
                                           YoloWorldDetector detector,
                                           DetectionProjector projector)
        {
            var serialized = new SerializedObject(visualizer);
            serialized.FindProperty("detector").objectReferenceValue = detector;
            serialized.FindProperty("projector").objectReferenceValue = projector;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static T FindSingleAsset<T>(string nameContains) where T : Object
        {
            var guid = AssetDatabase
                .FindAssets($"{nameContains} t:{typeof(T).Name}", new[] { "Assets" })
                .FirstOrDefault();

            return guid == null ? null : AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));
        }

        private static bool TrySetBool(SerializedObject serialized, string[] candidates, bool value)
        {
            foreach (var name in candidates)
            {
                var property = serialized.FindProperty(name);
                if (property == null || property.propertyType != SerializedPropertyType.Boolean) continue;

                property.boolValue = value;
                return true;
            }

            return false;
        }

        private static void AddSceneToBuildSettings()
        {
            if (EditorBuildSettings.scenes.Any(s => s.path == ScenePath)) return;

            EditorBuildSettings.scenes = EditorBuildSettings.scenes
                .Append(new EditorBuildSettingsScene(ScenePath, true))
                .ToArray();
        }
    }
}
