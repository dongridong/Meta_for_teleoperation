using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace HandTrackingViz.EditorTools
{
    /// <summary>
    /// Injects the Meta Quest tracking permissions and features into the generated
    /// AndroidManifest.xml at build time, so they do not have to be maintained by hand.
    ///
    /// Adds:
    ///   &lt;uses-permission android:name="com.oculus.permission.HAND_TRACKING" /&gt;
    ///   &lt;uses-permission android:name="com.oculus.permission.BODY_TRACKING" /&gt;
    ///   &lt;uses-permission android:name="android.permission.INTERNET" /&gt;
    ///   &lt;uses-feature android:name="oculus.software.handtracking" android:required="false" /&gt;
    ///   &lt;uses-feature android:name="com.oculus.software.body_tracking" android:required="false" /&gt;
    ///
    /// BODY_TRACKING is what lets the runtime create a body tracker for Inside-Out Body
    /// Tracking. Unlike hand tracking, no OpenXR feature injects it for us — the body
    /// tracking support here is this project's own OpenXR feature
    /// (<see cref="HandTrackingViz.BodyTrackingFeature"/>), so declaring it is entirely
    /// on us. It is also a runtime permission: the manifest entry only makes the
    /// consent dialog possible, and BodyLandmarkProvider raises it on first use.
    ///
    /// (The OpenXR hand-tracking feature also injects the hand permission; that part is
    /// a belt-and-suspenders safeguard. The whole postprocessor is idempotent — it will
    /// not create duplicates.)
    /// </summary>
    public sealed class HandTrackingHandPermissionPostprocessor : IPostGenerateGradleAndroidProject
    {
        const string AndroidNs = "http://schemas.android.com/apk/res/android";
        const string HandTrackingPermission = "com.oculus.permission.HAND_TRACKING";
        const string HandTrackingFeature = "oculus.software.handtracking";
        const string BodyTrackingPermission = "com.oculus.permission.BODY_TRACKING";
        const string BodyTrackingFeature = "com.oculus.software.body_tracking";
        const string InternetPermission = "android.permission.INTERNET"; // required by PoseFramePublisher's TCP server

        public int callbackOrder => 1;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning($"[HandTrackingViz] AndroidManifest not found at {manifestPath}; skipping permission injection.");
                return;
            }

            var doc = new XmlDocument();
            doc.Load(manifestPath);

            var manifest = doc.DocumentElement;
            if (manifest == null || manifest.Name != "manifest")
                return;

            AddUsesPermission(doc, manifest, HandTrackingPermission);
            AddUsesPermission(doc, manifest, BodyTrackingPermission);
            AddUsesPermission(doc, manifest, InternetPermission);
            AddUsesFeature(doc, manifest, HandTrackingFeature, required: false);
            // required=false so the app still installs on headsets without body
            // tracking — there it simply publishes the body as tracked:false.
            AddUsesFeature(doc, manifest, BodyTrackingFeature, required: false);

            doc.Save(manifestPath);
            Debug.Log("[HandTrackingViz] Hand-tracking and body-tracking permissions/features ensured in AndroidManifest.xml.");
        }

        static void AddUsesPermission(XmlDocument doc, XmlElement manifest, string permission)
        {
            foreach (XmlElement e in manifest.GetElementsByTagName("uses-permission"))
            {
                if (e.GetAttribute("name", AndroidNs) == permission)
                    return; // already present
            }

            var el = doc.CreateElement("uses-permission");
            el.SetAttribute("name", AndroidNs, permission);
            manifest.AppendChild(el);
        }

        static void AddUsesFeature(XmlDocument doc, XmlElement manifest, string feature, bool required)
        {
            foreach (XmlElement e in manifest.GetElementsByTagName("uses-feature"))
            {
                if (e.GetAttribute("name", AndroidNs) == feature)
                    return; // already present
            }

            var el = doc.CreateElement("uses-feature");
            el.SetAttribute("name", AndroidNs, feature);
            el.SetAttribute("required", AndroidNs, required ? "true" : "false");
            manifest.AppendChild(el);
        }
    }
}
