using System;
using UnityEngine;
using UnityEngine.Android;

// PassthroughCameraAccess ships in MRUK (Meta XR SDK v81+). It replaced the older
// WebCamTextureManager + PassthroughCameraUtils pair that most tutorials and
// Meta's pre-v81 samples still use.
//
// This is the ONLY file in the project that touches the passthrough camera API.
// If Meta moves the type, or you have to fall back to WebCamTextureManager on a
// pre-v81 headset, this file is the entire blast radius.
using Meta.XR.MRUtilityKit;

namespace QuestObjectMemory
{
    /// <summary>
    /// Owns the passthrough camera: permission, lifecycle, and the handful of
    /// projections the rest of the app needs.
    ///
    /// PassthroughCameraAccess returns a null texture until
    /// <c>horizonos.permission.HEADSET_CAMERA</c> is granted, and the grant
    /// arrives asynchronously well after Start(), so everything downstream has to
    /// tolerate <see cref="IsReady"/> being false for the first few seconds of a
    /// session.
    /// </summary>
    [RequireComponent(typeof(PassthroughCameraAccess))]
    public class PassthroughFrameSource : MonoBehaviour
    {
        /// <summary>The Horizon OS permission that unlocks passthrough pixels.</summary>
        public const string HeadsetCameraPermission = "horizonos.permission.HEADSET_CAMERA";

        [Tooltip("Logs resolution and permission transitions to logcat.")]
        [SerializeField] private bool verboseLogging = true;

        private PassthroughCameraAccess _access;
        private bool _permissionRequested;
        private bool _wasReady;

        /// <summary>Raised the first frame the camera starts producing pixels.</summary>
        public event Action CameraReady;

        /// <summary>True once permission is granted and frames are flowing.</summary>
        public bool IsReady => _access != null && _access.IsPlaying && _access.GetTexture() != null;

        /// <summary>Native resolution of the passthrough stream.</summary>
        public Vector2Int Resolution =>
            _access != null ? _access.CurrentResolution : Vector2Int.zero;

        /// <summary>
        /// Capture timestamp of the current frame. Detections are projected using
        /// the pose from the same timestamp, otherwise boxes lag behind head
        /// motion.
        /// </summary>
        public long Timestamp => _access != null ? _access.Timestamp : 0L;

        private void Awake()
        {
            _access = GetComponent<PassthroughCameraAccess>();

            // Belt and braces. SceneBuilder saves this component disabled, which
            // is what actually prevents it initialising before the permission
            // lands (component callbacks run in add order, so by the time this
            // Awake runs its OnEnable would already have gone). This line only
            // covers the case where the scene was assembled by hand.
            _access.enabled = false;
        }

        private void Start() => RequestPermissionIfNeeded();

        private void Update()
        {
            if (!_access.enabled && HasPermission())
            {
                _access.enabled = true;
                if (verboseLogging) Debug.Log("[QuestObjectMemory] Camera permission granted; starting capture.");
            }

            if (_wasReady || !IsReady) return;

            _wasReady = true;
            if (verboseLogging)
            {
                Debug.Log($"[QuestObjectMemory] Passthrough camera streaming at " +
                          $"{Resolution.x}x{Resolution.y} ({_access.CameraPosition}).");
            }

            CameraReady?.Invoke();
        }

        private void RequestPermissionIfNeeded()
        {
            if (HasPermission() || _permissionRequested) return;

            _permissionRequested = true;

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionDenied += _ => Debug.LogError(
                "[QuestObjectMemory] HEADSET_CAMERA permission denied. " +
                "Detection cannot run. Grant it in Settings > Apps > Permissions and relaunch.");

            Permission.RequestUserPermission(HeadsetCameraPermission, callbacks);
        }

        private static bool HasPermission() => Permission.HasUserAuthorizedPermission(HeadsetCameraPermission);

        /// <summary>
        /// The live camera texture, or null if the camera is not ready. Do not
        /// cache it across frames — the underlying texture can be reallocated on
        /// resolution changes.
        /// </summary>
        public Texture GetTexture() => _access != null ? _access.GetTexture() : null;

        /// <summary>
        /// World pose of the camera for the current frame. This is the RGB
        /// sensor's pose, which is offset from the eye being rendered — that
        /// offset is exactly why detections must be projected through this rather
        /// than through Camera.main.
        /// </summary>
        public Pose GetCameraPose() => _access.GetCameraPose();

        /// <summary>
        /// Ray through a viewport point, in world space.
        /// </summary>
        /// <param name="viewportPoint">
        /// Normalised, <b>bottom-left origin</b>. <see cref="Detection"/> uses
        /// top-left origin, so callers must flip Y first.
        /// </param>
        public Ray ViewportPointToRay(Vector2 viewportPoint) => _access.ViewportPointToRay(viewportPoint);

        /// <summary>Inverse of <see cref="ViewportPointToRay"/>, for placing UI against known world points.</summary>
        public Vector2 WorldToViewportPoint(Vector3 worldPosition) => _access.WorldToViewportPoint(worldPosition);
    }
}
