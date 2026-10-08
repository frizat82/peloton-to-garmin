using Api.Service;
using Common.Dto;
using Common.Service;
using FluentAssertions;
using Garmin.Auth;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using Sync;
using Sync.Database;
using Sync.Dto;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTests.Api.Service;

public class BackgroundSyncJobTests
{
	private static (AutoMocker Mocker, BackgroundSyncJob Job) CreateJob()
	{
		var mocker = new AutoMocker();
		mocker.GetMock<ISyncStatusDb>()
			.Setup(x => x.GetSyncStatusAsync())
			.ReturnsAsync(() => new SyncServiceStatus());

		var job = new BackgroundSyncJob(
			mocker.Get<ISettingsService>(),
			mocker.Get<ISyncStatusDb>(),
			mocker.Get<ISyncService>(),
			mocker.Get<IGarminAuthenticationService>(),
			mocker.Get<IHttpClientFactory>())
		{
			StepInterval = TimeSpan.FromMilliseconds(10),
		};

		return (mocker, job);
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var sw = Stopwatch.StartNew();
		while (!condition() && sw.Elapsed < TimeSpan.FromSeconds(5))
			await Task.Delay(10);
	}

	[Test]
	public async Task When_SettingsLookupThrows_Should_KeepPolling_InsteadOfStoppingTheHost()
	{
		var (mocker, job) = CreateJob();
		var calls = 0;
		mocker.GetMock<ISettingsService>()
			.Setup(x => x.GetSettingsAsync())
			.ReturnsAsync(() =>
			{
				if (Interlocked.Increment(ref calls) == 1)
					throw new InvalidOperationException("settings db unavailable");
				return new Settings();
			});

		await job.StartAsync(CancellationToken.None);
		await WaitUntilAsync(() => Volatile.Read(ref calls) >= 3);
		await job.StopAsync(CancellationToken.None);

		Volatile.Read(ref calls).Should().BeGreaterThanOrEqualTo(3);
		job.ExecuteTask!.IsFaulted.Should().BeFalse();
	}

	[Test]
	public async Task When_WaitingForNextSync_Should_StopPromptly_OnShutdown()
	{
		var (mocker, job) = CreateJob();
		var settings = new Settings();
		settings.App.EnablePolling = true;
		settings.App.PollingIntervalSeconds = 3600;
		mocker.GetMock<ISettingsService>().Setup(x => x.GetSettingsAsync()).ReturnsAsync(settings);

		var synced = new TaskCompletionSource();
		mocker.GetMock<ISyncService>()
			.Setup(x => x.SyncAsync(It.IsAny<int>(), It.IsAny<bool>()))
			.ReturnsAsync(() =>
			{
				synced.TrySetResult();
				return new SyncResult() { SyncSuccess = true };
			});

		await job.StartAsync(CancellationToken.None);
		await synced.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var sw = Stopwatch.StartNew();
		await job.StopAsync(CancellationToken.None);

		sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
		job.ExecuteTask!.IsCompleted.Should().BeTrue();
	}

	[TestCase(3600, true, TestName = "When_SyncThrows_Should_StillWaitForThePollingInterval")]
	[TestCase(0, false, TestName = "When_PollingIntervalIsZero_Should_NotSyncBackToBack")]
	public async Task Should_NotSyncInATightLoop(int pollingIntervalSeconds, bool syncThrows)
	{
		var (mocker, job) = CreateJob();
		var settings = new Settings();
		settings.App.EnablePolling = true;
		settings.App.PollingIntervalSeconds = pollingIntervalSeconds;
		mocker.GetMock<ISettingsService>().Setup(x => x.GetSettingsAsync()).ReturnsAsync(settings);

		var syncCalls = 0;
		mocker.GetMock<ISyncService>()
			.Setup(x => x.SyncAsync(It.IsAny<int>(), It.IsAny<bool>()))
			.ReturnsAsync(() =>
			{
				Interlocked.Increment(ref syncCalls);
				if (syncThrows) throw new InvalidOperationException("sync blew up");
				return new SyncResult() { SyncSuccess = true };
			});

		await job.StartAsync(CancellationToken.None);
		await WaitUntilAsync(() => Volatile.Read(ref syncCalls) >= 1);
		await Task.Delay(200);
		await job.StopAsync(CancellationToken.None);

		Volatile.Read(ref syncCalls).Should().Be(1);
		job.ExecuteTask!.IsFaulted.Should().BeFalse();
	}
}
