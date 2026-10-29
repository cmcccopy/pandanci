using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PandanciClone
{
    internal static class TranslationServiceTests
    {
        private const string PublicResult = "[[[\"你好\",\"hello\",null,null]],null,\"en\"]";
        private const string CloudResult = "{\"data\":{\"translations\":[{\"translatedText\":\"你好 &amp; 世界\",\"detectedSourceLanguage\":\"en\"}]}}";
        private static int _passed;

        private sealed class Clock
        {
            public DateTime Now = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
            public readonly List<int> Waits = new List<int>();
            public GoogleTranslationRequestPolicy Policy;

            public Clock()
            {
                Policy = new GoogleTranslationRequestPolicy(delegate { return Now; }, delegate(int milliseconds, CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    Waits.Add(milliseconds);
                    Now = Now.AddMilliseconds(milliseconds);
                });
            }
        }

        private static TranslationService Service(TranslationDownload download, Clock clock, bool official)
        {
            TranslationService service = new TranslationService(download, clock.Policy);
            service.GoogleApiKey = official ? "test-key-" + Guid.NewGuid().ToString("N") : "";
            service.GoogleProxyAddress = "http://test-" + Guid.NewGuid().ToString("N") + ".invalid:8080";
            return service;
        }

        private static TranslationResult Translate(TranslationService service, string text)
        {
            return service.Translate(text, TranslationProvider.Google, TranslationLanguageMode.Auto);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Run(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS " + name);
        }

        public static int Main()
        {
            try
            {
                Run("keyless legacy Web endpoint, query encoding and proxy", TestLegacyRequest);
                Run("legacy transport preserves JSON and HTTP Retry-After", TestLegacyTransportResponse);
                Run("legacy transport ignores stale redirect headers", TestLegacyRedirectHeaders);
                Run("legacy process arguments preserve quotes and trailing slashes", TestLegacyArguments);
                Run("public 429 sends once, shares cooldown across service instances", TestPublicCooldown);
                Run("Retry-After seconds is respected, one probe succeeds after cooldown", TestRetryAfter);
                Run("Retry-After HTTP-date, invalid and extreme values", TestRetryAfterParsing);
                Run("repeated rate limits increase cooldown", TestEscalatingCooldown);
                Run("official POST, private key header, JSON body and response parsing", TestOfficialRequest);
                Run("official 403 quota errors enter cooldown", TestQuota403);
                Run("invalid official credentials do not fall back to public endpoints", TestInvalidCredentials);
                Run("successful results remain cached during cooldown", TestCacheDuringCooldown);
                Run("public and official caches and cooldowns are independent", TestRouteIsolation);
                Run("identical simultaneous requests are coalesced", TestConcurrentRequests);
                Run("distinct requests observe global spacing", TestSpacing);
                Run("cancellation interrupts throttling and releases the gate", TestCancellation);
                Run("gateway failures retry at most once", TestGatewayRetry);
                Run("503 honors server cooldown without immediate retry", TestUnavailable);
                Run("long text uses sequential segments and caches completed work", TestLongText);
                Run("long text preserves UTF-16 surrogate pairs", TestSurrogatePairs);
                Run("HTTP transport captures 429 and structured 403 quota errors", TestHttpTransport);
                Run("API key encryption round-trips without storing plaintext", TestCredentialEncryption);
                Console.WriteLine("All " + _passed + " translation tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex);
                return 1;
            }
        }

        private static void TestLegacyRequest()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationService service = Service(delegate(string url, string method, string type, string body, WebHeaderCollection headers, IWebProxy proxy, CancellationToken token)
            {
                calls++;
                Assert(url.StartsWith("https://translate.googleapis.com/translate_a/single?client=gtx&"), "Expected the keyless legacy endpoint, not the rate-limited Web page or Cloud API.");
                Assert(url.Contains("sl=auto") && url.Contains("tl=zh-CN") && url.Contains("dt=t"), "Legacy language parameters are missing.");
                Assert(url.Contains("q=hello%20%26%20world%3F"), "Selected text must be URL encoded.");
                Assert(method == "GET" && body == null && headers == null && !url.Contains("key="), "Keyless mode must not send credentials.");
                Assert(proxy != null, "Legacy mode must honor the configured Google proxy.");
                return PublicResult;
            }, clock, false);
            TranslationResult result = Translate(service, "hello & world?");
            Assert(calls == 1 && result.TranslatedText == "你好" && result.DirectionText.Contains("免密钥"), "Legacy translation or mode label failed.");
        }

        private static void TestLegacyTransportResponse()
        {
            Assert(GoogleLegacyWebTransport.ParseResponse(PublicResult + "\nPANDANCI_HTTP_STATUS:200", "HTTP/2 200\r\n") == PublicResult, "Transport modified JSON.");
            bool caught = false;
            try { GoogleLegacyWebTransport.ParseResponse("limited\nPANDANCI_HTTP_STATUS:429", "HTTP/2 429\r\nRetry-After: 900\r\n"); }
            catch (TranslationHttpException ex) { caught = ex.StatusCode == 429 && ex.RetryAfter == "900"; }
            Assert(caught, "Legacy transport must preserve 429 cooldown headers.");
            caught = false;
            try { GoogleLegacyWebTransport.ParseResponse("unexpected", ""); }
            catch (InvalidOperationException) { caught = true; }
            Assert(caught, "Invalid transport status must not appear as a successful translation.");
        }

        private static void TestLegacyRedirectHeaders()
        {
            bool caught = false;
            try { GoogleLegacyWebTransport.ParseResponse("limited\nPANDANCI_HTTP_STATUS:429", "HTTP/1.1 302 Redirect\r\nRetry-After: 900\r\n\r\nHTTP/2 429\r\n"); }
            catch (TranslationHttpException ex) { caught = ex.StatusCode == 429 && ex.RetryAfter == ""; }
            Assert(caught, "Cooldown must come from the final response, not a redirect or proxy handshake.");
        }

        private static void TestLegacyArguments()
        {
            Assert(GoogleLegacyWebTransport.QuoteArgument("") == "\"\"", "Empty argument was lost.");
            Assert(GoogleLegacyWebTransport.QuoteArgument("a\"b") == "\"a\\\"b\"", "Embedded quote not escaped.");
            Assert(GoogleLegacyWebTransport.QuoteArgument("a b\\") == "\"a b\\\\\"", "Trailing slash not escaped.");
            Assert(GoogleLegacyWebTransport.QuoteArgument("a&b") == "\"a&b\"", "Query separator not preserved.");
        }

        private static void TestPublicCooldown()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationDownload download = delegate { calls++; throw new TranslationHttpException(429, null); };
            TranslationService first = Service(download, clock, false);
            TranslationResult error = Translate(first, "first");
            Assert(error.Error.Contains("300") && calls == 1, "First 429 must open a five-minute cooldown without Mobile/retries.");
            TranslationService second = Service(download, clock, false);
            second.GoogleProxyAddress = first.GoogleProxyAddress;
            Assert(Translate(second, "different").Error.Contains("暂停") && calls == 1, "A new service instance must not bypass cooldown.");
        }

        private static void TestRetryAfter()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationService service = Service(delegate
            {
                if (++calls == 1) throw new TranslationHttpException(429, "900");
                return PublicResult;
            }, clock, false);
            Assert(Translate(service, "first").Error.Contains("900"), "Server cooldown must override the default.");
            clock.Now = clock.Now.AddSeconds(899);
            Assert(Translate(service, "second").Error.Length > 0 && calls == 1, "Must not probe early.");
            clock.Now = clock.Now.AddSeconds(1);
            Assert(Translate(service, "third").TranslatedText == "你好" && calls == 2, "Should probe once after cooldown expires.");
        }

        private static void TestRetryAfterParsing()
        {
            DateTime now = new Clock().Now;
            Assert(GoogleTranslationRequestPolicy.ParseRetryAfter(now.AddMinutes(20).ToString("r", CultureInfo.InvariantCulture), now) == now.AddMinutes(20), "HTTP date failed.");
            Assert(GoogleTranslationRequestPolicy.ParseRetryAfter("garbage", now) == now, "Invalid header should use policy default.");
            Assert(GoogleTranslationRequestPolicy.ParseRetryAfter("-1", now) <= now, "Negative header must not produce a future delay.");
            Assert(GoogleTranslationRequestPolicy.ParseRetryAfter(long.MaxValue.ToString(), now) > now.AddYears(1), "Extreme header overflowed.");
        }

        private static void TestEscalatingCooldown()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationService service = Service(delegate { calls++; throw new TranslationHttpException(429, null); }, clock, false);
            Translate(service, "first");
            clock.Now = clock.Now.AddSeconds(300);
            Assert(Translate(service, "second").Error.Contains("600") && calls == 2, "Repeated 429 must increase cooldown.");
        }

        private static void TestOfficialRequest()
        {
            Clock clock = new Clock();
            string key = "test-secret-" + Guid.NewGuid();
            TranslationService service = Service(delegate(string url, string method, string type, string body, WebHeaderCollection headers, IWebProxy proxy, CancellationToken token)
            {
                Assert(url == "https://translation.googleapis.com/language/translate/v2" && !url.Contains(key), "Use the official endpoint without credentials in the URL.");
                Assert(method == "POST" && type.Contains("application/json"), "Expected JSON POST.");
                Assert(headers["X-Goog-Api-Key"] == key && proxy != null, "Missing API key or configured proxy.");
                Dictionary<string, object> payload = new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>;
                Assert((string)payload["q"] == "hello" && (string)payload["target"] == "zh-CN" && (string)payload["format"] == "text", "Wrong payload.");
                return CloudResult;
            }, clock, true);
            service.GoogleApiKey = key;
            TranslationResult result = Translate(service, "hello");
            Assert(result.TranslatedText == "你好 & 世界" && result.DetectedLanguage == "en" && result.DirectionText.Contains("官方"), "Cloud response not parsed.");
        }

        private static void TestQuota403()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationService service = Service(delegate { calls++; throw new TranslationHttpException(403, "120", "userRateLimitExceeded"); }, clock, true);
            Assert(Translate(service, "first").Error.Contains("120"), "Official quota 403 must be treated as rate limiting.");
            Translate(service, "second");
            Assert(calls == 1, "Quota 403 was retried during cooldown.");
        }

        private static void TestInvalidCredentials()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationService service = Service(delegate { calls++; throw new TranslationHttpException(403, null); }, clock, true);
            TranslationResult result = Translate(service, "hello");
            Assert(calls == 1 && result.Error.Contains("API Key") && !result.Error.Contains(service.GoogleApiKey), "Invalid credentials must report guidance, not try public endpoints or expose secrets.");
        }

        private static void TestCacheDuringCooldown()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationService service = Service(delegate
            {
                if (++calls == 1) return PublicResult;
                throw new TranslationHttpException(429, null);
            }, clock, false);
            Assert(Translate(service, "cached").Error == "", "Initial request failed.");
            Translate(service, "limited");
            Assert(Translate(service, "cached").TranslatedText == "你好" && calls == 2, "Cache hits should work during cooldown without a request.");
        }

        private static void TestRouteIsolation()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationService service = Service(delegate
            {
                calls++;
                if (calls == 1) throw new TranslationHttpException(429, null);
                return CloudResult;
            }, clock, false);
            Translate(service, "same-text");
            service.GoogleApiKey = "new-official-key";
            Assert(Translate(service, "same-text").Error == "" && calls == 2, "Public cooldown must not block official mode.");
            Translate(service, "same-text");
            Assert(calls == 2, "Official response not cached.");
            service.GoogleApiKey = "";
            Assert(Translate(service, "same-text").Error.Length > 0 && calls == 2, "Official cache must not masquerade as public mode.");
        }

        private static void TestConcurrentRequests()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationDownload download = delegate { Interlocked.Increment(ref calls); Thread.Sleep(30); return PublicResult; };
            TranslationService template = Service(download, clock, false);
            Thread[] threads = new Thread[8];
            TranslationResult[] results = new TranslationResult[threads.Length];
            for (int i = 0; i < threads.Length; i++)
            {
                int index = i;
                threads[i] = new Thread(delegate()
                {
                    TranslationService service = Service(download, clock, false);
                    service.GoogleProxyAddress = template.GoogleProxyAddress;
                    results[index] = Translate(service, "concurrent");
                });
                threads[i].Start();
            }
            foreach (Thread thread in threads) Assert(thread.Join(5000), "Concurrent test hung.");
            foreach (TranslationResult result in results) Assert(result != null && result.Error == "", "Concurrent request failed.");
            Assert(calls == 1, "Identical queued requests must share a cached result.");
        }

        private static void TestSpacing()
        {
            Clock clock = new Clock();
            List<DateTime> times = new List<DateTime>();
            TranslationService service = Service(delegate { times.Add(clock.Now); return PublicResult; }, clock, false);
            Translate(service, "first");
            Translate(service, "second");
            Translate(service, "third");
            Assert(times.Count == 3 && (times[1] - times[0]).TotalMilliseconds >= 1500 && (times[2] - times[1]).TotalMilliseconds >= 1500, "Public requests were not spaced globally.");
        }

        private static void TestCancellation()
        {
            DateTime now = new Clock().Now;
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                GoogleTranslationRequestPolicy policy = new GoogleTranslationRequestPolicy(delegate { return now; }, delegate(int ms, CancellationToken token)
                {
                    cancel.Cancel();
                    token.ThrowIfCancellationRequested();
                    now = now.AddMilliseconds(ms);
                });
                int sends = 0;
                Func<TranslationResult> send = delegate { sends++; return new TranslationResult(); };
                policy.Execute("route", false, send, CancellationToken.None);
                bool canceled = false;
                try { policy.Execute("route", false, send, cancel.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && sends == 1, "Cancellation must prevent sending during throttle wait.");
                policy.Execute("other-route", false, send, CancellationToken.None);
                Assert(sends == 2, "Canceled request did not release the gate.");
            }
        }

        private static void TestGatewayRetry()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationService service = Service(delegate
            {
                if (++calls == 1) throw new TranslationHttpException(502, null);
                return PublicResult;
            }, clock, false);
            Assert(Translate(service, "hello").Error == "" && calls == 2, "A transient gateway failure should retry once.");
            calls = 0;
            service = Service(delegate { calls++; throw new TranslationHttpException(504, null); }, clock, false);
            Assert(Translate(service, "other").Error.Length > 0 && calls == 2, "Gateway failures must not retry indefinitely.");
        }

        private static void TestUnavailable()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationService service = Service(delegate { calls++; throw new TranslationHttpException(503, "600"); }, clock, true);
            Assert(Translate(service, "first").Error.Contains("600"), "503 Retry-After not honored.");
            Translate(service, "second");
            Assert(calls == 1, "503 must not trigger another request during cooldown.");
        }

        private static void TestLongText()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationService service = Service(delegate { calls++; return PublicResult; }, clock, false);
            string text = new string('a', 1200) + new string('b', 1200) + new string('c', 100);
            Assert(Translate(service, text).Error == "" && calls == 3, "Expected three sequential public segments.");
            Translate(service, text);
            Assert(calls == 3, "Full long-text result should be cached.");

            int segmentCalls = 0;
            service = Service(delegate
            {
                if (++segmentCalls == 2) throw new TranslationHttpException(429, null);
                return PublicResult;
            }, clock, false);
            Assert(Translate(service, text).Error.Contains("第 2 段") && segmentCalls == 2, "Long text must stop after the first rate-limited segment.");
            clock.Now = clock.Now.AddMinutes(5);
            Assert(Translate(service, text).Error == "" && segmentCalls == 4, "Retry must reuse the first completed segment.");

            int cloudCalls = 0;
            service = Service(delegate { cloudCalls++; return CloudResult; }, clock, true);
            Assert(Translate(service, new string('x', 4000) + new string('y', 1000)).Error == "" && cloudCalls == 2, "Official API should use larger sequential segments.");
        }

        private static void TestSurrogatePairs()
        {
            Clock clock = new Clock();
            int calls = 0;
            TranslationService service = Service(delegate(string url, string method, string type, string body, WebHeaderCollection headers, IWebProxy proxy, CancellationToken token)
            {
                calls++;
                Assert(!url.Contains("%EF%BF%BD"), "A split broke a surrogate pair into replacement characters.");
                return PublicResult;
            }, clock, false);
            Assert(Translate(service, new string('a', 1199) + "\uD83D\uDE00" + "tail").Error == "" && calls == 2, "Surrogate test failed.");
        }

        private static void TestHttpTransport()
        {
            AssertHttpError(429, "Retry-After: 120\r\n", "{}", true);
            AssertHttpError(403, "", "{\"error\":{\"errors\":[{\"reason\":\"userRateLimitExceeded\"}]}}", true);
            AssertHttpError(403, "", "{\"error\":{\"status\":\"PERMISSION_DENIED\"}}", false);
        }

        private static void AssertHttpError(int status, string headers, string body, bool limited)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Exception serverError = null;
            Thread server = new Thread(delegate()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        stream.ReadTimeout = 5000;
                        StreamReader reader = new StreamReader(stream, Encoding.ASCII);
                        while (!string.IsNullOrEmpty(reader.ReadLine())) { }
                        byte[] content = Encoding.UTF8.GetBytes(body);
                        byte[] prefix = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Test\r\nConnection: close\r\nContent-Type: application/json\r\n" + headers + "Content-Length: " + content.Length + "\r\n\r\n");
                        stream.Write(prefix, 0, prefix.Length);
                        stream.Write(content, 0, content.Length);
                    }
                }
                catch (Exception ex) { serverError = ex; }
            });
            server.IsBackground = true;
            server.Start();
            try
            {
                bool caught = false;
                try
                {
                    TranslationService.DownloadString("http://127.0.0.1:" + port + "/", "GET", null, null, null, null, CancellationToken.None);
                }
                catch (TranslationHttpException ex)
                {
                    caught = true;
                    Assert(ex.StatusCode == status && ex.IsRateLimited == limited, "Wrong HTTP classification.");
                    if (status == 429) Assert(ex.RetryAfter == "120", "Retry-After was discarded by transport.");
                }
                Assert(caught, "Expected a structured HTTP exception.");
            }
            finally
            {
                listener.Stop();
                Assert(server.Join(6000), "HTTP test server did not stop.");
            }
            Assert(serverError == null, "HTTP test server failed: " + serverError);
        }

        private static void TestCredentialEncryption()
        {
            string key = "test-key-" + Guid.NewGuid();
            byte[] encrypted = GoogleTranslationCredentials.Encode(key);
            Assert(GoogleTranslationCredentials.Decode(encrypted) == key, "DPAPI round-trip failed.");
            Assert(!Encoding.UTF8.GetString(encrypted).Contains(key), "Key stored as plaintext.");
        }
    }
}
