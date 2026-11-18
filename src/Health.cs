using System;
using System.Net;

namespace TinySticky {
    internal static class Health {
        internal static bool Probe(string url, int timeoutMs) {
            if (string.IsNullOrWhiteSpace(url)) return true;
            try {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.Proxy = null;
                request.AllowAutoRedirect = false;
                using (var response = (HttpWebResponse)request.GetResponse())
                    return (int)response.StatusCode >= 200 && (int)response.StatusCode < 300;
            } catch (WebException ex) {
                var response = ex.Response as HttpWebResponse;
                if (response != null) {
                    int code = (int)response.StatusCode;
                    response.Close();
                    return code >= 200 && code < 300;
                }
                return false;
            } catch {
                return false;
            }
        }
    }
}
