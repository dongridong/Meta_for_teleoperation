using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

namespace HandTrackingViz
{
    // ====================================================================
    // PanelPointer
    // --------------------------------------------------------------------
    // Input for the control panel, without the XR Interaction Toolkit. Two
    // ways to press a button, both live in both APKs:
    //
    //   POKE  — an index fingertip pushed through the panel plane. A press
    //           needs the finger to come from IN FRONT of the panel (at
    //           least ArmDepth toward the eye) and then reach the plane while
    //           over a button. Touching the plane anywhere disarms that hand
    //           until it is pulled back out, so a finger sliding sideways
    //           behind the panel, or pulled back out through it, never fires.
    //
    //   RAY   — a controller's aim ray hits the panel and the index trigger
    //           is pulled past TriggerPress (re-arms below TriggerRelease).
    //           A thin line is drawn from the controller to the hit point
    //           while the ray is over the panel, so the user can see aim.
    //
    // The hand build has hand visualization on, the controller build off —
    // that only changes what is DRAWN. Hand tracking runs in both, so poke
    // works in both, and so does the ray whenever a controller is held.
    //
    // Frames: XR Hands joints and controller poses are in tracking space and
    // go through the XR Origin to world, as everywhere else in this project.
    // ====================================================================
    internal sealed class PanelPointer
    {
        private const float ArmDepth = 0.012f;      // m in front of the plane to arm a poke
        private const float TouchDepth = 0.004f;    // m in front of the plane that counts as touching
        private const float BehindLimit = 0.04f;    // m behind the plane still counted as a press
        private const float HoverDepth = 0.04f;     // m in front of the plane that highlights
        private const float PokeMarginUnits = 10f;  // 1 mm slack around buttons (keys sit 2 mm apart)
        private const float RayMaxDistance = 3f;
        private const float TriggerPress = 0.6f;
        private const float TriggerRelease = 0.3f;
        private const float RayWidth = 0.0015f;

        // Same filter as ControllerSampler: hand-tracking interaction profiles
        // also register as HeldInHand|Controller and must not be taken for one.
        private const InputDeviceCharacteristics HeldController =
            InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Controller;

        // OpenXR exposes the controller's aim pose under these names. The grip
        // (device) pose is used when they are missing; it points less well,
        // but still points.
        private static readonly InputFeatureUsage<Vector3> PointerPosition = new InputFeatureUsage<Vector3>("PointerPosition");
        private static readonly InputFeatureUsage<Quaternion> PointerRotation = new InputFeatureUsage<Quaternion>("PointerRotation");

        private readonly Transform trackingOrigin;
        private readonly bool[] pokeArmed = new bool[2];
        private readonly bool[] triggerHeld = new bool[2];
        private readonly LineRenderer[] rays = new LineRenderer[2];
        private readonly List<InputDevice> devices = new List<InputDevice>();
        private readonly List<XRHandSubsystem> subsystems = new List<XRHandSubsystem>();
        private XRHandSubsystem handSubsystem;

        public PanelPointer(Transform trackingOrigin, Transform rayParent)
        {
            this.trackingOrigin = trackingOrigin;
            var mat = new Material(Shader.Find("Sprites/Default")) { color = new Color(1f, 1f, 1f, 0.8f) };
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject(i == 0 ? "PanelRay_Left" : "PanelRay_Right");
                go.transform.SetParent(rayParent, false);
                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.startWidth = RayWidth;
                line.endWidth = RayWidth * 0.5f;
                line.sharedMaterial = mat;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.enabled = false;
                rays[i] = line;
            }
        }

        /// <param name="plane">The panel canvas; its forward axis points away from the eye.</param>
        /// <param name="buttons">Candidates; hidden ones are skipped.</param>
        /// <param name="isOverPanel">Whether a world point lies on a visible part of the panel.</param>
        public void Update(Transform plane, IReadOnlyList<PanelButton> buttons,
                           System.Func<Vector3, bool> isOverPanel)
        {
            for (int b = 0; b < buttons.Count; b++)
                buttons[b].SetHovered(false);

            UpdatePoke(0, plane, buttons);
            UpdatePoke(1, plane, buttons);
            UpdateRay(0, InputDeviceCharacteristics.Left, plane, buttons, isOverPanel);
            UpdateRay(1, InputDeviceCharacteristics.Right, plane, buttons, isOverPanel);
        }

        public void HideRays()
        {
            rays[0].enabled = false;
            rays[1].enabled = false;
        }

        // ---------------------------------------------------------------- poke

        private void UpdatePoke(int side, Transform plane, IReadOnlyList<PanelButton> buttons)
        {
            if (!TryGetIndexTip(side, out Vector3 tip))
            {
                pokeArmed[side] = false;
                return;
            }

            // Signed distance along the panel normal: negative = toward the eye.
            float depth = Vector3.Dot(tip - plane.position, plane.forward);

            if (depth < -ArmDepth)
            {
                pokeArmed[side] = true;
                if (depth > -HoverDepth)
                    Hit(buttons, tip, PokeMarginUnits)?.SetHovered(true);
                return;
            }

            if (depth > BehindLimit)
            {
                pokeArmed[side] = false;
                return;
            }

            // At or through the plane.
            PanelButton hit = Hit(buttons, tip, PokeMarginUnits);
            if (hit != null) hit.SetHovered(true);
            if (pokeArmed[side] && depth >= -TouchDepth)
            {
                pokeArmed[side] = false;
                hit?.Click();
            }
        }

        private bool TryGetIndexTip(int side, out Vector3 world)
        {
            world = default;
            if (handSubsystem == null || !handSubsystem.running)
            {
                handSubsystem = null;
                SubsystemManager.GetSubsystems(subsystems);
                foreach (var s in subsystems)
                    if (s.running) { handSubsystem = s; break; }
                if (handSubsystem == null) return false;
            }

            XRHand hand = side == 0 ? handSubsystem.leftHand : handSubsystem.rightHand;
            if (!hand.isTracked) return false;
            if (!hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose pose)) return false;

            world = trackingOrigin != null ? trackingOrigin.TransformPoint(pose.position) : pose.position;
            return true;
        }

        // ----------------------------------------------------------------- ray

        private void UpdateRay(int side, InputDeviceCharacteristics hand, Transform plane,
                               IReadOnlyList<PanelButton> buttons, System.Func<Vector3, bool> isOverPanel)
        {
            LineRenderer line = rays[side];
            if (!TryGetController(hand, out InputDevice device, out Vector3 origin, out Vector3 direction))
            {
                line.enabled = false;
                triggerHeld[side] = false;
                return;
            }

            float trigger = device.TryGetFeatureValue(CommonUsages.trigger, out float t) ? t : 0f;
            bool pressedNow = false;
            if (!triggerHeld[side] && trigger > TriggerPress) { triggerHeld[side] = true; pressedNow = true; }
            else if (triggerHeld[side] && trigger < TriggerRelease) { triggerHeld[side] = false; }

            float denom = Vector3.Dot(direction, plane.forward);
            float dist = denom > 1e-4f ? Vector3.Dot(plane.position - origin, plane.forward) / denom : -1f;
            if (dist <= 0f || dist > RayMaxDistance)
            {
                line.enabled = false;
                return;
            }

            Vector3 point = origin + direction * dist;
            if (!isOverPanel(point))
            {
                line.enabled = false;
                return;
            }

            line.enabled = true;
            line.SetPosition(0, origin);
            line.SetPosition(1, point);

            PanelButton hit = Hit(buttons, point, 0f);
            if (hit == null) return;
            hit.SetHovered(true);
            if (pressedNow) hit.Click();
        }

        private bool TryGetController(InputDeviceCharacteristics hand, out InputDevice device,
                                      out Vector3 origin, out Vector3 direction)
        {
            origin = default;
            direction = default;
            device = default;

            InputDevices.GetDevicesWithCharacteristics(HeldController | hand, devices);
            bool found = false;
            foreach (var candidate in devices)
            {
                if (!candidate.isValid) continue;
                if ((candidate.characteristics & InputDeviceCharacteristics.HandTracking) != 0) continue;
                device = candidate;
                found = true;
                break;
            }
            if (!found) return false;
            if (!device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked) return false;

            if (!(device.TryGetFeatureValue(PointerPosition, out Vector3 pos) &&
                  device.TryGetFeatureValue(PointerRotation, out Quaternion rot)))
            {
                if (!(device.TryGetFeatureValue(CommonUsages.devicePosition, out pos) &&
                      device.TryGetFeatureValue(CommonUsages.deviceRotation, out rot)))
                    return false;
            }

            if (trackingOrigin != null)
            {
                pos = trackingOrigin.TransformPoint(pos);
                rot = trackingOrigin.rotation * rot;
            }
            origin = pos;
            direction = rot * Vector3.forward;
            return true;
        }

        // ------------------------------------------------------------- helpers

        private static PanelButton Hit(IReadOnlyList<PanelButton> buttons, Vector3 world, float margin)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                PanelButton b = buttons[i];
                if (b.Visible && b.Contains(world, margin)) return b;
            }
            return null;
        }
    }
}
