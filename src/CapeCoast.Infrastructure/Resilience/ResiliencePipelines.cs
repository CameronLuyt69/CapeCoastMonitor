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
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();

        // zero retries is a valid setting, but Polly refuses a retry strategy with no attempts
        if (o.Retries > 0)
        {
            builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = o.Retries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => (int)r.StatusCode >= 500 || (int)r.StatusCode == 429)
            });
        }

        return builder
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
