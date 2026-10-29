using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace PandanciClone
{
    // Preserve only the information needed for retry decisions, never a URL or API key.
    internal sealed class TranslationHttpException : Exception
    {
        public readonly int StatusCode;
        public readonly string RetryAfter;
        public readonly bool IsRateLimited;

        public TranslationHttpException(int statusCode, string retryAfter, string reason = "")
            : base("翻译服务返回 HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + "。")
        {
            StatusCode = statusCode;
            RetryAfter = retryAfter;
            IsRateLimited = statusCode == 429 || (statusCode == 403 &&
                (reason == "rateLimitExceeded" || reason == "userRateLimitExceeded" || reason == "dailyLimitExceeded"
                || reason == "quotaExceeded" || reason == "RESOURCE_EXHAUSTED" || reason == "RATE_LIMIT_EXCEEDED"));
        }
    }

    internal sealed class GoogleTranslationRequestPolicy
    {
        private sealed class RouteState
        {
            public DateTime NextRequestUtc;
            public DateTime RetryNotBeforeUtc;
            public int RateLimitCount;
        }

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, RouteState> _routes = new Dictionary<string, RouteState>(StringComparer.Ordinal);
        private readonly Func<DateTime> _utcNow;
        private readonly Action<int, CancellationToken> _wait;

        public GoogleTranslationRequestPolicy()
            : this(delegate { return DateTime.UtcNow; }, WaitWithCancellation)
        {
        }

        internal GoogleTranslationRequestPolicy(Func<DateTime> utcNow, Action<int, CancellationToken> wait)
        {
            _utcNow = utcNow;
            _wait = wait;
        }

        public TranslationResult Execute(string route, bool official, Func<TranslationResult> send, CancellationToken cancellationToken)
        {
            _gate.Wait(cancellationToken);
            try
            {
                RouteState state;
                if (!_routes.TryGetValue(route, out state))
                {
                    state = new RouteState();
                    _routes.Add(route, state);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (state.RetryNotBeforeUtc > _utcNow()) throw CreateCooldownError(state, official);

                for (int attempt = 0; ; attempt++)
                {
                    double delay = (state.NextRequestUtc - _utcNow()).TotalMilliseconds;
                    if (delay > 0) _wait((int)Math.Ceiling(delay), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    state.NextRequestUtc = _utcNow().AddMilliseconds(official ? 200 : 1500);
                    try
                    {
                        TranslationResult result = send();
                        state.RateLimitCount = 0;
                        state.RetryNotBeforeUtc = DateTime.MinValue;
                        return result;
                    }
                    catch (TranslationHttpException ex)
                    {
                        if (ex.IsRateLimited || ex.StatusCode == 503)
                        {
                            state.RateLimitCount = Math.Min(4, state.RateLimitCount + 1);
                            int baseSeconds = ex.StatusCode == 503 ? 60 : (official ? 60 : 300);
                            double seconds = Math.Min(1800, baseSeconds * Math.Pow(2, state.RateLimitCount - 1));
                            DateTime retryAt = _utcNow().AddSeconds(seconds);
                            DateTime serverRetryAt = ParseRetryAfter(ex.RetryAfter, _utcNow());
                            state.RetryNotBeforeUtc = serverRetryAt > retryAt ? serverRetryAt : retryAt;
                            throw CreateCooldownError(state, official);
                        }
                        // Only transient gateway failures get one retry. A 429 never does.
                        if (attempt == 0 && (ex.StatusCode == 502 || ex.StatusCode == 504))
                        {
                            _wait(1000, cancellationToken);
                            continue;
                        }
                        throw;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private InvalidOperationException CreateCooldownError(RouteState state, bool official)
        {
            double seconds = Math.Ceiling((state.RetryNotBeforeUtc - _utcNow()).TotalSeconds);
            string reason = official
                ? "Google 官方接口限流或暂时不可用，请检查 Cloud Translation 的配额、用量和结算状态。"
                : "Google 公共接口已限流或暂时不可用。稳定使用请在“设置 → Google 官方接口...”配置自己的 API Key。";
            return new InvalidOperationException(reason + " 将暂停请求约 " + Math.Max(1, seconds).ToString("0", CultureInfo.InvariantCulture)
                + " 秒，避免重复触发限制；期间可使用右侧 Bing 结果。");
        }

        internal static DateTime ParseRetryAfter(string value, DateTime now)
        {
            long seconds;
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) && seconds >= 0)
            {
                double safeSeconds = Math.Min(seconds, (DateTime.MaxValue - now).TotalSeconds - 1);
                return now.AddSeconds(safeSeconds);
            }
            DateTimeOffset date;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)) return date.UtcDateTime;
            return now;
        }

        private static void WaitWithCancellation(int milliseconds, CancellationToken cancellationToken)
        {
            if (cancellationToken.WaitHandle.WaitOne(milliseconds)) cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
