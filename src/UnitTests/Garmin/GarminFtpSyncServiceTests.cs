using Common.Dto;
using Common.Dto.Peloton;
using Common.Service;
using FluentAssertions;
using Garmin;
using Garmin.Auth;
using Garmin.Dto;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace UnitTests.Garmin
{
	public class GarminFtpSyncServiceTests
	{
		private const string Cycling = GarminPowerZones.CyclingSport;
		private static readonly UserData TestedFtp149 = new() { Cycling_Ftp_Source = CyclingFtpSource.Ftp_Workout_Source, Cycling_Workout_Ftp = 149, Estimated_Cycling_Ftp = 143 };

		private static List<GarminPowerZones> GarminZones(double cyclingFtp = 200) => new()
		{
			new() { Sport = Cycling, FunctionalThresholdPower = cyclingFtp, Zone1Floor = 0, Zone2Floor = 110, Zone3Floor = 150, Zone4Floor = 180, Zone5Floor = 210, Zone6Floor = 240, Zone7Floor = 300 },
			new() { Sport = "ROWING", FunctionalThresholdPower = 160, Zone1Floor = 0, Zone2Floor = 88, Zone3Floor = 120, Zone4Floor = 144, Zone5Floor = 168, Zone6Floor = 192, Zone7Floor = 240 },
		};

		private class Fixture
		{
			public AutoMocker Mocker { get; } = new();
			public GarminFtpSyncService Service { get; }
			public List<GarminPowerZones> Sent { get; } = new();
			private Mock<IGarminApiClient> Api => Mocker.GetMock<IGarminApiClient>();

			public Fixture(List<GarminPowerZones> zones, bool syncFtp = true, bool upload = true, AuthStage auth = AuthStage.Completed)
			{
				Mocker.GetMock<ISettingsService>().Setup(s => s.GetSettingsAsync())
					.ReturnsAsync(new Settings { Garmin = { Upload = upload, SyncCyclingFtp = syncFtp } });
				Mocker.GetMock<IGarminAuthenticationService>().Setup(a => a.GetGarminAuthenticationAsync())
					.ReturnsAsync(new GarminApiAuthentication { AuthStage = auth });
				Api.Setup(c => c.GetPowerZonesAsync(It.IsAny<GarminApiAuthentication>())).ReturnsAsync(zones);
				Api.Setup(c => c.UpdatePowerZonesAsync(It.IsAny<GarminPowerZones>(), It.IsAny<GarminApiAuthentication>()))
					.Callback((GarminPowerZones z, GarminApiAuthentication _) => Sent.Add(z));
				Service = Mocker.CreateInstance<GarminFtpSyncService>();
			}

			public void VerifyFetched(Times times) => Api.Verify(c => c.GetPowerZonesAsync(It.IsAny<GarminApiAuthentication>()), times);
			public void VerifyNoUpdate() => Api.Verify(c => c.UpdatePowerZonesAsync(It.IsAny<GarminPowerZones>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
		}

		[Test]
		public async Task Updates_OnlyCycling_WithPelotonFtpAndZones()
		{
			var f = new Fixture(GarminZones());

			await f.Service.SyncCyclingFtpAsync(TestedFtp149);

			var cycling = f.Sent.Should().ContainSingle().Which;
			cycling.Sport.Should().Be(Cycling);
			cycling.FunctionalThresholdPower.Should().Be(149);
			new[] { cycling.Zone1Floor, cycling.Zone2Floor, cycling.Zone3Floor, cycling.Zone4Floor, cycling.Zone5Floor, cycling.Zone6Floor, cycling.Zone7Floor }
				.Should().Equal(0, 82, 112, 134, 156, 179, 224);
		}

		/// <summary>A save Garmin accepts but doesn't apply must not be reported or remembered as done.</summary>
		[Test]
		public async Task SaveThatGarminDoesNotApply_IsNotTrusted()
		{
			var f = new Fixture(GarminZones());
			// Every read returns a fresh copy still at 200, as if the save was ignored.
			f.Mocker.GetMock<IGarminApiClient>().Setup(c => c.GetPowerZonesAsync(It.IsAny<GarminApiAuthentication>())).ReturnsAsync(() => GarminZones());

			await f.Service.SyncCyclingFtpAsync(TestedFtp149);
			await f.Service.SyncCyclingFtpAsync(TestedFtp149);

			// Read, save, read back, on both runs: the unconfirmed FTP isn't cached.
			f.Sent.Should().HaveCount(2);
			f.VerifyFetched(Times.Exactly(4));
		}

		/// <summary>Settings saved before this fix hold the wrong save address under an old key; loading them must not use it.</summary>
		[Test]
		public async Task OldSavedSettings_LoadWithCurrentAddresses()
		{
			var dataDir = Path.Join(Path.GetTempPath(), $"p2g_settings_{System.Guid.NewGuid():N}");
			Directory.CreateDirectory(dataDir);
			File.WriteAllText(Path.Join(dataDir, "SettingsDb.json"),
				"{\"1\":{\"garmin\":{\"api\":{\"powerZonesUrl\":\"https://connectapi.garmin.com/biometric-service/powerZones/sports\"}}}}");
			var original = global::Common.Stateful.Statics.DefaultDataDirectory;
			try
			{
				global::Common.Stateful.Statics.DefaultDataDirectory = dataDir;
				var settings = await new global::Common.Database.SettingsDb(new global::Common.IOWrapper()).GetSettingsAsync(1);

				var defaults = new GarminApiSettings();
				settings.Garmin.Api.PowerZonesReadUrl.Should().Be(defaults.PowerZonesReadUrl);
				settings.Garmin.Api.PowerZonesSaveUrl.Should().Be(defaults.PowerZonesSaveUrl);
			}
			finally
			{
				global::Common.Stateful.Statics.DefaultDataDirectory = original;
				Directory.Delete(dataDir, recursive: true);
			}
		}

		[Test]
		public async Task ManualFtp_Uses95PercentOfEnteredBest()
		{
			var f = new Fixture(GarminZones());

			await f.Service.SyncCyclingFtpAsync(new UserData { Cycling_Ftp_Source = CyclingFtpSource.Ftp_Manual_Source, Cycling_Ftp = 200 });

			f.Sent.Should().ContainSingle().Which.FunctionalThresholdPower.Should().Be(190);
		}

		[TestCase(149)]
		[TestCase(149.0000001)]
		public async Task NoUpdate_WhenGarminAlreadyMatches(double garminFtp)
		{
			var f = new Fixture(GarminZones(garminFtp));

			await f.Service.SyncCyclingFtpAsync(TestedFtp149);

			f.VerifyNoUpdate();
		}

		[Test]
		public async Task ChecksGarminOnce_WhileFtpIsUnchanged()
		{
			var f = new Fixture(GarminZones());

			await f.Service.SyncCyclingFtpAsync(TestedFtp149);
			await f.Service.SyncCyclingFtpAsync(TestedFtp149);

			// The first run reads, saves and reads back; the second trusts the confirmed FTP.
			f.VerifyFetched(Times.Exactly(2));
		}

		[Test]
		public async Task NoUpdate_WhenPelotonOnlyHasAnEstimate()
		{
			var f = new Fixture(GarminZones());

			await f.Service.SyncCyclingFtpAsync(new UserData { Cycling_Ftp_Source = CyclingFtpSource.Ftp_Estimated_Source, Estimated_Cycling_Ftp = 143 });

			f.VerifyFetched(Times.Never());
			f.VerifyNoUpdate();
		}

		[Test]
		public async Task DoesNothing_WhenNoUserData()
		{
			var f = new Fixture(GarminZones());

			await f.Service.SyncCyclingFtpAsync(null);

			f.VerifyFetched(Times.Never());
		}

		[TestCase(false, true)]
		[TestCase(true, false)]
		public async Task DoesNothing_WhenSettingOrUploadIsOff(bool syncFtp, bool upload)
		{
			var f = new Fixture(GarminZones(), syncFtp, upload);

			await f.Service.SyncCyclingFtpAsync(TestedFtp149);

			f.VerifyFetched(Times.Never());
		}

		[TestCase(AuthStage.None)]
		[TestCase(AuthStage.NeedMfaToken)]
		public async Task DoesNothing_WhenNotSignedInToGarmin(AuthStage stage)
		{
			var f = new Fixture(GarminZones(), auth: stage);

			await f.Service.SyncCyclingFtpAsync(TestedFtp149);

			f.VerifyFetched(Times.Never());
		}

		[Test]
		public async Task NoUpdate_WhenGarminHasNoCyclingZones()
		{
			var f = new Fixture(GarminZones().Where(z => z.Sport != Cycling).ToList());

			await f.Service.SyncCyclingFtpAsync(TestedFtp149);

			f.VerifyNoUpdate();
		}

		[Test]
		public void PowerZones_KeepFieldsP2GDoesNotKnow()
		{
			const string json = "{\"sport\":\"ROWING\",\"functionalThresholdPower\":160.0,\"userLocalTime\":null,\"userProfilePk\":42}";

			var roundTrip = JsonSerializer.Serialize(JsonSerializer.Deserialize<GarminPowerZones>(json));

			roundTrip.Should().Contain("\"userLocalTime\":null").And.Contain("\"userProfilePk\":42");
		}

		[TestCase(110, 1)]
		[TestCase(111, 2)]
		[TestCase(300, 6)]
		[TestCase(301, 7)]
		public void PowerZoneModel_PutsFloorValuesInTheZoneBelow(double watts, int zone)
		{
			PowerZoneModel.ZoneOf(watts, ftp: 200).Should().Be(zone);
		}
	}
}
