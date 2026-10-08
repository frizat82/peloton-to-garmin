using Common.Observe;
using Garmin;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace Api.Service;

/// <summary>
/// Periodically checks merged FIT uploads and re-uploads any that Garmin stored without cadence and power.
/// </summary>
public class MergeVerificationJob : BackgroundService
{
	private static readonly ILogger _logger = LogContext.ForClass<MergeVerificationJob>();
	private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

	private readonly IGarminMergeVerificationService _verificationService;

	public MergeVerificationJob(IGarminMergeVerificationService verificationService)
	{
		_verificationService = verificationService;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using var timer = new PeriodicTimer(Interval);
		while (await timer.WaitForNextTickAsync(stoppingToken))
		{
			try
			{
				await _verificationService.VerifyPendingAsync();
			}
			catch (Exception e)
			{
				_logger.Error(e, "Merge check run failed.");
			}
		}
	}
}
