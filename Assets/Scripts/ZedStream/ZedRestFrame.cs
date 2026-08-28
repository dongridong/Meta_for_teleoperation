using UnityEngine;

namespace ZedStream
{
    /// <summary>
    /// The quiet surround around the video panel — a large sphere drawn from the
    /// inside, in a very dark neutral grey with a faint static grid.
    ///
    /// This exists because of the decision not to magnify the image (see
    /// UNITY_PORT_PROMPT.md section 4): at 1:1 angular size the panel covers only
    /// part of the headset's field of view, and the surround is therefore a design
    /// problem rather than a defect to hide. Peripheral vision with a stable
    /// reference in it is more comfortable than peripheral vision staring into
    /// nothing, so the surround is deliberately visible — but barely. If the grid
    /// is noticeable it is drawn too strongly; the target is "you would not say it
    /// was there, but you would miss it if it went".
    ///
    /// Fixed in the world, never parented to the head. A surround that moves with
    /// the head is not a reference frame.
    /// </summary>
    public sealed class ZedRestFrame : MonoBehaviour
    {
        static readonly int GridDensityId = Shader.PropertyToID("_GridDensity");
        static readonly int GridWidthId = Shader.PropertyToID("_GridWidth");

        [Tooltip("Draw the surround. Off leaves the camera's clear colour showing " +
                 "through, i.e. flat dark with no reference at all.")]
        public bool show = true;

        [Tooltip("Radius in metres. Large enough that the operator can move around a " +
                 "seated workspace without approaching the shell.")]
        public float radius = 50f;

        [Tooltip("Grid lines across the sphere's UV. Higher is denser.")]
        [Range(4f, 128f)] public float gridDensity = 48f;

        [Tooltip("Grid line width as a fraction of a cell. Keep it small — a grid " +
                 "that draws attention is worse than no grid.")]
        [Range(0.001f, 0.2f)] public float gridWidth = 0.02f;

        MeshRenderer m_Renderer;
        Material m_Material;

        void Awake()
        {
            m_Renderer = GetComponent<MeshRenderer>();
            if (m_Renderer != null)
                m_Material = m_Renderer.material;   // instance, not the shared asset
            Apply();
        }

        void OnValidate() => Apply();

        void Apply()
        {
            // Unity's built-in sphere is 1 unit across, so a radius of r needs a
            // diameter of 2r in scale.
            transform.localScale = Vector3.one * (radius * 2f);

            if (m_Renderer != null)
                m_Renderer.enabled = show;

            if (m_Material != null)
            {
                m_Material.SetFloat(GridDensityId, gridDensity);
                m_Material.SetFloat(GridWidthId, gridWidth);
            }
        }

        void OnDestroy()
        {
            if (m_Material != null)
                Destroy(m_Material);
        }
    }
}
