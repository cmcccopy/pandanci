using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PandanciClone
{
    internal static class GoogleLegacyWebTransport
    {
        private const string StatusMarker = "\nPANDANCI_HTTP_STATUS:";

        private static string CurlPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe"); }
        }

        public static bool IsAvailable { get { return File.Exists(CurlPath); } }

        public static string Download(string url, IWebProxy proxy, CancellationToken cancellationToken)
        {
            return DownloadRequest(url, "GET", null, null, null, proxy, cancellationToken);
        }

        public static string DownloadRequest(string url, string method, string contentType, string body,
            WebHeaderCollection headers, IWebProxy proxy, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string headerFile = Path.GetTempFileName();
            try
            {
                Uri uri = new Uri(url);
                string proxyAddress = proxy != null && !proxy.IsBypassed(uri) ? proxy.GetProxy(uri).AbsoluteUri : "";
                StringBuilder args = new StringBuilder("--disable --silent --show-error --location --connect-timeout 6 --max-time 15 --max-redirs 2");
                args.Append(" --proto =https --proto-redir =https --user-agent PandanciClone/1.0 --header ").Append(QuoteArgument("Accept: application/json"));
                args.Append(" --dump-header ").Append(QuoteArgument(headerFile));
                args.Append(" --write-out ").Append(QuoteArgument(StatusMarker + "%{http_code}"));
                args.Append(" --noproxy ").Append(QuoteArgument(proxyAddress.Length == 0 ? "*" : ""));
                if (proxyAddress.Length > 0) args.Append(" --proxy ").Append(QuoteArgument(proxyAddress));
                args.Append(" --url ").Append(QuoteArgument(url));
                args.Append(" --request ").Append(QuoteArgument(method));
                if (!string.IsNullOrEmpty(contentType)) args.Append(" --header ").Append(QuoteArgument("Content-Type: " + contentType));
                if (headers != null)
                    foreach (string name in headers.AllKeys) args.Append(" --header ").Append(QuoteArgument(name + ": " + headers[name]));
                if (body != null) args.Append(" --data-binary @-");

                ProcessStartInfo start = new ProcessStartInfo(CurlPath, args.ToString());
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Hidden;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.RedirectStandardInput = body != null;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;
                using (Process process = new Process())
                {
                    process.StartInfo = start;
                    process.Start();
                    Task<string> output = Task.Factory.StartNew(delegate { return process.StandardOutput.ReadToEnd(); });
                    Task<string> error = Task.Factory.StartNew(delegate { return process.StandardError.ReadToEnd(); });
                    using (cancellationToken.Register(delegate { TryKill(process); }))
                    {
                        Stopwatch timer = Stopwatch.StartNew();
                        try
                        {
                            if (body != null)
                            {
                                byte[] bytes = Encoding.UTF8.GetBytes(body);
                                process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                                process.StandardInput.Close();
                            }
                            while (!process.WaitForExit(100))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (timer.ElapsedMilliseconds > 20000)
                                {
                                    TryKill(process);
                                    throw new TimeoutException("Google Web 请求超时，请检查翻译代理和网络。");
                                }
                            }
                            cancellationToken.ThrowIfCancellationRequested();
                            // Drain both pipes concurrently to prevent a child-process deadlock.
                            string response = output.Result;
                            string ignoredError = error.Result;
                            if (process.ExitCode != 0)
                                throw new InvalidOperationException("Google Web 网络请求失败（curl 退出码 " + process.ExitCode.ToString(CultureInfo.InvariantCulture)
                                    + "），请检查翻译代理和网络。");
                            return ParseResponse(response, File.ReadAllText(headerFile));
                        }
                        catch (IOException)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw;
                        }
                        finally
                        {
                            TryKill(process);
                            // Readers must finish before Process.Dispose closes their streams.
                            process.WaitForExit();
                            Task.WaitAll(output, error);
                        }
                    }
                }
            }
            finally
            {
                try { File.Delete(headerFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        internal static string ParseResponse(string output, string headers)
        {
            int marker = output.LastIndexOf(StatusMarker, StringComparison.Ordinal);
            int status;
            if (marker < 0 || !int.TryParse(output.Substring(marker + StatusMarker.Length).Trim(), out status) || status < 100 || status > 599)
                throw new InvalidOperationException("Google Web 返回了无法识别的 HTTP 状态。");
            string retryAfter = "";
            foreach (string line in headers.Split('\n'))
            {
                if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) retryAfter = "";
                if (line.StartsWith("Retry-After:", StringComparison.OrdinalIgnoreCase)) retryAfter = line.Substring(12).Trim();
            }
            if (status < 200 || status >= 300) throw new TranslationHttpException(status, retryAfter);
            return output.Substring(0, marker);
        }

        // ProcessStartInfo invokes curl directly without cmd.exe or PowerShell.
        internal static string QuoteArgument(string value)
        {
            StringBuilder quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                quoted.Append(c);
                slashes = 0;
            }
            quoted.Append('\\', slashes * 2);
            return quoted.Append('"').ToString();
        }
    }
}
