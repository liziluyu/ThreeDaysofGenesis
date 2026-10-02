using System;
using System.Net;
using UnityEditor;
using UnityEngine;

namespace TapTap2026.Editor
{
    /// <summary>
    /// Keeps Unity's Mono HTTP stack usable when the Windows system proxy holds a value Mono cannot parse.
    ///
    /// <para><b>Failure this guards against.</b> Tools such as dev-sidecar write the WinINET per-protocol form
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ProxyServer = "https=http://127.0.0.1:31181"</c>.
    /// WinINET and .NET Framework accept it, but Mono's proxy engine throws
    /// <c>UriFormatException: Invalid URI: The hostname could not be parsed.</c> while building the proxy —
    /// before the bypass list is consulted, so <c>127.0.0.1</c> is never exempted. Every Mono
    /// <c>WebRequest</c>/<c>HttpClient</c> call in the Editor then fails, which breaks the Unity-MCP SignalR
    /// link (<c>AttemptConnection ... endpoint: /hub/mcp-server</c>) and leaves the MCP server with no tools.</para>
    ///
    /// <para><b>Fix.</b> Pin <see cref="WebRequest.DefaultWebProxy"/> to a direct (no-proxy) instance for this
    /// AppDomain. Detection only runs when the machine proxy fails to parse, so a healthy proxy is left alone —
    /// a machine with a working proxy sees a no-op. The field is per-AppDomain, so this re-arms on every
    /// domain reload without touching the registry (which the proxy tool rewrites anyway).</para>
    /// </summary>
    [InitializeOnLoad]
    internal static class SystemProxyGuard
    {
        /// <summary>Any absolute URI works; it only forces the lazy proxy resolution to run.</summary>
        static readonly Uri Probe = new Uri("http://127.0.0.1/");

        static SystemProxyGuard()
        {
            Apply();
            EditorApplication.delayCall += Apply;
        }

        static void Apply()
        {
            try
            {
                // Reading DefaultWebProxy (or resolving through it) is what triggers Mono's WinINET parse.
                var proxy = WebRequest.DefaultWebProxy;
                if (proxy != null)
                    proxy.GetProxy(Probe);
            }
            catch (Exception ex)
            {
                WebRequest.DefaultWebProxy = new WebProxy();
                Debug.LogWarning(
                    $"[SystemProxyGuard] System proxy is not parseable by Mono ({ex.GetType().Name}); " +
                    "pinned WebRequest.DefaultWebProxy to a direct connection so loopback tools (Unity-MCP) keep working. " +
                    @"Check HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ProxyServer.");
            }
        }
    }
}
