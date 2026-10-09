using Common.Dto;
using Common.Observe;
using Common.Service;
using Common.Stateful;
using Garmin.Auth;
using Microsoft.Extensions.Hosting;
using Prometheus;
using Sync;
using Sync.Database;
using Sync.Dto;
using System.Net.Http;
using static Common.Observe.Metrics;
using ILogger = Serilog.ILogger;
using PromMetrics = Prometheus.Metrics;

namespace Api.Service;

public class BackgroundSyncJob : BackgroundService
{
	private static readonly Histogram SyncHistogram = PromMetrics.CreateHistogram("p2g_sync_duration_seconds", "The histogram of sync jobs that have run.");
	private static readonly Gauge Health = PromMetrics.CreateGauge("p2g_sync_service_health", "Health status for P2G Sync Service.");
	private static readonly Gauge NextSyncTime = PromMetrics.CreateGauge("p2g_next_sync_time", "The next time the sync will run in seconds since epoch.");

	private static readonly ILogger _logger = LogContext.ForClass<BackgroundSyncJob>();

	private readonly ISettingsService _settingsService;
	private readonly ISyncStatusDb _syncStatusDb;
	private readonly ISyncService _syncService;
	private readonly IGarminAuthenticationService _garminAuthService;
	private readonly IHttpClientFactory _httpClientFactory;

	private bool? _previousPollingState;
	private Settings _config;


	public BackgroundSyncJob(ISettingsService settingsService, ISyncStatusDb syncStatusDb, ISyncService syncService, IGarminAuthenticationService garminAuthService, IHttpClientFactory httpClientFactory)
	{
		_settingsService = settingsService;
		_syncStatusDb = syncStatusDb;

		_previousPollingState = null;
		_syncService = syncService;

		_config = new Settings();
		_garminAuthService = garminAuthService;
		_httpClientFactory = httpClientFactory;
	}

	/// <summary>
	/// How often the loop re-checks settings while idle. Settable for tests.
	/// </summary>
	public TimeSpan StepInterval { get; init; } = TimeSpan.FromSeconds(5);

	private static readonly int DefaultPollingIntervalSeconds = new App().PollingIntervalSeconds;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		Health.Set(HealthStatus.Healthy);

		// Yield so host startup is never blocked by the first loop iteration.
		await Task.Yield();

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await RunIterationAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception e)
			{
				// An unhandled exception here would stop the whole API host, so log it and keep polling.
				_logger.Error(e, "Background sync loop failed, will retry.");
				await Task.Delay(StepInterval, stoppingToken);
			}
		}
	}

	private async Task RunIterationAsync(CancellationToken stoppingToken)
	{
		if (await PollingDisabled())
		{
			await Task.Delay(StepInterval, stoppingToken);
			return;
		}

		if (await NeedToWaitForMFAToBeCompletedAsync())
		{
			_logger.Information("Can't start background syncing until MFA flow is completed for the first time.");
			await Task.Delay(StepInterval, stoppingToken);
			return;
		}

		await SyncAsync();
		await WaitForNextSyncAsync(stoppingToken);
	}

	private async Task WaitForNextSyncAsync(CancellationToken stoppingToken)
	{
		var intervalSeconds = SyncServiceState.PollingIntervalSeconds;
		if (intervalSeconds != _config.App.PollingIntervalSeconds)
			_logger.Warning("PollingIntervalSeconds is {Interval}, which is invalid. Using the default of {Default} seconds.", _config.App.PollingIntervalSeconds, intervalSeconds);

		_logger.Information("Sleeping for {@Seconds} seconds...", intervalSeconds);

		for (var waited = TimeSpan.Zero; waited.TotalSeconds < intervalSeconds; waited += StepInterval)
		{
			await Task.Delay(StepInterval, stoppingToken);

			try
			{
				if (await StateChangedAsync()) break;
			}
			catch (Exception e)
			{
				_logger.Warning(e, "Failed to check for settings changes, will keep waiting.");
			}
		}
	}


	private async Task<bool> StateChangedAsync()
	{
		using var tracing = Tracing.Trace($"{nameof(BackgroundService)}.{nameof(StateChangedAsync)}");

		_config = await _settingsService.GetSettingsAsync();
		SyncServiceState.Enabled = _config.App.EnablePolling;
		// A non-positive interval (e.g. from a config file or env var) would sync back-to-back, so fall back to the default.
		SyncServiceState.PollingIntervalSeconds = _config.App.PollingIntervalSeconds > 0
			? _config.App.PollingIntervalSeconds
			: DefaultPollingIntervalSeconds;

		return _previousPollingState != SyncServiceState.Enabled;
	}

	private async Task<bool> PollingDisabled()
	{
		using var tracing = Tracing.Trace($"{nameof(BackgroundService)}.{nameof(PollingDisabled)}");

		if (await StateChangedAsync())
		{
			var syncTime = await _syncStatusDb.GetSyncStatusAsync();
			syncTime.NextSyncTime = SyncServiceState.Enabled ? DateTime.Now : null;
			syncTime.SyncStatus = SyncServiceState.Enabled ? Status.Running : Status.NotRunning;
			await _syncStatusDb.UpsertSyncStatusAsync(syncTime);

			if (SyncServiceState.Enabled) _logger.Information("Sync Service started.");
			else _logger.Information("Sync Service stopped.");
		}

		_previousPollingState = SyncServiceState.Enabled;
		return !SyncServiceState.Enabled;
	}

	private async Task<bool> NeedToWaitForMFAToBeCompletedAsync()
	{
		// _config was just refreshed by PollingDisabled.
		if (_config.Garmin.TwoStepVerificationEnabled)
		{
			var alreadyHaveToken = await _garminAuthService.GarminAuthTokenExistsAndIsValidAsync();
			return !alreadyHaveToken;
		}
		return false;
	}

	private IDiscordNotificationService BuildNotifier()
	{
		var n = _config.Notifications;
		if (string.IsNullOrWhiteSpace(n?.DiscordWebhookUrl))
			return new NullDiscordNotificationService();

		return new DiscordNotificationService(n.DiscordWebhookUrl, n.NotifyOnSuccess, _httpClientFactory.CreateClient());
	}

	private async Task SyncAsync()
	{
		using var tracing = Tracing.Trace($"{nameof(BackgroundService)}.{nameof(SyncAsync)}");

		var notifier = BuildNotifier();

		try
		{
			var result = await _syncService.SyncAsync(_config.Peloton.NumWorkoutsToDownload, forceStackClasses: false);
			if (result.SyncSuccess)
			{
				Health.Set(HealthStatus.Healthy);
				await notifier.SendSuccessAsync(result.MergeResults?.Count ?? 0);
			}
			else
			{
				Health.Set(HealthStatus.UnHealthy);
				var errorMsg = result.Errors?.Count > 0
					? string.Join("; ", System.Linq.Enumerable.Select(result.Errors, e => e.Message))
					: "Unknown error — check logs.";
				await notifier.SendFailureAsync(errorMsg);
			}

		}
		catch (Exception e)
		{
			_logger.Error(e, "Uncaught Exception.");
			Health.Set(HealthStatus.UnHealthy);
			await notifier.SendFailureAsync(e.Message);
		}
		finally
		{
			var now = DateTime.UtcNow;
			var nextRunTime = now.AddSeconds(SyncServiceState.PollingIntervalSeconds);
			NextSyncTime.Set(new DateTimeOffset(nextRunTime).ToUnixTimeSeconds());

			try
			{
				var syncStatus = await _syncStatusDb.GetSyncStatusAsync();
				syncStatus.NextSyncTime = nextRunTime;
				syncStatus.SyncStatus = Health.Value == HealthStatus.UnHealthy ? Status.UnHealthy :
										Status.Running;

				await _syncStatusDb.UpsertSyncStatusAsync(syncStatus);
			}
			catch (Exception e)
			{
				// Swallow so the caller still waits out the polling interval instead of re-syncing immediately.
				_logger.Error(e, "Failed to save sync status.");
			}
		}
	}
}
