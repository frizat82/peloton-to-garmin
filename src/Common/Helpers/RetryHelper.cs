using Serilog;
using System;
using System.Threading.Tasks;

namespace Common.Helpers;

public static class RetryHelper
{
	private static readonly Random _rng = new();

	/// <summary>
	/// Retries <paramref name="action"/> up to <paramref name="maxAttempts"/> times using
	/// exponential backoff with ±50% jitter. Throws on the final failed attempt.
	/// Delays: attempt 1→2s, 2→4s, 3→8s (before jitter), base controlled by <paramref name="baseDelaySeconds"/>.
	/// </summary>
	public static async Task RetryWithBackoffAsync(
		Func<Task> action,
		int maxAttempts = 3,
		double baseDelaySeconds = 2.0,
		ILogger? logger = null,
		string? operationName = null)
	{
		for (int attempt = 1; attempt <= maxAttempts; attempt++)
		{
			try
			{
				await action();
				return;
			}
			catch (Exception ex) when (attempt < maxAttempts)
			{
				var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1) * baseDelaySeconds);
				var jitter = TimeSpan.FromSeconds(delay.TotalSeconds * 0.5 * ((_rng.NextDouble() * 2) - 1));
				var total = delay + jitter;
				logger?.Warning(ex, "{Op} attempt {Attempt}/{Max} failed — retrying in {Delay:F1}s: {Message}",
					operationName ?? "Operation", attempt, maxAttempts, total.TotalSeconds, ex.Message);
				await Task.Delay(total);
			}
		}
	}

	/// <inheritdoc cref="RetryWithBackoffAsync(Func{Task}, int, double, ILogger?, string?)"/>
	public static async Task<T> RetryWithBackoffAsync<T>(
		Func<Task<T>> action,
		int maxAttempts = 3,
		double baseDelaySeconds = 2.0,
		ILogger? logger = null,
		string? operationName = null)
	{
		for (int attempt = 1; attempt <= maxAttempts; attempt++)
		{
			try
			{
				return await action();
			}
			catch (Exception ex) when (attempt < maxAttempts)
			{
				var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1) * baseDelaySeconds);
				var jitter = TimeSpan.FromSeconds(delay.TotalSeconds * 0.5 * ((_rng.NextDouble() * 2) - 1));
				var total = delay + jitter;
				logger?.Warning(ex, "{Op} attempt {Attempt}/{Max} failed — retrying in {Delay:F1}s: {Message}",
					operationName ?? "Operation", attempt, maxAttempts, total.TotalSeconds, ex.Message);
				await Task.Delay(total);
			}
		}

		return await action();
	}
}
