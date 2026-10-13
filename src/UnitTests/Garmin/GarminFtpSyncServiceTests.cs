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
using System.Linq;
using System.Threading.Tasks;

namespace UnitTests.Garmin
{
	public class GarminFtpSyncServiceTests
	{
		private static readonly UserData TestedFtp149 = new() { Cycling_Ftp_Source = CyclingFtpSource.Ftp_Workout_Source, Cycling_Workout_Ftp = 149, Estimated_Cycling_Ftp = 143 };

		private static List<GarminPowerZones> GarminZones(double cyclingFtp = 200) => new()
		{
			new() { Sport = "CYCLING", FunctionalThresholdPower = cyclingFtp, Zone1Floor = 0, Zone2Floor = 110, Zone3Floor = 150, Zone4Floor = 180, Zone5Floor = 210, Zone6Floor = 240, Zone7Floor = 300 },
			new() { Sport = "ROWING", FunctionalThresholdPower = 160, Zone1Floor = 0, Zone2Floor = 88, Zone3Floor = 120, Zone4Floor = 144, Zone5Floor = 168, Zone6Floor = 192, Zone7Floor = 240 },
		};

		private static AutoMocker Setup(List<GarminPowerZones> zones, bool syncFtp = true, bool upload = true)
		{
			var mocker = new AutoMocker();
			mocker.GetMock<ISettingsService>().Setup(s => s.GetSettingsAsync())
				.ReturnsAsync(new Settings { Garmin = { Upload = upload, SyncCyclingFtp = syncFtp } });
			mocker.GetMock<IGarminAuthenticationService>().Setup(a => a.GetGarminAuthenticationAsync())
				.ReturnsAsync(new GarminApiAuthentication { AuthStage = AuthStage.Completed });
			mocker.GetMock<IGarminApiClient>().Setup(c => c.GetPowerZonesAsync(It.IsAny<GarminApiAuthentication>())).ReturnsAsync(zones);
			return mocker;
		}

		[Test]
		public async Task Updates_CyclingFtpAndZones_AndKeepsOtherSports()
		{
			var zones = GarminZones();
			var mocker = Setup(zones);
			ICollection<GarminPowerZones> sent = null;
			mocker.GetMock<IGarminApiClient>().Setup(c => c.UpdatePowerZonesAsync(It.IsAny<ICollection<GarminPowerZones>>(), It.IsAny<GarminApiAuthentication>()))
				.Callback((ICollection<GarminPowerZones> z, GarminApiAuthentication _) => sent = z);

			await mocker.CreateInstance<GarminFtpSyncService>().SyncCyclingFtpAsync(TestedFtp149);

			sent.Should().HaveCount(2);
			var cycling = sent.Single(z => z.Sport == "CYCLING");
			cycling.FunctionalThresholdPower.Should().Be(149);
			new[] { cycling.Zone1Floor, cycling.Zone2Floor, cycling.Zone3Floor, cycling.Zone4Floor, cycling.Zone5Floor, cycling.Zone6Floor, cycling.Zone7Floor }
				.Should().Equal(0, 82, 112, 134, 156, 179, 224);
			var rowing = sent.Single(z => z.Sport == "ROWING");
			rowing.FunctionalThresholdPower.Should().Be(160);
			rowing.Zone2Floor.Should().Be(88);
		}

		[Test]
		public async Task ManualFtp_Uses95PercentOfEnteredBest()
		{
			var mocker = Setup(GarminZones());
			ICollection<GarminPowerZones> sent = null;
			mocker.GetMock<IGarminApiClient>().Setup(c => c.UpdatePowerZonesAsync(It.IsAny<ICollection<GarminPowerZones>>(), It.IsAny<GarminApiAuthentication>()))
				.Callback((ICollection<GarminPowerZones> z, GarminApiAuthentication _) => sent = z);

			await mocker.CreateInstance<GarminFtpSyncService>().SyncCyclingFtpAsync(new UserData { Cycling_Ftp_Source = CyclingFtpSource.Ftp_Manual_Source, Cycling_Ftp = 200 });

			sent.Single(z => z.Sport == "CYCLING").FunctionalThresholdPower.Should().Be(190);
		}

		[Test]
		public async Task NoUpdate_WhenGarminAlreadyMatches()
		{
			var mocker = Setup(GarminZones(cyclingFtp: 149));

			await mocker.CreateInstance<GarminFtpSyncService>().SyncCyclingFtpAsync(TestedFtp149);

			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdatePowerZonesAsync(It.IsAny<ICollection<GarminPowerZones>>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
		}

		[Test]
		public async Task NoUpdate_WhenPelotonOnlyHasAnEstimate()
		{
			var mocker = Setup(GarminZones());

			await mocker.CreateInstance<GarminFtpSyncService>().SyncCyclingFtpAsync(new UserData { Cycling_Ftp_Source = CyclingFtpSource.Ftp_Estimated_Source, Estimated_Cycling_Ftp = 143 });

			mocker.GetMock<IGarminApiClient>().Verify(c => c.GetPowerZonesAsync(It.IsAny<GarminApiAuthentication>()), Times.Never);
			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdatePowerZonesAsync(It.IsAny<ICollection<GarminPowerZones>>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
		}

		[TestCase(false, true)]
		[TestCase(true, false)]
		public async Task DoesNothing_WhenSettingOrUploadIsOff(bool syncFtp, bool upload)
		{
			var mocker = Setup(GarminZones(), syncFtp, upload);

			await mocker.CreateInstance<GarminFtpSyncService>().SyncCyclingFtpAsync(TestedFtp149);

			mocker.GetMock<IGarminApiClient>().Verify(c => c.GetPowerZonesAsync(It.IsAny<GarminApiAuthentication>()), Times.Never);
		}

		[Test]
		public async Task NoUpdate_WhenGarminHasNoCyclingZones()
		{
			var mocker = Setup(GarminZones().Where(z => z.Sport != "CYCLING").ToList());

			await mocker.CreateInstance<GarminFtpSyncService>().SyncCyclingFtpAsync(TestedFtp149);

			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdatePowerZonesAsync(It.IsAny<ICollection<GarminPowerZones>>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
		}
	}
}
