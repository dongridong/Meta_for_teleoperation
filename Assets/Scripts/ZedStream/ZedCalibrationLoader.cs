using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace ZedStream
{
    /// <summary>
    /// Fetches a camera's calibration JSON from the host serving it, so the
    /// angular size comes from THAT camera's measurements rather than from
    /// constants baked into the app.
    ///
    /// The target is typed as <see cref="object"/> because JsonUtility's
    /// FromJsonOverwrite is, and because the Insta360 dome needs the identical
    /// fetch-and-merge behaviour for a differently shaped calibration. Nothing
    /// here was ever ZED-specific except the log messages, which now name the
    /// file the caller asked for.
    ///
    /// On any failure the caller keeps the <see cref="ZedCalibration"/> defaults and
    /// the stream still renders — slightly wrong angular size beats a black screen —
    /// but the failure is logged as a warning, because silently drawing at the wrong
    /// scale is exactly the kind of bug that gets diagnosed as motion sickness
    /// instead of as a failed HTTP request.
    /// </summary>
    public static class ZedCalibrationLoader
    {
        public const float DefaultTimeoutSeconds = 5f;

        /// <summary>
        /// GETs <paramref name="url"/> and merges the response onto
        /// <paramref name="target"/>, leaving fields the server omits at their
        /// defaults. <paramref name="onDone"/> reports whether the fetch succeeded.
        /// </summary>
        public static IEnumerator Fetch(string url, object target, Action<bool> onDone = null,
                                       string label = "zedx.json")
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            bool ok = false;
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = Mathf.CeilToInt(DefaultTimeoutSeconds);
                yield return ZedHttp.Send(request, ZedHttp.HostOf(url));

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[ZedStream] {label} fetch failed ({request.error}) — " +
                                     $"falling back to the built-in calibration. Angular size may be " +
                                     $"slightly off. url={url}");
                }
                else
                {
                    try
                    {
                        // Overwrite, not replace: a field the server does not send
                        // keeps the fallback value instead of becoming zero, which
                        // for fx or width would divide the HIT calculation by zero.
                        JsonUtility.FromJsonOverwrite(request.downloadHandler.text, target);
                        ok = true;
                        Debug.Log($"[ZedStream] calibration loaded — {target}");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[ZedStream] {label} parse failed ({e.Message}) — " +
                                         "falling back to the built-in calibration.");
                    }
                }
            }

            onDone?.Invoke(ok);
        }
    }
}
