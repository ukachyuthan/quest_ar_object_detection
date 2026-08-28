using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace QuestObjectMemory.EditorTools
{
    /// <summary>
    /// Injects the passthrough camera permissions into the generated manifest at
    /// build time.
    ///
    /// Deliberately a post-processor rather than a checked-in
    /// Assets/Plugins/Android/AndroidManifest.xml: the Meta XR SDK generates and
    /// maintains the base manifest (XR feature tags, intent filters, required
    /// device features), and overriding that file wholesale means silently
    /// missing whatever Meta adds in the next SDK version. This only adds the two
    /// lines we own.
    /// </summary>
    public class AndroidManifestPostProcessor : IPostGenerateGradleAndroidProject
    {
        // Runs after the Meta SDK's own manifest work.
        public int callbackOrder => 100;

        private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

        /// <summary>
        /// The Horizon OS permission that actually unlocks passthrough camera
        /// pixels. PassthroughCameraAccess returns a null texture until it is
        /// granted.
        /// </summary>
        private const string HeadsetCameraPermission = "horizonos.permission.HEADSET_CAMERA";

        /// <summary>
        /// Also required: Horizon OS gates HEADSET_CAMERA behind the standard
        /// Android camera permission, and the WebCamTexture fallback path needs
        /// it outright.
        /// </summary>
        private const string AndroidCameraPermission = "android.permission.CAMERA";

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogError($"[QuestObjectMemory] Generated manifest not found at {manifestPath}; " +
                               "camera permissions were NOT added and passthrough capture will fail.");
                return;
            }

            var doc = new XmlDocument();
            doc.Load(manifestPath);

            var manifest = doc.SelectSingleNode("/manifest") as XmlElement;
            if (manifest == null)
            {
                Debug.LogError("[QuestObjectMemory] Malformed AndroidManifest.xml: no <manifest> root.");
                return;
            }

            var added = false;
            added |= EnsurePermission(doc, manifest, HeadsetCameraPermission);
            added |= EnsurePermission(doc, manifest, AndroidCameraPermission);

            if (!added) return;

            doc.Save(manifestPath);
            Debug.Log($"[QuestObjectMemory] Camera permissions written to {manifestPath}");
        }

        private static bool EnsurePermission(XmlDocument doc, XmlElement manifest, string permission)
        {
            foreach (XmlElement existing in manifest.SelectNodes("uses-permission"))
            {
                if (existing.GetAttribute("name", AndroidNamespace) == permission) return false;
            }

            var element = doc.CreateElement("uses-permission");
            element.SetAttribute("name", AndroidNamespace, permission);
            manifest.AppendChild(element);
            return true;
        }
    }
}
