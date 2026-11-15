using System;
using UnityEngine;

namespace ZedStream
{
    /// <summary>
    /// The camera's MEASURED calibration, mirroring the Jetson's zedx.json field
    /// for field so <see cref="JsonUtility"/> can overwrite an instance directly.
    ///
    /// The defaults below are the values from UNITY_PORT_PROMPT.md section 2 and
    /// exist only as a fallback for when the fetch fails — the running system is
    /// supposed to read zedx.json at startup (see <see cref="ZedCalibrationLoader"/>).
    /// These are NOT spec-sheet numbers: they come from this particular unit's
    /// calibration and change with the lens option, so a hardcoded HFOV would put
    /// the angular size off and that lands as eye strain.
    ///
    /// Fields absent from the JSON keep their default, which is why the loader uses
    /// FromJsonOverwrite — the same "merge onto defaults" the reference page does
    /// with Object.assign(CAM, await r.json()).
    /// </summary>
    [Serializable]
    public sealed class ZedCalibration
    {
        // ---- identity (logging only) ----
        public string model = "ZED X Mini";
        public string serial = "57007126";

        // ---- per-eye image ----
        public int width = 1920;
        public int height = 1200;
        public float fps = 30f;

        // ---- intrinsics ----
        public float fx = 1275.7947998046875f;

        // ---- field of view, degrees ----
        public float hfov_deg = 73.92088105961813f;
        public float vfov_deg = 50.37474031785319f;

        // ---- stereo ----
        public float baseline_mm = 49.97663572430611f;
        public float convergence_m = 1.5f;

        // ---- viewing ----
        public float panel_distance_m = 2f;

        /// <summary>
        /// Full SBS frame width — the stream carries both eyes side by side in one
        /// picture, which is why the shader samples halves rather than two textures.
        /// </summary>
        public int SbsWidth => width * 2;

        /// <summary>
        /// Horizontal shift, in PER-EYE UV, that puts the convergence plane at
        /// <paramref name="convergenceM"/> metres.
        ///
        ///   hit_px = fx * (baseline_m) / (2 * Zc)
        ///   hit_uv = hit_px / width
        ///
        /// (The disparity at distance Z is fx*B/Z; the two eyes split that between
        /// them to cancel it.) Without this the parallax at infinity is zero, so the
        /// whole scene sits in front of the display and the eyes fight to fuse it.
        /// Ported from hitUv() in the reference page.
        /// </summary>
        public float HitUv(float convergenceM)
        {
            float baselineM = baseline_mm / 1000f;
            return (fx * baselineM / (2f * Mathf.Max(0.1f, convergenceM))) / width;
        }

        /// <summary>HitUv at the calibration's own convergence distance.</summary>
        public float HitUv() => HitUv(convergence_m);

        /// <summary>
        /// Half-extents, in metres, of a panel at <paramref name="distanceM"/> that
        /// subtends exactly the camera's field of view — one camera degree to one
        /// headset degree. Recomputed from the distance rather than stored, because
        /// a constant here silently breaks the moment the panel is moved.
        /// </summary>
        public Vector2 PanelHalfExtents(float distanceM)
        {
            return new Vector2(
                distanceM * Mathf.Tan(hfov_deg * Mathf.Deg2Rad * 0.5f),
                distanceM * Mathf.Tan(vfov_deg * Mathf.Deg2Rad * 0.5f));
        }

        public override string ToString()
        {
            return $"{model} sn={serial} {width}x{height}@{fps:0.#} (SBS {SbsWidth}) " +
                   $"hfov={hfov_deg:0.##}° vfov={vfov_deg:0.##}° " +
                   $"baseline={baseline_mm:0.##}mm Zc={convergence_m:0.##}m " +
                   $"hit_uv={HitUv():0.######}";
        }
    }
}
