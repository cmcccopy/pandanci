using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PandanciClone
{
    internal delegate string TranslationDownload(string url, string method, string contentType, string body,
        WebHeaderCollection headers, IWebProxy proxy, CancellationToken cancellationToken);

    internal enum TranslationProvider
    {
        Google,
        Bing
    }

    internal enum TranslationLanguageMode
    {
        Auto,
        EnglishToChinese,
        ChineseToEnglish
    }

    internal sealed class TranslationResult
    {
        public string SourceText = "";
        public string TranslatedText = "";
        public string Provider = "";
        public string DetectedLanguage = "";
        public string TargetLanguage = "";
        public string DirectionText = "";
        public string Error = "";
    }

    internal sealed class TranslationTarget
    {
        public readonly string GoogleCode;
        public readonly string BingCode;
        public readonly string DirectionText;

        public TranslationTarget(string googleCode, string bingCode, string directionText)
        {
            GoogleCode = googleCode;
            BingCode = bingCode;
            DirectionText = directionText;
        }
    }

    internal sealed class TranslationService
    {
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private TranslationProvider _provider = TranslationProvider.Google;
        private string _googleProxyAddress = "";
        private const int MaxGoogleSegmentLength = 1200;
        private const int MaxGoogleCloudSegmentLength = 4000;
        private const int MaxBingSegmentLength = 4000;
        private const int MaxCacheEntries = 256;
        private static readonly GoogleTranslationRequestPolicy SharedGooglePolicy = new GoogleTranslationRequestPolicy();
        private readonly GoogleTranslationRequestPolicy _googlePolicy;
        private readonly TranslationDownload _download;
        // null loads the local encrypted credential/environment; an empty override uses public mode.
        public string GoogleApiKey { get; set; }
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, TranslationResult> TranslationCache = new Dictionary<string, TranslationResult>(StringComparer.Ordinal);
        private static readonly Queue<string> CacheOrder = new Queue<string>();

        public TranslationService() : this(DownloadString, SharedGooglePolicy)
        {
        }

        internal TranslationService(TranslationDownload download, GoogleTranslationRequestPolicy googlePolicy)
        {
            _download = download;
            _googlePolicy = googlePolicy;
        }

        public TranslationProvider Provider
        {
            get { return _provider; }
            set { _provider = value; }
        }

        public string GoogleProxyAddress
        {
            get { return _googleProxyAddress; }
            set { _googleProxyAddress = value == null ? "" : value.Trim(); }
        }

        public TranslationResult Translate(string text)
        {
            return Translate(text, _provider);
        }

        public TranslationResult Translate(string text, TranslationProvider provider)
        {
            return Translate(text, provider, TranslationLanguageMode.Auto);
        }

        public TranslationResult Translate(string text, TranslationProvider provider, TranslationLanguageMode languageMode)
        {
            return Translate(text, provider, languageMode, CancellationToken.None);
        }

        public TranslationResult Translate(string text, TranslationProvider provider, TranslationLanguageMode languageMode, CancellationToken cancellationToken)
        {
            TranslationResult result = new TranslationResult();
            result.SourceText = text ?? "";
            result.Provider = provider.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                result.Error = "没有检测到选中文本。";
                return result;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                TranslationTarget target = DetectTarget(text, languageMode);
                string apiKey = provider == TranslationProvider.Google ? GoogleApiKey ?? GoogleTranslationCredentials.LoadApiKey() : "";
                string cacheKey = BuildCacheKey(provider, target, text, apiKey);
                TranslationResult cached;
                if (TryGetCachedResult(cacheKey, out cached)) return cached;

                result = provider == TranslationProvider.Bing
                    ? TranslateWithBing(text, target)
                    : TranslateWithGoogle(text, target, apiKey, cancellationToken);

                if (string.IsNullOrWhiteSpace(result.Error)) StoreCachedResult(cacheKey, result);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        private TranslationResult TranslateWithGoogle(string text, TranslationTarget target, string apiKey, CancellationToken cancellationToken)
        {
            int segmentLength = string.IsNullOrWhiteSpace(apiKey) ? MaxGoogleSegmentLength : MaxGoogleCloudSegmentLength;
            if (!string.IsNullOrEmpty(text) && text.Length > segmentLength)
            {
                return TranslateLongText(text, target, TranslationProvider.Google, segmentLength, cancellationToken, apiKey);
            }
            return TranslateWithGoogleSingle(text, target, apiKey, cancellationToken);
        }

        private TranslationResult TranslateWithGoogleSingle(string text, TranslationTarget target, string apiKey, CancellationToken cancellationToken)
        {
            string cacheKey = BuildCacheKey(TranslationProvider.Google, target, text, apiKey);
            bool official = !string.IsNullOrWhiteSpace(apiKey);
            try
            {
                return _googlePolicy.Execute(GetGoogleRoute(apiKey), official, delegate
                {
                    // Another caller may have populated this entry while this call was queued.
                    TranslationResult cached;
                    if (TryGetCachedResult(cacheKey, out cached)) return cached;
                    TranslationResult result = official
                        ? TranslateWithGoogleCloud(text, target, apiKey, cancellationToken)
                        : TranslateWithGoogleSingleCore(text, target, cancellationToken);
                    if (string.IsNullOrWhiteSpace(result.Error)) StoreCachedResult(cacheKey, result);
                    return result;
                }, cancellationToken);
            }
            catch (TranslationHttpException ex)
            {
                if (official && (ex.StatusCode == 400 || ex.StatusCode == 401 || ex.StatusCode == 403))
                    throw new InvalidOperationException("Google 官方接口拒绝请求（HTTP " + ex.StatusCode
                        + "）。请检查 API Key、Cloud Translation API 是否已启用、结算、配额和密钥限制。不会自动切回公共接口。");
                throw;
            }
        }

        private TranslationResult TranslateWithGoogleCloud(string text, TranslationTarget target, string apiKey, CancellationToken cancellationToken)
        {
            WebHeaderCollection headers = new WebHeaderCollection();
            headers["X-Goog-Api-Key"] = apiKey;
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["q"] = text;
            payload["target"] = target.GoogleCode;
            payload["format"] = "text";
            string json = _download("https://translation.googleapis.com/language/translate/v2", "POST",
                "application/json; charset=utf-8", _serializer.Serialize(payload), headers, CreateGoogleProxy(), cancellationToken);
            Dictionary<string, object> root = _serializer.DeserializeObject(json) as Dictionary<string, object>;
            TranslationResult result = new TranslationResult();
            result.SourceText = text;
            result.Provider = "Google";
            result.TargetLanguage = target.GoogleCode;
            result.DirectionText = target.DirectionText + " · Google 官方接口";
            object dataObject;
            Dictionary<string, object> data = root != null && root.TryGetValue("data", out dataObject)
                ? dataObject as Dictionary<string, object> : null;
            object translationsObject;
            object[] translations = data != null && data.TryGetValue("translations", out translationsObject)
                ? translationsObject as object[] : null;
            Dictionary<string, object> first = translations != null && translations.Length > 0
                ? translations[0] as Dictionary<string, object> : null;
            object value;
            if (first != null && first.TryGetValue("translatedText", out value))
                result.TranslatedText = WebUtility.HtmlDecode(Convert.ToString(value));
            if (first != null && first.TryGetValue("detectedSourceLanguage", out value)) result.DetectedLanguage = Convert.ToString(value);
            if (string.IsNullOrWhiteSpace(result.TranslatedText)) result.Error = "Google 官方接口返回了空结果。";
            return result;
        }

        private TranslationResult TranslateWithGoogleSingleCore(string text, TranslationTarget target, CancellationToken cancellationToken)
        {
            // Legacy Web endpoint: the googleapis.com hostname does not make this the paid Cloud API.
            string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&dt=t"
                + "&tl=" + Uri.EscapeDataString(target.GoogleCode)
                + "&ie=UTF-8&oe=UTF-8&q=" + Uri.EscapeDataString(text);
            string json = _download(url, "GET", null, null, null, CreateGoogleProxy(), cancellationToken);
            object[] root = _serializer.DeserializeObject(json) as object[];
            StringBuilder translated = new StringBuilder();
            if (root != null && root.Length > 0)
            {
                object[] sentences = root[0] as object[];
                if (sentences != null)
                {
                    foreach (object sentence in sentences)
                    {
                        object[] fields = sentence as object[];
                        if (fields != null && fields.Length > 0 && fields[0] != null) translated.Append(Convert.ToString(fields[0]));
                    }
                }
            }

            TranslationResult result = new TranslationResult();
            result.SourceText = text;
            result.TranslatedText = translated.ToString();
            result.Provider = "Google";
            result.TargetLanguage = target.GoogleCode;
            result.DirectionText = target.DirectionText + " · Google Web（免密钥）";
            if (root != null && root.Length > 2 && root[2] != null) result.DetectedLanguage = Convert.ToString(root[2]);
            if (string.IsNullOrWhiteSpace(result.TranslatedText)) result.Error = "Google 返回了空结果。";
            return result;
        }

        private TranslationResult TranslateWithBing(string text, TranslationTarget target)
        {
            if (!string.IsNullOrEmpty(text) && text.Length > MaxBingSegmentLength)
            {
                return TranslateLongText(text, target, TranslationProvider.Bing, MaxBingSegmentLength, CancellationToken.None);
            }
            return TranslateWithBingSingle(text, target);
        }

        private TranslationResult TranslateWithBingSingle(string text, TranslationTarget target)
        {
            TranslationResult edgeResult = TryTranslateWithBingEdge(text, target);
            if (string.IsNullOrWhiteSpace(edgeResult.Error)) return edgeResult;

            string key = Environment.GetEnvironmentVariable("PANDANCI_BING_TRANSLATOR_KEY");
            string region = Environment.GetEnvironmentVariable("PANDANCI_BING_TRANSLATOR_REGION");
            if (string.IsNullOrWhiteSpace(key))
            {
                edgeResult.Error = "Bing Edge 接口不可用：" + edgeResult.Error + Environment.NewLine + "可配置 PANDANCI_BING_TRANSLATOR_KEY 使用官方接口。";
                return edgeResult;
            }

            string body = _serializer.Serialize(new object[] { new TextPayload(text) });
            string url = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + Uri.EscapeDataString(target.BingCode);
            WebHeaderCollection headers = new WebHeaderCollection();
            headers["Ocp-Apim-Subscription-Key"] = key;
            if (!string.IsNullOrWhiteSpace(region)) headers["Ocp-Apim-Subscription-Region"] = region;
            string json = DownloadString(url, "POST", "application/json; charset=utf-8", body, headers);

            return ParseMicrosoftTranslationJson(json, text, "Bing", target);
        }

        private TranslationResult TryTranslateWithBingEdge(string text, TranslationTarget target)
        {
            TranslationResult result = new TranslationResult();
            result.SourceText = text;
            result.Provider = "Bing";
            result.TargetLanguage = target.BingCode;
            result.DirectionText = target.DirectionText;
            try
            {
                string token = DownloadBingEdgeToken();
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("获取 Edge token 失败。");
                string body = _serializer.Serialize(new object[] { new TextPayload(text) });
                string url = "https://api-edge.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + Uri.EscapeDataString(target.BingCode) + "&includeSentenceLength=true";
                string json = DownloadBingEdgeTranslate(url, token.Trim(), body);
                return ParseMicrosoftTranslationJson(json, text, "Bing", target);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        private TranslationResult TranslateLongText(string text, TranslationTarget target, TranslationProvider provider, int maxSegmentLength, CancellationToken cancellationToken, string apiKey = "")
        {
            List<string> parts = SplitTextForTranslation(text, maxSegmentLength);
            TranslationResult result = new TranslationResult();
            result.SourceText = text;
            result.Provider = provider.ToString();
            result.TargetLanguage = provider == TranslationProvider.Bing ? target.BingCode : target.GoogleCode;
            result.DirectionText = target.DirectionText;

            StringBuilder translated = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (i > 0) WaitWithCancellation(GetSegmentDelayMilliseconds(i), cancellationToken);

                string part = parts[i];
                string segmentCacheKey = BuildCacheKey(provider, target, part, apiKey);
                TranslationResult partResult;
                if (!TryGetCachedResult(segmentCacheKey, out partResult))
                {
                    try
                    {
                        partResult = provider == TranslationProvider.Bing
                            ? TranslateWithBingSingle(part, target)
                            : TranslateWithGoogleSingle(part, target, apiKey, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { partResult = new TranslationResult { Error = ex.Message }; }
                    if (string.IsNullOrWhiteSpace(partResult.Error)) StoreCachedResult(segmentCacheKey, partResult);
                }

                if (!string.IsNullOrWhiteSpace(partResult.Error))
                {
                    result.Error = provider + " 长文本第 " + (i + 1).ToString() + " 段翻译失败：" + partResult.Error;
                    return result;
                }

                string partText = partResult.TranslatedText == null ? "" : partResult.TranslatedText.Trim();
                if (partText.Length == 0) continue;
                if (translated.Length > 0) translated.AppendLine();
                translated.Append(partText);
                result.DirectionText = partResult.DirectionText;

                if (string.IsNullOrWhiteSpace(result.DetectedLanguage) && !string.IsNullOrWhiteSpace(partResult.DetectedLanguage))
                {
                    result.DetectedLanguage = partResult.DetectedLanguage;
                }
            }

            result.TranslatedText = translated.ToString();
            if (string.IsNullOrWhiteSpace(result.TranslatedText)) result.Error = provider + " 返回了空结果。";
            return result;
        }

        private static List<string> SplitTextForTranslation(string text, int maxLength)
        {
            List<string> parts = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return parts;

            int start = 0;
            while (start < text.Length)
            {
                while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
                if (start >= text.Length) break;

                int end = Math.Min(text.Length, start + maxLength);
                if (end < text.Length)
                {
                    end = FindSplitPosition(text, start, end);
                }

                if (end <= start) end = Math.Min(text.Length, start + maxLength);
                if (end < text.Length && end > start && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
                string part = text.Substring(start, end - start).Trim();
                if (part.Length > 0) parts.Add(part);
                start = end;
            }

            return parts;
        }

        private static int FindSplitPosition(string text, int start, int end)
        {
            int min = start + Math.Min(200, Math.Max(1, (end - start) / 3));

            for (int i = end - 1; i > min; i--)
            {
                char c = text[i];
                if (c == '\r' || c == '\n') return i + 1;
            }

            for (int i = end - 1; i > min; i--)
            {
                char c = text[i];
                if (c == '.' || c == '!' || c == '?' || c == ';' || c == ':' || c == '。' || c == '！' || c == '？' || c == '；') return i + 1;
            }

            for (int i = end - 1; i > min; i--)
            {
                if (char.IsWhiteSpace(text[i])) return i + 1;
            }

            return end;
        }
        private TranslationResult ParseMicrosoftTranslationJson(string json, string text, string provider, TranslationTarget target)
        {
            object[] root = _serializer.DeserializeObject(json) as object[];
            TranslationResult result = new TranslationResult();
            result.SourceText = text;
            result.Provider = provider;
            result.TargetLanguage = target.BingCode;
            result.DirectionText = target.DirectionText;
            if (root != null && root.Length > 0)
            {
                object item = root[0];
                System.Collections.Generic.Dictionary<string, object> map = item as System.Collections.Generic.Dictionary<string, object>;
                if (map != null)
                {
                    object translationsObj;
                    if (map.TryGetValue("translations", out translationsObj))
                    {
                        object[] translations = translationsObj as object[];
                        if (translations != null && translations.Length > 0)
                        {
                            System.Collections.Generic.Dictionary<string, object> first = translations[0] as System.Collections.Generic.Dictionary<string, object>;
                            object translated;
                            if (first != null && first.TryGetValue("text", out translated)) result.TranslatedText = Convert.ToString(translated);
                        }
                    }
                    object detectedObj;
                    if (map.TryGetValue("detectedLanguage", out detectedObj))
                    {
                        System.Collections.Generic.Dictionary<string, object> detected = detectedObj as System.Collections.Generic.Dictionary<string, object>;
                        object language;
                        if (detected != null && detected.TryGetValue("language", out language)) result.DetectedLanguage = Convert.ToString(language);
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(result.TranslatedText)) result.Error = provider + " 返回了空结果。";
            return result;
        }

        private string BuildCacheKey(TranslationProvider provider, TranslationTarget target, string text, string apiKey)
        {
            string language = provider == TranslationProvider.Bing ? target.BingCode : target.GoogleCode;
            string route = provider == TranslationProvider.Google ? GetGoogleRoute(apiKey) : "Bing";
            return route + "|" + language + "|" + (text ?? "");
        }

        private string GetGoogleRoute(string apiKey)
        {
            string identity = (apiKey ?? "") + "|" + (_googleProxyAddress ?? "").Trim();
            using (SHA256 hash = SHA256.Create())
            {
                return (string.IsNullOrWhiteSpace(apiKey) ? "GoogleLegacy|" : "GoogleCloud|")
                    + Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(identity)));
            }
        }

        private static bool TryGetCachedResult(string key, out TranslationResult result)
        {
            lock (CacheLock)
            {
                TranslationResult cached;
                if (TranslationCache.TryGetValue(key, out cached))
                {
                    result = CloneResult(cached);
                    return true;
                }
            }
            result = null;
            return false;
        }

        private static void StoreCachedResult(string key, TranslationResult result)
        {
            if (result == null || !string.IsNullOrWhiteSpace(result.Error)) return;
            lock (CacheLock)
            {
                if (!TranslationCache.ContainsKey(key))
                {
                    CacheOrder.Enqueue(key);
                }
                TranslationCache[key] = CloneResult(result);
                while (TranslationCache.Count > MaxCacheEntries && CacheOrder.Count > 0)
                {
                    TranslationCache.Remove(CacheOrder.Dequeue());
                }
            }
        }

        private static TranslationResult CloneResult(TranslationResult source)
        {
            TranslationResult clone = new TranslationResult();
            clone.SourceText = source.SourceText;
            clone.TranslatedText = source.TranslatedText;
            clone.Provider = source.Provider;
            clone.DetectedLanguage = source.DetectedLanguage;
            clone.TargetLanguage = source.TargetLanguage;
            clone.DirectionText = source.DirectionText;
            clone.Error = source.Error;
            return clone;
        }

        private static int GetSegmentDelayMilliseconds(int segmentIndex)
        {
            return 300 + ((segmentIndex * 173) % 501);
        }

        private static void WaitWithCancellation(int milliseconds, CancellationToken cancellationToken)
        {
            if (milliseconds <= 0) return;
            if (!cancellationToken.CanBeCanceled)
            {
                Thread.Sleep(milliseconds);
                return;
            }
            if (cancellationToken.WaitHandle.WaitOne(milliseconds))
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        public static TranslationTarget DetectTarget(string text)
        {
            return DetectTarget(text, TranslationLanguageMode.Auto);
        }

        public static TranslationTarget DetectTarget(string text, TranslationLanguageMode languageMode)
        {
            if (languageMode == TranslationLanguageMode.EnglishToChinese)
            {
                return new TranslationTarget("zh-CN", "zh-Hans", "English -> 简体中文");
            }
            if (languageMode == TranslationLanguageMode.ChineseToEnglish)
            {
                return new TranslationTarget("en", "en", "简体中文 -> English");
            }

            return ContainsChinese(text)
                ? new TranslationTarget("en", "en", "简体中文 -> English")
                : new TranslationTarget("zh-CN", "zh-Hans", "English -> 简体中文");
        }

        private static bool ContainsChinese(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF))
                {
                    return true;
                }
            }
            return false;
        }

        private static string DownloadString(string url, string method, string contentType, string body)
        {
            return DownloadString(url, method, contentType, body, null);
        }

        private static string DownloadString(string url, string method, string contentType, string body, WebHeaderCollection headers)
        {
            return DownloadString(url, method, contentType, body, headers, null);
        }

        private static string DownloadString(string url, string method, string contentType, string body, WebHeaderCollection headers, IWebProxy proxy)
        {
            return DownloadString(url, method, contentType, body, headers, proxy, CancellationToken.None);
        }

        internal static string DownloadString(string url, string method, string contentType, string body, WebHeaderCollection headers, IWebProxy proxy, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri = new Uri(url);
            if (method == "GET" && uri.Scheme == "https" && uri.Host == "translate.googleapis.com"
                && uri.AbsolutePath == "/translate_a/single" && GoogleLegacyWebTransport.IsAvailable)
                return GoogleLegacyWebTransport.Download(url, proxy, cancellationToken);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.UserAgent = GetEdgeUserAgent();
            request.Accept = "application/json, text/javascript, */*; q=0.01";
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.Proxy = proxy;
            if (headers != null) request.Headers.Add(headers);
            if (!string.IsNullOrEmpty(contentType)) request.ContentType = contentType;

            using (CancellationTokenRegistration registration = cancellationToken.Register(delegate { request.Abort(); }))
            {
                try
                {
                    if (!string.IsNullOrEmpty(body))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        request.ContentLength = bytes.Length;
                        using (Stream stream = request.GetRequestStream())
                        {
                            stream.Write(bytes, 0, bytes.Length);
                        }
                    }

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
                catch (WebException ex)
                {
                    if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                    HttpWebResponse response = ex.Response as HttpWebResponse;
                    if (response != null)
                    {
                        using (response)
                        {
                            string reason = (int)response.StatusCode == 403 ? ReadHttpErrorReason(response) : "";
                            throw new TranslationHttpException((int)response.StatusCode, response.Headers["Retry-After"], reason);
                        }
                    }
                    throw;
                }
            }
        }

        private static string ReadHttpErrorReason(HttpWebResponse response)
        {
            try
            {
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    char[] buffer = new char[16384];
                    int total = 0;
                    int count;
                    while (total < buffer.Length && (count = reader.Read(buffer, total, buffer.Length - total)) > 0) total += count;
                    Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(new string(buffer, 0, total)) as Dictionary<string, object>;
                    object value;
                    Dictionary<string, object> error = root != null && root.TryGetValue("error", out value) ? value as Dictionary<string, object> : null;
                    object[] errors = error != null && error.TryGetValue("errors", out value) ? value as object[] : null;
                    Dictionary<string, object> first = errors != null && errors.Length > 0 ? errors[0] as Dictionary<string, object> : null;
                    if (first != null && first.TryGetValue("reason", out value)) return Convert.ToString(value);
                    if (error != null && error.TryGetValue("status", out value)) return Convert.ToString(value);
                }
            }
            catch (Exception) { }
            return "";
        }

        private IWebProxy CreateGoogleProxy()
        {
            if (string.IsNullOrWhiteSpace(_googleProxyAddress)) return null;
            string address = _googleProxyAddress.Trim();
            if (address.IndexOf("://", StringComparison.Ordinal) < 0) address = "http://" + address;
            return new WebProxy(address);
        }

        private static string DownloadBingEdgeToken()
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://edge.microsoft.com/translate/auth");
            request.Method = "GET";
            request.UserAgent = GetEdgeUserAgent();
            request.Timeout = 6000;
            request.ReadWriteTimeout = 6000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static string DownloadBingEdgeTranslate(string url, string token, string body)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.UserAgent = GetEdgeUserAgent();
            request.Accept = "application/json, text/javascript, */*; q=0.01";
            request.Timeout = 6000;
            request.ReadWriteTimeout = 6000;
            request.Referer = "https://appsumo.com/";
            request.ContentType = "application/json";
            request.Headers["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.8";
            request.Headers["Authorization"] = "Bearer " + token;
            request.Headers["Cache-Control"] = "no-cache";
            request.Headers["Pragma"] = "no-cache";
            request.Headers["sec-ch-ua"] = "\"Microsoft Edge\";v=\"113\", \"Chromium\";v=\"113\", \"Not-A.Brand\";v=\"24\"";
            request.Headers["sec-ch-ua-mobile"] = "?0";
            request.Headers["sec-ch-ua-platform"] = "\"Windows\"";
            request.Headers["sec-fetch-dest"] = "empty";
            request.Headers["sec-fetch-mode"] = "cors";
            request.Headers["sec-fetch-site"] = "cross-site";
            if (!string.IsNullOrEmpty(body))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
            }

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static string GetEdgeUserAgent()
        {
            return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/113.0.0.0 Safari/537.36 Edg/113.0.1774.42";
        }

        private sealed class TextPayload
        {
            public string Text;

            public TextPayload(string text)
            {
                Text = text;
            }
        }
    }
}





