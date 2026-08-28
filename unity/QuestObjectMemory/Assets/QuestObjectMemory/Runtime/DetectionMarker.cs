using UnityEngine;

namespace QuestObjectMemory
{
    /// <summary>
    /// The visual for one detected object: a rectangle outlining its extents plus
    /// a billboarded caption.
    ///
    /// Builds its own geometry in code. A prefab would be the conventional choice,
    /// but prefabs are GUID-linked YAML that cannot be authored or reviewed
    /// outside the editor, and this shape is a dozen lines of LineRenderer.
    /// </summary>
    public class DetectionMarker : MonoBehaviour
    {
        private const float LineWidth = 0.006f;
        private const float CaptionHeightAboveBox = 0.06f;

        private LineRenderer _outline;
        private TextMesh _caption;
        private Transform _captionTransform;
        private Camera _headCamera;
        private Material _outlineMaterial;

        public static DetectionMarker Create(Transform parent)
        {
            var go = new GameObject("DetectionMarker");
            go.transform.SetParent(parent, false);

            var marker = go.AddComponent<DetectionMarker>();
            marker.Build();
            return marker;
        }

        private void Build()
        {
            // Unlit: the markers must stay legible against passthrough regardless
            // of room lighting, so they are deliberately not lit by the scene.
            // Owned per-marker (not shared) because each instance is recoloured to
            // whichever class currently occupies its pool slot.
            _outlineMaterial = new Material(Shader.Find("Unlit/Color"));

            _outline = gameObject.AddComponent<LineRenderer>();
            _outline.material = _outlineMaterial;
            _outline.useWorldSpace = false;
            _outline.loop = true;
            _outline.positionCount = 4;
            _outline.startWidth = LineWidth;
            _outline.endWidth = LineWidth;
            _outline.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _outline.receiveShadows = false;

            var captionGo = new GameObject("Caption");
            _captionTransform = captionGo.transform;
            _captionTransform.SetParent(transform, false);

            _caption = captionGo.AddComponent<TextMesh>();
            _caption.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _caption.GetComponent<MeshRenderer>().sharedMaterial = _caption.font.material;
            _caption.fontSize = 96;

            // A small character size with a large font size is how you get crisp
            // world-space text out of TextMesh; scaling up a small font blurs.
            _caption.characterSize = 0.006f;
            _caption.anchor = TextAnchor.LowerCenter;
            _caption.alignment = TextAlignment.Center;

            _headCamera = Camera.main;
        }

        /// <summary>
        /// Positions, recolours and relabels the marker for a freshly projected
        /// detection. Colour is passed per-call rather than fixed at construction
        /// because pooled markers are reused across classes between frames.
        /// </summary>
        public void Show(in ProjectedDetection projected, Color color)
        {
            gameObject.SetActive(true);

            _outlineMaterial.color = color;
            _caption.color = color;

            var d = projected.Detection;
            transform.position = projected.Position;

            // Lie the rectangle flat against the surface it was found on.
            transform.rotation = Quaternion.LookRotation(-projected.Normal, Vector3.up);

            var halfWidth = Mathf.Max(projected.WorldSize.x, 0.05f) * 0.5f;
            var halfHeight = Mathf.Max(projected.WorldSize.y, 0.05f) * 0.5f;

            _outline.SetPosition(0, new Vector3(-halfWidth, -halfHeight, 0f));
            _outline.SetPosition(1, new Vector3(halfWidth, -halfHeight, 0f));
            _outline.SetPosition(2, new Vector3(halfWidth, halfHeight, 0f));
            _outline.SetPosition(3, new Vector3(-halfWidth, halfHeight, 0f));

            _captionTransform.localPosition = new Vector3(0f, halfHeight + CaptionHeightAboveBox, 0f);

            // The "?" marks a marker placed at the fallback distance because depth
            // was unavailable — worth seeing, since those must not be anchored.
            _caption.text = projected.HasDepth
                ? $"{d.Label}  {d.Score:0.00}"
                : $"{d.Label}  {d.Score:0.00}  ?";
        }

        public void Hide() => gameObject.SetActive(false);

        private void LateUpdate()
        {
            if (_headCamera == null)
            {
                _headCamera = Camera.main;
                if (_headCamera == null) return;
            }

            // Billboard only the caption. The outline stays welded to the surface,
            // which is what communicates the object's actual orientation.
            _captionTransform.rotation = Quaternion.LookRotation(
                _captionTransform.position - _headCamera.transform.position, Vector3.up);
        }

        private void OnDestroy()
        {
            // Instantiated in Build(), so it is ours to clean up.
            if (_outlineMaterial != null) Destroy(_outlineMaterial);
        }
    }
}
