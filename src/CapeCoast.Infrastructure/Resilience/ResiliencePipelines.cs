using CapeCoast.Infrastructure.Sources;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace CapeCoast.Infrastructure.Resilience;

public static class ResiliencePipelines
{
    // Each source builds its own pipeline once, so a breaker that opens for one source
    // never affects the others.
    public static ResiliencePipeline<HttpResponseMessage> ForHttpSource(SourceOptions o)
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = o.Retries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => (int)r.StatusCode >= 500 || (int)r.StatusCode == 429)
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 0.7,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromMinutes(5),
                BreakDuration = TimeSpan.FromMinutes(2),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => (int)r.StatusCode >= 500)
            })
            .AddTimeout(TimeSpan.FromSeconds(o.TimeoutSeconds))
            .Build();
    }
}
