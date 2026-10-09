using Api.Contract;
using Api.Service;
using Api.Service.Helpers;
using Common;
using Common.Dto;
using Common.Service;
using FluentAssertions;
using Garmin.Auth;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using System.Threading.Tasks;

namespace UnitTests.Api.Service;

public class SettingsUpdaterServiceTests
{
	[Test]
	public async Task UpdateAppSettingsAsync_With_NullRequest_Returns400()
	{
		var autoMocker = new AutoMocker();
		var service = autoMocker.CreateInstance<SettingsUpdaterService>();

		var response = await service.UpdateAppSettingsAsync(null);

		response.IsErrored().Should().BeTrue();
		response.Error.Should().NotBeNull();
		response.Error.Message.Should().Be("Updated AppSettings must not be null or empty.");
	}

	[Test]
	public async Task UpdatePelotonSettingsAsync_With_NullRequest_ReturnsError()
	{
		var autoMocker = new AutoMocker();
		var service = autoMocker.CreateInstance<SettingsUpdaterService>();

		var response = await service.UpdatePelotonSettingsAsync(null);

		response.IsErrored().Should().BeTrue();
		response.Error.Should().NotBeNull();
		response.Error.Message.Should().Be("Updated PelotonSettings must not be null or empty.");
	}

	[Test]
	public async Task UpdatePelotonSettingsAsync_With_Invalid_NumWorkoutsToDownload_And_PollingEnabled_ReturnsError()
	{
		var autoMocker = new AutoMocker();
		var service = autoMocker.CreateInstance<SettingsUpdaterService>();
		var settingService = autoMocker.GetMock<ISettingsService>();

		settingService.SetupWithAny<ISettingsService, Task<Settings>>(nameof(settingService.Object.GetSettingsAsync))
			.ReturnsAsync(new Settings() { App = new() { EnablePolling = true } });

		var request = new SettingsPelotonPostRequest()
		{
			NumWorkoutsToDownload = -1
		};

		var response = await service.UpdatePelotonSettingsAsync(request);

		response.IsErrored().Should().BeTrue();
		response.Error.Should().NotBeNull();
		response.Error.Message.Should().Be("Number of workouts to download must be greater than 0 when Automatic Polling is enabled.");
	}

	[Test]
	public async Task FormatPost_With_NullRequest_Returns400()
	{
		var autoMocker = new AutoMocker();
		var service = autoMocker.CreateInstance<SettingsUpdaterService>();

		var response = await service.UpdateFormatSettingsAsync(null);

		response.IsErrored().Should().BeTrue();
		response.Error.Should().NotBeNull();
		response.Error.Message.Should().Be("Updated Format Settings must not be null or empty.");
	}

	[Test]
	public async Task GarminPost_With_NullRequest_Returns400()
	{
		var autoMocker = new AutoMocker();
		var service = autoMocker.CreateInstance<SettingsUpdaterService>();

		var response = await service.UpdateGarminSettingsAsync(null);

		response.IsErrored().Should().BeTrue();
		response.Error.Should().NotBeNull();
		response.Error.Message.Should().Be("Updated Garmin Settings must not be null or empty.");
	}

	[Test]
	public async Task GarminPost_With_EmailChange_Should_SignOut_of_Garmin()
	{
		var autoMocker = new AutoMocker();
		var service = autoMocker.CreateInstance<SettingsUpdaterService>();
		var settingService = autoMocker.GetMock<ISettingsService>();

		settingService
			.SetupWithAny<ISettingsService, Task<Settings>>(nameof(settingService.Object.GetSettingsAsync))
			.ReturnsAsync(new Settings()
			{
				App = new() { EnablePolling = true },
				Garmin = new() { Email = "ogEmail", Password = "ogPassword" }
			});

		SettingsGarminPostRequest request = new()
		{
			Email = "newEmail",
		};

		var response = await service.UpdateGarminSettingsAsync(request);

		autoMocker
			.GetMock<IGarminAuthenticationService>()
			.Verify(x => x.SignOutAsync(), Times.Once);

		response.IsErrored().Should().BeFalse();
		response.Error.Should().BeNull();
		response.Successful.Should().BeTrue();
	}

	[Test]
	public async Task GarminPost_With_PasswordChange_Should_SignOut_of_Garmin()
	{
		var autoMocker = new AutoMocker();
		var service = autoMocker.CreateInstance<SettingsUpdaterService>();
		var settingService = autoMocker.GetMock<ISettingsService>();

		settingService
			.SetupWithAny<ISettingsService, Task<Settings>>(nameof(settingService.Object.GetSettingsAsync))
			.ReturnsAsync(new Settings()
			{
				App = new() { EnablePolling = true },
				Garmin = new() { Email = "ogEmail", Password = "ogPassword" }
			});

		SettingsGarminPostRequest request = new()
		{
			Password = "newPassword",
		};

		var response = await service.UpdateGarminSettingsAsync(request);

		autoMocker
			.GetMock<IGarminAuthenticationService>()
			.Verify(x => x.SignOutAsync(), Times.Once);

		response.IsErrored().Should().BeFalse();
		response.Error.Should().BeNull();
		response.Successful.Should().BeTrue();
	}

	[Test]
	public async Task GarminPost_With_UnchangedCredentials_Should_Not_SignOut_of_Garmin()
	{
		var autoMocker = new AutoMocker();
		var service = autoMocker.CreateInstance<SettingsUpdaterService>();
		var settingService = autoMocker.GetMock<ISettingsService>();

		settingService
			.SetupWithAny<ISettingsService, Task<Settings>>(nameof(settingService.Object.GetSettingsAsync))
			.ReturnsAsync(new Settings()
			{
				Garmin = new() { Email = "ogEmail", Password = "ogPassword" }
			});

		// The UI never receives the saved password, so it posts null when the password is unchanged.
		SettingsGarminPostRequest request = new()
		{
			Email = "ogEmail",
			Password = null,
			Upload = true,
		};

		var response = await service.UpdateGarminSettingsAsync(request);

		autoMocker
			.GetMock<IGarminAuthenticationService>()
			.Verify(x => x.SignOutAsync(), Times.Never);
		response.Successful.Should().BeTrue();
	}

	[Test]
	public async Task UpdateAppSettingsAsync_EnablingPolling_With_Invalid_NumWorkoutsToDownload_ReturnsError()
	{
		var autoMocker = new AutoMocker();
		var service = autoMocker.CreateInstance<SettingsUpdaterService>();
		var settingService = autoMocker.GetMock<ISettingsService>();
		settingService
			.SetupWithAny<ISettingsService, Task<Settings>>(nameof(settingService.Object.GetSettingsAsync))
			.ReturnsAsync(new Settings() { Peloton = new() { NumWorkoutsToDownload = 0 } });

		var response = await service.UpdateAppSettingsAsync(new App() { EnablePolling = true, PollingIntervalSeconds = 3600 });

		response.IsErrored().Should().BeTrue();
		response.Error.Message.Should().Be("Number of workouts to download must be greater than 0 when Automatic Polling is enabled.");
		settingService.Verify(x => x.UpdateSettingsAsync(It.IsAny<Settings>()), Times.Never);
	}

	[TestCase(0)]
	[TestCase(-5)]
	public async Task UpdateAppSettingsAsync_With_NonPositivePollingInterval_And_PollingEnabled_ReturnsError(int interval)
	{
		var autoMocker = new AutoMocker();
		var service = autoMocker.CreateInstance<SettingsUpdaterService>();

		var response = await service.UpdateAppSettingsAsync(new App() { EnablePolling = true, PollingIntervalSeconds = interval });

		response.IsErrored().Should().BeTrue();
		response.Error.Message.Should().Be("Polling interval must be greater than 0 seconds when Automatic Syncing is enabled.");
		autoMocker.GetMock<ISettingsService>().Verify(x => x.UpdateSettingsAsync(It.IsAny<Settings>()), Times.Never);
	}

	[TestCase("valid", ExpectedResult = false)]
	[TestCase("\\", ExpectedResult = true)]
	[TestCase("a\\a", ExpectedResult = true)]
	[TestCase("\\a", ExpectedResult = true)]
	[TestCase("a\\", ExpectedResult = true)]
	[TestCase("a\\ads\\adf", ExpectedResult = true)]
	public async Task<bool> GarminPost_With_PasswordChange_Should_Validate_Characters(string password)
	{
		var autoMocker = new AutoMocker();
		var service = autoMocker.CreateInstance<SettingsUpdaterService>();
		var settingService = autoMocker.GetMock<ISettingsService>();

		settingService
			.SetupWithAny<ISettingsService, Task<Settings>>(nameof(settingService.Object.GetSettingsAsync))
			.ReturnsAsync(new Settings()
			{
				App = new() { EnablePolling = true },
				Garmin = new() { Email = "ogEmail", Password = "ogPassword" }
			});

		SettingsGarminPostRequest request = new()
		{
			Password = password,
		};

		var response = await service.UpdateGarminSettingsAsync(request);

		return response.IsErrored();
	}

	[TestCase("valid", ExpectedResult = false)]
	[TestCase("\\", ExpectedResult = true)]
	[TestCase("a\\a", ExpectedResult = true)]
	[TestCase("\\a", ExpectedResult = true)]
	[TestCase("a\\", ExpectedResult = true)]
	[TestCase("a\\ads\\adf", ExpectedResult = true)]
	public async Task<bool> PelotonPost_With_PasswordChange_Should_Validate_Characters(string password)
	{
		var autoMocker = new AutoMocker();
		var service = autoMocker.CreateInstance<SettingsUpdaterService>();
		var settingService = autoMocker.GetMock<ISettingsService>();

		settingService
			.SetupWithAny<ISettingsService, Task<Settings>>(nameof(settingService.Object.GetSettingsAsync))
			.ReturnsAsync(new Settings()
			{
				App = new() { EnablePolling = false },
				Peloton = new() { Email = "ogEmail", Password = "ogPassword" }
			});

		SettingsPelotonPostRequest request = new()
		{
			Password = password,
		};

		var response = await service.UpdatePelotonSettingsAsync(request);

		return response.IsErrored();
	}

	private static (SettingsUpdaterService Service, Settings Settings) BuildForNotifications(string savedWebhook)
	{
		var autoMocker = new AutoMocker();
		var settings = new Settings();
		settings.Notifications.DiscordWebhookUrl = savedWebhook;
		autoMocker.GetMock<ISettingsService>().Setup(s => s.GetSettingsAsync()).ReturnsAsync(settings);
		return (autoMocker.CreateInstance<SettingsUpdaterService>(), settings);
	}

	[Test]
	public async Task UpdateNotificationSettingsAsync_With_NullRequest_ReturnsError()
	{
		var (service, _) = BuildForNotifications(null);

		var response = await service.UpdateNotificationSettingsAsync(null);

		response.IsErrored().Should().BeTrue();
	}

	[TestCase("http://discord.com/api/webhooks/1/abc")]
	[TestCase("https://evildiscord.com/api/webhooks/1/abc")]
	[TestCase("https://example.com/api/webhooks/1/abc")]
	[TestCase("https://discord.com/channels/1")]
	[TestCase("not a url")]
	public async Task UpdateNotificationSettingsAsync_With_NonDiscordWebhook_ReturnsError(string url)
	{
		var (service, settings) = BuildForNotifications("https://discord.com/api/webhooks/saved");

		var response = await service.UpdateNotificationSettingsAsync(new SettingsNotificationsPostRequest { DiscordWebhookUrl = url });

		response.IsErrored().Should().BeTrue();
		settings.Notifications.DiscordWebhookUrl.Should().Be("https://discord.com/api/webhooks/saved");
	}

	[TestCase("https://canary.discordapp.com/api/webhooks/1/abc")]
	[TestCase("https://ptb.discord.com/api/webhooks/1/abc")]
	public async Task UpdateNotificationSettingsAsync_With_DiscordSubdomainWebhook_SavesIt(string url)
	{
		var (service, settings) = BuildForNotifications(null);

		var response = await service.UpdateNotificationSettingsAsync(new SettingsNotificationsPostRequest { DiscordWebhookUrl = url });

		response.IsErrored().Should().BeFalse();
		settings.Notifications.DiscordWebhookUrl.Should().Be(url);
	}

	[Test]
	public async Task UpdateNotificationSettingsAsync_With_NewWebhook_SavesIt()
	{
		var (service, settings) = BuildForNotifications(null);

		var response = await service.UpdateNotificationSettingsAsync(new SettingsNotificationsPostRequest { DiscordWebhookUrl = " https://discord.com/api/webhooks/1/abc ", NotifyOnSuccess = true });

		response.IsErrored().Should().BeFalse();
		settings.Notifications.DiscordWebhookUrl.Should().Be("https://discord.com/api/webhooks/1/abc");
		settings.Notifications.NotifyOnSuccess.Should().BeTrue();
		response.Result.IsDiscordWebhookUrlSet.Should().BeTrue();
	}

	[Test]
	public async Task UpdateNotificationSettingsAsync_With_NullWebhook_KeepsSavedOne()
	{
		var (service, settings) = BuildForNotifications("https://discord.com/api/webhooks/saved");

		await service.UpdateNotificationSettingsAsync(new SettingsNotificationsPostRequest { DiscordWebhookUrl = null, NotifyOnSuccess = true });

		settings.Notifications.DiscordWebhookUrl.Should().Be("https://discord.com/api/webhooks/saved");
		settings.Notifications.NotifyOnSuccess.Should().BeTrue();
	}

	[Test]
	public async Task UpdateNotificationSettingsAsync_With_EmptyWebhook_KeepsSavedOne()
	{
		var (service, settings) = BuildForNotifications("https://discord.com/api/webhooks/saved");

		await service.UpdateNotificationSettingsAsync(new SettingsNotificationsPostRequest { DiscordWebhookUrl = string.Empty });

		settings.Notifications.DiscordWebhookUrl.Should().Be("https://discord.com/api/webhooks/saved");
	}

	[Test]
	public async Task UpdateNotificationSettingsAsync_With_Remove_RemovesWebhook()
	{
		var (service, settings) = BuildForNotifications("https://discord.com/api/webhooks/saved");

		var response = await service.UpdateNotificationSettingsAsync(new SettingsNotificationsPostRequest { RemoveDiscordWebhookUrl = true });

		settings.Notifications.DiscordWebhookUrl.Should().BeNull();
		response.Result.IsDiscordWebhookUrlSet.Should().BeFalse();
	}
}
