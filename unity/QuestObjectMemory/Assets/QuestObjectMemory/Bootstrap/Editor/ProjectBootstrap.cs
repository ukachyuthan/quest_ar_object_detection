using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestObjectMemory.EditorTools
{
    /// <summary>
    /// One-time project setup, driven from menu items rather than checked-in
    /// ProjectSettings YAML.
    ///
    /// The YAML under ProjectSettings/ is GUID-linked and version-specific, so
    /// hand-authoring it produces a project that silently misbehaves on a
    /// different editor version. Applying the same settings through the public
    /// API is verifiable and survives editor upgrades.
    ///
    /// Packages are added *by name with no version*, so UPM resolves whatever is
    /// current for the installed editor. Pinning Meta's versions here would rot
    /// every time Horizon OS ships a new SDK.
    ///
    /// This lives in its own assembly (QuestObjectMemory.Bootstrap.Editor, which
    /// references nothing) on purpose. On a fresh clone the runtime scripts do not
    /// compile until the Meta packages exist, and a broken assembly takes all of
    /// its menu items down with it — including, if this lived alongside them, the
    /// one that installs the packages. Isolated, it always compiles, so the
    /// bootstrap is always reachable.
    /// </summary>
    public static class ProjectBootstrap
    {
        private const string ApplicationIdentifier = "com.questdetect.objectmemory";

        /// <summary>
        /// Unity Inference Engine runs the detector; the two Meta packages supply
        /// PassthroughCameraAccess, MRUK rooms, spatial anchors and the
        /// environment raycast.
        /// </summary>
        private static readonly string[] RequiredPackages =
        {
            "com.unity.ai.inference",
            "com.meta.xr.sdk.core",
            "com.meta.xr.mrutilitykit",
        };

        private static Queue<string> s_pending;
        private static AddRequest s_request;
        private static bool s_exitWhenDone;
        private static bool s_failed;

        [MenuItem("Tools/Quest Object Memory/1. Install Dependencies", priority = 1)]
        public static void InstallDependencies()
        {
            s_exitWhenDone = false;
            s_pending = new Queue<string>(RequiredPackages);
            InstallNext();
        }

        /// <summary>
        /// Headless entry point:
        /// <code>
        /// Unity.exe -batchmode -nographics -projectPath &lt;path&gt; \
        ///   -executeMethod QuestObjectMemory.EditorTools.ProjectBootstrap.InstallDependenciesBatch
        /// </code>
        /// Deliberately invoked WITHOUT <c>-quit</c>: package requests are driven
        /// by editor ticks, and -quit would exit before the first one resolves.
        /// This exits the editor itself once the queue drains.
        /// </summary>
        public static void InstallDependenciesBatch()
        {
            s_exitWhenDone = true;
            s_failed = false;
            s_pending = new Queue<string>(RequiredPackages);
            InstallNext();
        }

        private static void InstallNext()
        {
            if (s_pending == null || s_pending.Count == 0)
            {
                s_pending = null;
                Debug.Log("[QuestObjectMemory] All packages installed. " +
                          "Run 'Tools/Quest Object Memory/2. Apply Player Settings' next.");

                if (s_exitWhenDone) EditorApplication.Exit(s_failed ? 1 : 0);
                return;
            }

            var package = s_pending.Dequeue();
            Debug.Log($"[QuestObjectMemory] Installing {package}...");
            s_request = Client.Add(package);
            EditorApplication.update += PollInstall;
        }

        private static void PollInstall()
        {
            if (s_request == null || !s_request.IsCompleted) return;

            EditorApplication.update -= PollInstall;

            if (s_request.Status == StatusCode.Success)
            {
                Debug.Log($"[QuestObjectMemory] Installed {s_request.Result.packageId}");
            }
            else
            {
                s_failed = true;

                // Most likely cause is the Meta scoped registry being unreachable,
                // which is worth saying out loud rather than failing silently.
                Debug.LogError(
                    $"[QuestObjectMemory] Could not install package: {s_request.Error?.message}\n" +
                    "If this is a com.meta.* package, check that the 'Meta XR' scoped registry in " +
                    "Packages/manifest.json resolves, then install it manually from " +
                    "Window > Package Manager > My Registries.");
            }

            s_request = null;
            InstallNext();
        }

        [MenuItem("Tools/Quest Object Memory/2. Apply Player Settings", priority = 2)]
        public static void ApplyPlayerSettings()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                Debug.Log("[QuestObjectMemory] Switching active build target to Android...");
                EditorUserBuildSettings.SwitchActiveBuildTarget(NamedBuildTarget.Android.ToBuildTargetGroup(), BuildTarget.Android);
            }

            var android = NamedBuildTarget.Android;

            PlayerSettings.SetApplicationIdentifier(android, ApplicationIdentifier);
            PlayerSettings.productName = "Quest Object Memory";
            PlayerSettings.companyName = "questdetect";

            // IL2CPP + ARM64 only: the Quest store requirement, and the only
            // configuration the Meta SDK supports.
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // Horizon OS v81 sits well above this, but the Meta SDK refuses to
            // build below 32.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;

            // Vulkan only. GLES is not supported for passthrough on Quest 3.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });

            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.SetMobileMTRendering(android, true);

            // We request the camera permission ourselves at the right moment
            // (see PassthroughFrameSource); the automatic prompt on startup races
            // with PassthroughCameraAccess initialisation.
            PlayerSettings.Android.forceInternetPermission = false;

            AssetDatabase.SaveAssets();

            Debug.Log(
                "[QuestObjectMemory] Player settings applied.\n" +
                "Remaining manual steps:\n" +
                "  - Project Settings > XR Plug-in Management > Android: enable Oculus (or OpenXR + Meta Quest feature group)\n" +
                "  - Run 'Tools/Quest Object Memory/3. Build Scene'");
        }
    }
}
