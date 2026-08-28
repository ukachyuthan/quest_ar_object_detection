using System.Collections.Generic;
using UnityEngine;
using Meta.XR;
using Meta.XR.MRUtilityKit;

namespace QuestObjectMemory
{
    /// <summary>A detection resolved to a place in the room.</summary>
    public readonly struct ProjectedDetection
    {
        public readonly Detection Detection;

        /// <summary>World position of the box centre on the nearest real surface.</summary>
        public readonly Vector3 Position;

        /// <summary>Surface normal at <see cref="Position"/>, or the ray direction reversed if unknown.</summary>
        public readonly Vector3 Normal;

        /// <summary>Approximate real-world size of the object in metres (width, height).</summary>
        public readonly Vector2 WorldSize;

        /// <summary>
        /// False when depth was unavailable and <see cref="Position"/> is a guess
        /// at a fixed distance. Callers should not anchor these.
        /// </summary>
        public readonly bool HasDepth;

        public ProjectedDetection(in Detection detection, Vector3 position, Vector3 normal,
                                  Vector2 worldSize, bool hasDepth)
        {
            Detection = detection;
            Position = position;
            Normal = normal;
            WorldSize = worldSize;
            HasDepth = hasDepth;
        }
    }

    /// <summary>
    /// Turns 2D boxes into world poses.
    ///
    /// Depth comes from the environment raycast (Quest 3 depth sensor) rather
    /// than from stereo: the passthrough API gives us one eye's camera, so there
    /// is no disparity to work with. Where the depth sensor has nothing to say —
    /// beyond its frustum, or a surface it cannot resolve — we fall back to the
    /// scanned room geometry, and only then to a fixed distance.
    /// </summary>
    public class DetectionProjector : MonoBehaviour
    {
        [SerializeField] private PassthroughFrameSource frameSource;
        [SerializeField] private EnvironmentRaycastManager raycastManager;

        [Tooltip("Rays longer than this are treated as misses; rooms are not this big.")]
        [SerializeField] private float maxRayDistance = 12f;

        [Tooltip("Used only when neither depth nor room geometry produces a hit.")]
        [SerializeField] private float fallbackDistance = 2.5f;

        /// <summary>
        /// Projects every detection in a decoded frame.
        /// </summary>
        public List<ProjectedDetection> Project(in DetectionFrame frame)
        {
            var results = new List<ProjectedDetection>(frame.Detections.Count);

            foreach (var detection in frame.Detections)
            {
                if (TryProject(detection, out var projected)) results.Add(projected);
            }

            return results;
        }

        public bool TryProject(in Detection detection, out ProjectedDetection projected)
        {
            projected = default;
            if (frameSource == null || !frameSource.IsReady) return false;

            var centerRay = ViewportRay(detection.CenterX, detection.CenterY);

            var hasDepth = TryResolveDepth(centerRay, out var position, out var normal);
            if (!hasDepth)
            {
                position = centerRay.origin + centerRay.direction * fallbackDistance;
                normal = -centerRay.direction;
            }

            var distance = Vector3.Distance(centerRay.origin, position);
            projected = new ProjectedDetection(
                detection, position, normal, EstimateWorldSize(detection, distance), hasDepth);

            return true;
        }

        /// <summary>
        /// Builds a world ray through a point in the detection's coordinate space.
        ///
        /// Detection is normalised TOP-left origin; ViewportPointToRay wants
        /// normalised BOTTOM-left origin. That single Y flip is the difference
        /// between boxes landing on objects and boxes landing on their mirror
        /// image across the horizon.
        /// </summary>
        private Ray ViewportRay(float x, float topLeftY) =>
            frameSource.ViewportPointToRay(new Vector2(x, 1f - topLeftY));

        private bool TryResolveDepth(in Ray ray, out Vector3 position, out Vector3 normal)
        {
            // 1. Depth sensor. Most accurate, and works on unscanned clutter.
            if (raycastManager != null &&
                raycastManager.Raycast(ray, out var depthHit, maxRayDistance))
            {
                position = depthHit.point;
                normal = depthHit.normal;
                return true;
            }

            // 2. Scanned room geometry. Coarser, but covers anything outside the
            //    depth camera frustum — notably a TV you are looking at edge-on.
            var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
            if (room != null && room.Raycast(ray, maxRayDistance, out var roomHit, out _))
            {
                position = roomHit.point;
                normal = roomHit.normal;
                return true;
            }

            position = default;
            normal = default;
            return false;
        }

        /// <summary>
        /// Approximates the object's physical size by walking the box's own edge
        /// rays out to the measured distance. Used for marker scale, and by the
        /// memory reconciler to tell a bookshelf speaker from a subwoofer when
        /// both carry the same label.
        /// </summary>
        private Vector2 EstimateWorldSize(in Detection detection, float distance)
        {
            var left = ViewportRay(detection.Left, detection.CenterY).direction;
            var right = ViewportRay(detection.Right, detection.CenterY).direction;
            var top = ViewportRay(detection.CenterX, detection.Top).direction;
            var bottom = ViewportRay(detection.CenterX, detection.Bottom).direction;

            return new Vector2(
                Vector3.Distance(left * distance, right * distance),
                Vector3.Distance(top * distance, bottom * distance));
        }
    }
}
