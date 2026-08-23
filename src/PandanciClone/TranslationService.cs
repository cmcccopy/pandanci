using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace PandanciClone
{
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
        private const int MaxBingSegmentLength = 4000;
        private const int MaxCacheEntries = 256;
        private static readonly int[] GoogleRetryDelays = new int[] { 1000, 2000, 4000, 8000 };
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, TranslationResult> TranslationCache = new Dictionary<string, TranslationResult>(StringComparer.Ordinal);
        private static readonly Queue<string> CacheOrder = new Queue<string>();

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
                string cacheKey = BuildCacheKey(provider, target, text);
                TranslationResult cached;
                if (TryGetCachedResult(cacheKey, out cached)) return cached;

                result = provider == TranslationProvider.Bing
                    ? TranslateWithBing(text, target)
                    : TranslateWithGoogle(text, target, cancellationToken);

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

        private TranslationResult TranslateWithGoogle(string text, TranslationTarget target, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(text) && text.Length > MaxGoogleSegmentLength)
            {
                return TranslateLongText(text, target, TranslationProvider.Google, MaxGoogleSegmentLength, cancellationToken);
            }
            return TranslateWithGoogleSingle(text, target, cancellationToken);
        }

        private TranslationResult TranslateWithGoogleSingle(string text, TranslationTarget target, CancellationToken cancellationToken)
        {
            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return TranslateWithGoogleSingleCore(text, target, cancellationToken);
                }
                catch (WebException ex)
                {
                    if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                    if (!IsTooManyRequests(ex)) throw;

                    Exception mobileError = null;
                    try
                    {
                        return TranslateWithGoogleMobile(text, target, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception fallbackException)
                    {
                        mobileError = fallbackException;
                    }

                    if (attempt >= GoogleRetryDelays.Length)
                    {
                        string detail = mobileError == null ? "" : " Mobile 备用接口错误：" + mobileError.Message;
                        throw new InvalidOperationException("Google JSON 接口持续返回 429，Mobile 备用接口也不可用。已按 1、2、4、8 秒退避重试。" + detail, ex);
                    }
                    WaitWithCancellation(GoogleRetryDelays[attempt], cancellationToken);
                }
            }
        }

        private TranslationResult TranslateWithGoogleMobile(string text, TranslationTarget target, CancellationToken cancellationToken)
        {
            string url = "https://translate.google.com/m?sl=auto&tl=" + Uri.EscapeDataString(target.GoogleCode)
                + "&hl=" + Uri.EscapeDataString(target.GoogleCode) + "&q=" + Uri.EscapeDataString(text);
            string html = DownloadString(url, "GET", null, null, null, CreateGoogleProxy(), cancellationToken);
            Match match = Regex.Match(
                html ?? "",
                "<div\\s+class=[\\\"']result-container[\\\"'][^>]*>([\\s\\S]*?)</div>",
                RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                throw new InvalidOperationException("Google Mobile 返回内容中没有找到翻译结果。");
            }

            string translated = Regex.Replace(match.Groups[1].Value, "<[^>]+>", "");
            translated = WebUtility.HtmlDecode(translated).Trim();
            if (string.IsNullOrWhiteSpace(translated))
            {
                throw new InvalidOperationException("Google Mobile 返回了空结果。");
            }

            TranslationResult result = new TranslationResult();
            result.SourceText = text;
            result.TranslatedText = translated;
            result.Provider = "Google";
            result.TargetLanguage = target.GoogleCode;
            result.DirectionText = target.DirectionText + " · Mobile 备用接口";
            return result;
        }

        private TranslationResult TranslateWithGoogleSingleCore(string text, TranslationTarget target, CancellationToken cancellationToken)
        {
            string url = "https://translate.google.com/translate_a/single?dt=at&dt=bd&dt=ex&dt=ld&dt=md&dt=qca&dt=rw&dt=rm&dt=ss&dt=t"
                + "&client=gtx&sl=auto&tl=" + Uri.EscapeDataString(target.GoogleCode)
                + "&hl=" + Uri.EscapeDataString(target.GoogleCode) + "&ie=UTF-8&oe=UTF-8&otf=1&ssel=0&tsel=0&kc=7&q=" + Uri.EscapeDataString(text);
            string json = DownloadString(url, "GET", null, null, null, CreateGoogleProxy(), cancellationToken);
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
            result.DirectionText = target.DirectionText;
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

        private TranslationResult TranslateLongText(string text, TranslationTarget target, TranslationProvider provider, int maxSegmentLength, CancellationToken cancellationToken)
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
                string segmentCacheKey = BuildCacheKey(provider, target, part);
                TranslationResult partResult;
                if (!TryGetCachedResult(segmentCacheKey, out partResult))
                {
                    partResult = provider == TranslationProvider.Bing
                        ? TranslateWithBingSingle(part, target)
                        : TranslateWithGoogleSingle(part, target, cancellationToken);
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

        private static string BuildCacheKey(TranslationProvider provider, TranslationTarget target, string text)
        {
            string language = provider == TranslationProvider.Bing ? target.BingCode : target.GoogleCode;
            return provider.ToString() + "|" + language + "|" + (text ?? "");
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

        private static bool IsTooManyRequests(WebException exception)
        {
            HttpWebResponse response = exception == null ? null : exception.Response as HttpWebResponse;
            return response != null && (int)response.StatusCode == 429;
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

        private static string DownloadString(string url, string method, string contentType, string body, WebHeaderCollection headers, IWebProxy proxy, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                catch (WebException)
                {
                    if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                    throw;
                }
            }
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





