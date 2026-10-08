using Common;
using Common.Database;
using Common.Dto;
using Common.Dto.Garmin;
using Common.Dto.Peloton;
using Common.Service;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UnitTests.Common.Service;

public class SettingServiceTests
{
	[Test]
	public async Task GetCustomDeviceInfoAsync_When_LegacyDeviceFile_Fails_And_NoNewSettings_FallsBackToDefaults()
	{
		// SETUP
		var mocker = new AutoMocker();
		var settingsService = mocker.CreateInstance<SettingsService>();

		var settings = new Settings();
		mocker.GetMock<ISettingsDb>().Setup(x => x.GetSettingsAsync(It.IsAny<int>())).ReturnsAsync(settings);

		// ACT
		var chosenDeviceInfo = await settingsService.GetCustomDeviceInfoAsync(null);

		// ASSERT
		chosenDeviceInfo.Name.Should().Be("Forerunner 945", because: "If all fails we should fall back to the default Settings.");
	}

	[Test]
	public async Task GetCustomDeviceInfoAsync_Choose_CorrectDevice_For_WorkoutType([Values] WorkoutType workoutType)
	{
		// SETUP
		var mocker = new AutoMocker();
		var settingsService = mocker.CreateInstance<SettingsService>();

		var deviceInfoSettings = new Dictionary<WorkoutType, GarminDeviceInfo>()
				{
					{ WorkoutType.None, new GarminDeviceInfo() { Name = "MyDefaultDevice" } },
					{ WorkoutType.Circuit, GarminDevices.Forerunner945 },
					{ WorkoutType.Cycling, GarminDevices.TACXDevice },
					{ WorkoutType.Meditation, GarminDevices.EpixDevice },
				};

		var settings = new Settings()
		{
			Format = new Format()
			{
				DeviceInfoSettings = deviceInfoSettings
			}
		};
		mocker.GetMock<ISettingsDb>().Setup(x => x.GetSettingsAsync(It.IsAny<int>())).ReturnsAsync(settings);

		GarminDeviceInfo userDeviceInfo = null;
		mocker.GetMock<IFileHandling>().Setup(x => x.TryDeserializeXml<GarminDeviceInfo>("./some/path/to.xml", out userDeviceInfo)).Returns(false);

		// ACT
		var workout = new Workout
		{
			Fitness_Discipline = workoutType.ToFitnessDiscipline().fitnessDiscipline,
			Is_Outdoor = workoutType.ToFitnessDiscipline().isOutdoor,
		};
		var chosenDeviceInfo = await settingsService.GetCustomDeviceInfoAsync(workout);

		// ASSERT
		if (deviceInfoSettings.TryGetValue(workoutType, out var expectedDeviceInfo))
		{
			chosenDeviceInfo.Should().Be(expectedDeviceInfo);
		}
		else
		{
			chosenDeviceInfo.Should().Be(deviceInfoSettings[WorkoutType.None]);
		}
	}
	private readonly List<string> _testEnvironmentVariables = new();

	[TearDown]
	public void ClearTestEnvironmentVariables()
	{
		foreach (var name in _testEnvironmentVariables)
			System.Environment.SetEnvironmentVariable(name, null);
		_testEnvironmentVariables.Clear();
	}

	/// <summary>
	/// Builds configuration the way the app does: environment variables under a prefix (unique per test, standing in
	/// for P2G_), with "Section:Key" written as SECTION__KEY.
	/// </summary>
	private IConfiguration BuildEnvironmentConfig(Dictionary<string, string> env, Dictionary<string, string> fileConfig = null)
	{
		var prefix = $"P2GTEST{System.Guid.NewGuid():N}_";
		foreach (var (key, value) in env)
		{
			var name = prefix + key.Replace(":", "__");
			_testEnvironmentVariables.Add(name);
			System.Environment.SetEnvironmentVariable(name, value);
		}
		return new ConfigurationBuilder()
			.AddInMemoryCollection(fileConfig ?? new Dictionary<string, string>())
			.AddEnvironmentVariables(prefix)
			.Build();
	}

	private SettingsService BuildWithConfig(Settings dbSettings, Dictionary<string, string> env, Dictionary<string, string> fileConfig = null)
	{
		var mocker = new AutoMocker();
		mocker.Use<IConfiguration>(BuildEnvironmentConfig(env, fileConfig));
		mocker.GetMock<ISettingsDb>().Setup(x => x.GetSettingsAsync(It.IsAny<int>())).ReturnsAsync(dbSettings);
		return mocker.CreateInstance<SettingsService>();
	}

	[Test]
	public async Task GetSettingsAsync_IgnoresNestedInvalidAndNonEnvironmentValues()
	{
		var dbSettings = new Settings();
		dbSettings.Format.Fit = true;
		dbSettings.Notifications.NotifyOnSuccess = false;
		var service = BuildWithConfig(dbSettings,
			env: new()
			{
				["Format:Cycling:PreferredLapType"] = "Distance",
				["Notifications:NotifyOnSuccess"] = "yes",
			},
			fileConfig: new() { ["Format:Fit"] = "false" });

		var settings = await service.GetSettingsAsync();

		settings.Format.Fit.Should().BeTrue(because: "configuration files are not environment overrides");
		settings.Notifications.NotifyOnSuccess.Should().BeFalse(because: "'yes' is not a valid bool");
		settings.Format.Cycling.PreferredLapType.Should().NotBe(PreferredLapType.Distance, because: "nested keys are not overridden");
		service.GetEnvironmentOverrides().Should().BeEmpty();
	}

	[Test]
	public async Task GetSettingsAsync_EnvFormatValue_OverridesSavedSetting_AndLeavesOthersAlone()
	{
		var dbSettings = new Settings();
		dbSettings.Format.IncludeTimeInPowerZones = false;
		dbSettings.Format.Fit = true;
		var service = BuildWithConfig(dbSettings, new() { ["Format:IncludeTimeInPowerZones"] = "true" });

		var settings = await service.GetSettingsAsync();

		settings.Format.IncludeTimeInPowerZones.Should().BeTrue();
		settings.Format.Fit.Should().BeTrue(because: "keys not set in env keep the saved value");
	}

	[Test]
	public async Task GetSettingsAsync_EnvNotificationValues_AreApplied()
	{
		var service = BuildWithConfig(new Settings(), new()
		{
			["Notifications:DiscordWebhookUrl"] = "https://discord.example/webhook",
			["Notifications:NotifyOnSuccess"] = "true",
		});

		var settings = await service.GetSettingsAsync();

		settings.Notifications.DiscordWebhookUrl.Should().Be("https://discord.example/webhook");
		settings.Notifications.NotifyOnSuccess.Should().BeTrue();
	}

	[Test]
	public async Task GetSettingsAsync_NoEnvValues_KeepsSavedSettings()
	{
		var dbSettings = new Settings();
		dbSettings.Format.IncludeTimeInPowerZones = true;
		var service = BuildWithConfig(dbSettings, new());

		var settings = await service.GetSettingsAsync();

		settings.Format.IncludeTimeInPowerZones.Should().BeTrue();
		settings.Notifications.DiscordWebhookUrl.Should().BeNull();
	}

	[Test]
	public void GetEnvironmentOverrides_ListsOnlyKeysSetByEnvironment()
	{
		var service = BuildWithConfig(new Settings(), new()
		{
			["Format:INCLUDETIMEINPOWERZONES"] = "true",
			["Notifications:DiscordWebhookUrl"] = "https://discord.com/api/webhooks/1/abc",
			["App:EnablePolling"] = "true",
		});

		service.GetEnvironmentOverrides().Should().BeEquivalentTo("Format.IncludeTimeInPowerZones", "Notifications.DiscordWebhookUrl");
	}

	[Test]
	public async Task UpdateSettingsAsync_KeepsSavedValues_ForKeysSetByEnvironment()
	{
		// Like the real DB, every read returns a fresh copy of the saved settings.
		static Settings SavedSettings()
		{
			var s = new Settings();
			s.Format.IncludeTimeInPowerZones = false;
			s.Notifications.DiscordWebhookUrl = "https://discord.com/api/webhooks/saved";
			return s;
		}
		var mocker = new AutoMocker();
		mocker.Use<IConfiguration>(BuildEnvironmentConfig(new()
		{
			["Format:IncludeTimeInPowerZones"] = "true",
			["Notifications:DiscordWebhookUrl"] = "https://discord.com/api/webhooks/env",
		}));
		var db = mocker.GetMock<ISettingsDb>();
		db.Setup(x => x.GetSettingsAsync(It.IsAny<int>())).ReturnsAsync(SavedSettings);
		Settings saved = null;
		db.Setup(x => x.UpsertSettingsAsync(It.IsAny<int>(), It.IsAny<Settings>())).Callback<int, Settings>((_, s) => saved = s).ReturnsAsync(true);
		var service = mocker.CreateInstance<SettingsService>();

		var settings = await service.GetSettingsAsync();
		settings.Format.Fit = true;
		settings.Notifications.NotifyOnSuccess = true;
		await service.UpdateSettingsAsync(settings);

		saved.Format.IncludeTimeInPowerZones.Should().BeFalse(because: "the environment's value is not the user's choice");
		saved.Notifications.DiscordWebhookUrl.Should().Be("https://discord.com/api/webhooks/saved");
		saved.Format.Fit.Should().BeTrue();
		saved.Notifications.NotifyOnSuccess.Should().BeTrue();
	}
}
