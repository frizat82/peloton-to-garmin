using Flurl.Http;
using Polly;
using Polly.NoOp;
using Polly.Retry;
using Serilog;
using System;
using System.Net.Http;

namespace Common.Http;

public static class PollyPolicies
{
	public static AsyncNoOpPolicy<HttpResponseMessage> NoOp = Policy.NoOpAsync<HttpResponseMessage>();

	public static AsyncRetryPolicy<HttpResponseMessage> Retry = Policy
		.HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode && r.StatusCode != System.Net.HttpStatusCode.Unauthorized)
		.Or<FlurlHttpTimeoutException>()
		.WaitAndRetryAsync(new[]
		{
			TimeSpan.FromSeconds(5),
			TimeSpan.FromSeconds(5)
		},
		(result, timeSpan, retryCount, context) =>
		{
			Log.Information("Retry Policy - {@Url} - attempt {@Attempt}", result?.Result?.RequestMessage?.RequestUri, retryCount);
		});
}
