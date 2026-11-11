using UnityEngine;

namespace HandTrackingViz
{
    // ====================================================================
    // StatusIndicator
    // --------------------------------------------------------------------
    // Head-locked HUD of four small dots pinned to the top-centre of the
    // operator's view, just below the control panel's gear button. Fixed
    // order, left to right:
    //
    //   1. HMD      — head pose is live this frame
    //   2. LEFT     — left hand OR left controller tracked
    //   3. RIGHT    — right hand OR right controller tracked
    //   4. RECEIVER — at least one TCP subscriber is connected (set apart by
    //                 an extra gap, marking it as the network dot)
    //
    // GREEN = OK, RED = missing. PoseFramePublisher owns one and feeds it the
    // same values it serializes, so a green dot means the receiver is actually
    // getting that source as tracked — not merely that the headset thinks so.
    //
    // The dots are tiny spheres parented to the HMD camera (head-locked), so
    // they need no billboarding and stay in the corner of the view.
    // ====================================================================
    public sealed class StatusIndicator : MonoBehaviour
    {
        private const int DotCount = 4;

        /// <summary>m in front of the camera. Shared with ControlPanel, whose
        /// gear and panel are laid out around this row on the same plane, so
        /// the whole cluster sits at one depth and the gear is in poke reach.</summary>
        public const float Distance = 0.40f;

        /// <summary>Centre of the dot row in the camera's local x/y (m):
        /// horizontally centred, ~10 deg above the line of sight.</summary>
        public static readonly Vector2 RowCenter = new Vector2(0f, 0.072f);

        // Same angular size as the original 0.5 m layout (scaled by 0.8).
        private const float DotDiameter = 0.004f;
        private const float Spacing = 0.0096f;       // dot centre-to-centre
        private const float GroupGap = 0.0048f;      // extra gap before RECEIVER

        private static readonly Color Green = new Color(0.1f, 0.9f, 0.2f);
        private static readonly Color Red = new Color(0.95f, 0.15f, 0.1f);

        private static readonly string[] DotNames =
            { "Dot_HMD", "Dot_Left", "Dot_Right", "Dot_Receiver" };

        private readonly Renderer[] dots = new Renderer[DotCount];
        private readonly bool[] states = new bool[DotCount];
        private Material greenMat;
        private Material redMat;

        // Creates the HUD as a head-locked child of the HMD camera.
        public static StatusIndicator Attach(Transform hmdCamera)
        {
            var go = new GameObject("StatusHUD");
            go.transform.SetParent(hmdCamera, false);
            return go.AddComponent<StatusIndicator>();
        }

        private void Awake()
        {
            // Unlit so the colors read over passthrough (same shader choice as
            // HandVisualizer's fallback). Two shared materials; dots swap
            // between them instead of mutating per-dot materials.
            greenMat = MakeUnlit(Green);
            redMat = MakeUnlit(Red);

            // Centre the row (including the RECEIVER gap) on RowCenter.x.
            float rowWidth = (DotCount - 1) * Spacing + GroupGap;
            float x0 = RowCenter.x - rowWidth * 0.5f;
            for (int i = 0; i < DotCount; i++)
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = DotNames[i];
                sphere.transform.SetParent(transform, false);
                float x = x0 + i * Spacing + (i == DotCount - 1 ? GroupGap : 0f);
                sphere.transform.localPosition = new Vector3(x, RowCenter.y, Distance);
                sphere.transform.localScale = Vector3.one * DotDiameter;

                // Colliders are useless here and just cost physics work.
                var col = sphere.GetComponent<Collider>();
                if (col != null) Destroy(col);

                dots[i] = sphere.GetComponent<Renderer>();
                dots[i].sharedMaterial = redMat;   // pessimistic until first update
            }
        }

        private static Material MakeUnlit(Color c)
        {
            var mat = new Material(Shader.Find("Sprites/Default"));
            mat.color = c;
            return mat;
        }

        // Called once per frame by PoseFramePublisher, AFTER the snapshot has
        // been published, with the published tracked flags + client count.
        public void SetStates(bool hmd, bool left, bool right, bool receiver)
        {
            Apply(0, hmd);
            Apply(1, left);
            Apply(2, right);
            Apply(3, receiver);
        }

        private void Apply(int i, bool ok)
        {
            if (states[i] == ok || dots[i] == null) return;
            states[i] = ok;
            dots[i].sharedMaterial = ok ? greenMat : redMat;
        }
    }
}
