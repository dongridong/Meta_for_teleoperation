using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ZedStream.EditorTools
{
    /// <summary>
    /// Idempotent scene setup for the ZED video rig, in the same spirit as
    /// HandTrackingVizSceneSetup: run it as many times as you like, from the menu
    /// or from batch mode, and the scene converges on the same state.
    ///
    ///   Unity -batchmode -quit -projectPath . -buildTarget Android \
    ///         -executeMethod ZedStream.EditorTools.ZedSceneSetup.SetupRig
    ///
    /// The rig is added to the SAME scene the other two builds use. It costs them
    /// nothing: ZedPanelController disables the whole rig in Awake unless the build
    /// defined HTV_ZED_VIDEO, so in the hands and controller APKs these objects are
    /// inert from the first frame.
    /// </summary>
    public static class ZedSceneSetup
    {
        const string ScenePath = "Assets/Scenes/SampleScene.unity";
        const string MaterialDir = "Assets/Materials";
        const string PanelMaterialPath = MaterialDir + "/ZedSbsStereo.mat";
        const string RestFrameMaterialPath = MaterialDir + "/ZedRestFrame.mat";

        const string RigName = "ZED Rig";
        const string PanelName = "Panel";
        const string RestFrameName = "RestFrame";

        [MenuItem("Tools/ZED Stream/Setup Rig")]
        public static void SetupRig()
        {
            if (EditorSceneManager.GetActiveScene().path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            GameObject rig = GameObject.Find(RigName);
            if (rig == null)
            {
                rig = new GameObject(RigName);
                Undo.RegisterCreatedObjectUndo(rig, "Create ZED Rig");
            }
            rig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            ZedPanelController controller = GetOrAdd<ZedPanelController>(rig);
            ZedWhepClient whep = GetOrAdd<ZedWhepClient>(rig);

            ZedStereoPanel panel = BuildPanel(rig);
            ZedRestFrame restFrame = BuildRestFrame(rig);

            controller.whepClient = whep;
            controller.panel = panel;
            controller.restFrame = restFrame;
            controller.hmdCamera = Camera.main;
            EditorUtility.SetDirty(controller);

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            AssetDatabase.SaveAssets();
            Selection.activeObject = rig;
            Debug.Log("[ZedStream] rig setup complete: panel + rest frame + WHEP client. " +
                      "Inert unless the build defines HTV_ZED_VIDEO.");
        }

        static ZedStereoPanel BuildPanel(GameObject rig)
        {
            GameObject go = FindChild(rig, PanelName);
            if (go == null)
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = PanelName;
                Undo.RegisterCreatedObjectUndo(go, "Create ZED Panel");
                go.transform.SetParent(rig.transform, false);
            }

            // A collider on a backdrop is only ever in the way — hand and controller
            // rays would start hitting it.
            StripCollider(go);

            // Placed in front of the origin so the scene view shows something
            // sensible; the runtime recenters it on the first frame anyway.
            go.transform.localPosition = new Vector3(0f, 0f, 2f);
            go.transform.localRotation = Quaternion.identity;

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = EnsureMaterial(PanelMaterialPath, "ZED/SbsStereo");
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var panel = GetOrAdd<ZedStereoPanel>(go);
            EditorUtility.SetDirty(panel);
            return panel;
        }

        static ZedRestFrame BuildRestFrame(GameObject rig)
        {
            GameObject go = FindChild(rig, RestFrameName);
            if (go == null)
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = RestFrameName;
                Undo.RegisterCreatedObjectUndo(go, "Create ZED Rest Frame");
                go.transform.SetParent(rig.transform, false);
            }

            // The sphere is drawn from the INSIDE, so its collider would be a shell
            // enclosing the entire play area.
            StripCollider(go);

            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = EnsureMaterial(RestFrameMaterialPath, "ZED/RestFrame");
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var rest = GetOrAdd<ZedRestFrame>(go);
            EditorUtility.SetDirty(rest);
            return rest;
        }

        /// <summary>
        /// Loads the material, creating it if missing. The material asset is also
        /// what pulls the shader into the build — a shader nothing references is
        /// stripped, and the symptom of that is magenta in the headset only.
        /// </summary>
        static Material EnsureMaterial(string path, string shaderName)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
                throw new System.Exception($"[ZedStream] shader '{shaderName}' not found. " +
                                           "Did the shader fail to compile?");

            if (existing != null)
            {
                if (existing.shader != shader)
                {
                    existing.shader = shader;
                    EditorUtility.SetDirty(existing);
                }
                return existing;
            }

            Directory.CreateDirectory(MaterialDir);
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static GameObject FindChild(GameObject parent, string name)
        {
            Transform t = parent.transform.Find(name);
            return t == null ? null : t.gameObject;
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            T c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        static void StripCollider(GameObject go)
        {
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Object.DestroyImmediate(collider);
        }
    }
}
