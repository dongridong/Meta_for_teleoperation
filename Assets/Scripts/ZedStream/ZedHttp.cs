using System;
using UnityEngine;
using UnityEngine.Networking;

namespace ZedStream
{
    /// <summary>
    /// The one place that knows how to talk to the Jetson's HTTPS server.
    ///
    /// That server presents a SELF-SIGNED certificate, which UnityWebRequest
    /// rejects by default — and it rejects it *quietly*, so the symptom is "the
    /// stream never starts" with nothing obviously wrong in the log. Every request
    /// in this assembly therefore goes through <see cref="Send"/>, which attaches a
    /// permissive certificate handler.
    ///
    /// The handler is attached ONLY when the request's host matches the host we
    /// were told to trust. That scoping is the whole point: a blanket "accept any
    /// certificate" handler would disable TLS validation for anything this app ever
    /// fetches, and the trust we actually want to express is "this one machine on
    /// this LAN", not "the internet".
    ///
    /// This affects SIGNALLING ONLY. WebRTC's own media path uses DTLS with
    /// self-signed certificates by design and needs nothing from here.
    /// </summary>
    public static class ZedHttp
    {
        sealed class TrustAnyCertificate : CertificateHandler
        {
            // Scoping happens in Send(), which only attaches this to hosts the
            // caller declared trusted; by the time we are here the decision is made.
            protected override bool ValidateCertificate(byte[] certificateData) => true;
        }

        /// <summary>
        /// Sends <paramref name="request"/>, trusting a self-signed certificate if
        /// and only if the request targets <paramref name="trustedHost"/>. Returns
        /// the same request so the caller can read the result off it.
        /// </summary>
        public static UnityWebRequestAsyncOperation Send(UnityWebRequest request, string trustedHost)
        {
            if (HostMatches(request.uri, trustedHost))
            {
                request.certificateHandler = new TrustAnyCertificate();
                request.disposeCertificateHandlerOnDispose = true;
            }
            return request.SendWebRequest();
        }

        static bool HostMatches(Uri uri, string trustedHost)
        {
            if (uri == null || string.IsNullOrEmpty(trustedHost))
                return false;
            return string.Equals(uri.Host, trustedHost, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves a possibly-relative URL against <paramref name="baseUrl"/>.
        /// WHEP servers are allowed to return the session address in the Location
        /// header as an absolute URL or as a path, and MediaMTX returns a path —
        /// DELETEing that path verbatim would fail and leak the session.
        /// </summary>
        public static string ResolveAgainst(string baseUrl, string maybeRelative)
        {
            if (string.IsNullOrEmpty(maybeRelative))
                return null;
            return Uri.TryCreate(new Uri(baseUrl), maybeRelative, out Uri resolved)
                ? resolved.ToString()
                : maybeRelative;
        }

        /// <summary>Host part of a URL, for use as the trusted host.</summary>
        public static string HostOf(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ? uri.Host : null;
        }
    }
}
