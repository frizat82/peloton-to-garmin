using Common.Dto;
using Common.Dto.Peloton;
using Common.Service;
using FluentAssertions;
using Garmin;
using Garmin.Auth;
using Garmin.Database;
using Garmin.Dto;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UnitTests.Garmin
{
	public class GarminActivityEnrichmentServiceTests
	{
		private static readonly long UnixNow = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
		private static readonly string GarminStartGmt = "2024-06-15 10:01:00";

		private static P2GWorkout BuildWorkout(string id = "workout1")
		{
			return new P2GWorkout
			{
				Workout = new Workout
				{
					Id = id,
					Start_Time = UnixNow,
					Fitness_Discipline = FitnessDiscipline.Cycling,
				},
				WorkoutSamples = new WorkoutSamples(),
			};
		}

		private static GarminActivitySummary BuildActivity(long activityId = 12345, string startGmt = null)
		{
			return new GarminActivitySummary
			{
				ActivityId = activityId,
				StartTimeGMT = startGmt ?? GarminStartGmt,
				ActivityType = new GarminActivityType { TypeKey = "cycling" },
			};
		}

		private static Settings BuildSettings(bool enrich = true, bool upload = true)
		{
			return new Settings
			{
				Garmin =
				{
					EnrichGarminActivities = enrich,
					Upload = upload,
					ActivityMatchWindowSeconds = 900,
				}
			};
		}

		[Test]
		public async Task EnrichAsync_When_EnrichGarminActivities_Disabled_Returns_EmptyList()
		{
			var mocker = new AutoMocker();
			var service = mocker.CreateInstance<GarminActivityEnrichmentService>();
			var settingsService = mocker.GetMock<ISettingsService>();
			settingsService.Setup(s => s.GetSettingsAsync()).ReturnsAsync(BuildSettings(enrich: false));

			var result = await service.EnrichAsync(new[] { BuildWorkout() });

			result.Should().BeEmpty();
			mocker.GetMock<IGarminApiClient>().Verify(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
		}

		[Test]
		public async Task EnrichAsync_When_GarminUpload_Disabled_Returns_EmptyList()
		{
			var mocker = new AutoMocker();
			var service = mocker.CreateInstance<GarminActivityEnrichmentService>();
			var settingsService = mocker.GetMock<ISettingsService>();
			settingsService.Setup(s => s.GetSettingsAsync()).ReturnsAsync(BuildSettings(enrich: true, upload: false));

			var result = await service.EnrichAsync(new[] { BuildWorkout() });

			result.Should().BeEmpty();
			mocker.GetMock<IGarminApiClient>().Verify(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
		}

		[Test]
		public async Task EnrichAsync_When_AuthNotCompleted_Returns_EmptyList()
		{
			var mocker = new AutoMocker();
			var service = mocker.CreateInstance<GarminActivityEnrichmentService>();
			mocker.GetMock<ISettingsService>().Setup(s => s.GetSettingsAsync()).ReturnsAsync(BuildSettings());
			mocker.GetMock<IGarminAuthenticationService>()
				.Setup(a => a.GetGarminAuthenticationAsync())
				.ReturnsAsync(new GarminApiAuthentication { AuthStage = AuthStage.NeedMfaToken });

			var result = await service.EnrichAsync(new[] { BuildWorkout() });

			result.Should().BeEmpty();
			mocker.GetMock<IGarminApiClient>().Verify(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
		}

		[Test]
		public async Task EnrichAsync_When_NoGarminActivitiesFound_Returns_EmptyList_And_DoesNotSave()
		{
			var mocker = new AutoMocker();
			var service = mocker.CreateInstance<GarminActivityEnrichmentService>();
			mocker.GetMock<ISettingsService>().Setup(s => s.GetSettingsAsync()).ReturnsAsync(BuildSettings());
			mocker.GetMock<IGarminAuthenticationService>()
				.Setup(a => a.GetGarminAuthenticationAsync())
				.ReturnsAsync(new GarminApiAuthentication { AuthStage = AuthStage.Completed });
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary>());

			var result = await service.EnrichAsync(new[] { BuildWorkout() });

			result.Should().BeEmpty();
			mocker.GetMock<IGarminMergeDb>().Verify(db => db.SaveAsync(It.IsAny<GarminMergeRecord>()), Times.Never);
		}

		[Test]
		public async Task EnrichAsync_When_NoTimeMatch_Returns_EmptyList_And_DoesNotSave()
		{
			var mocker = new AutoMocker();
			var service = mocker.CreateInstance<GarminActivityEnrichmentService>();
			mocker.GetMock<ISettingsService>().Setup(s => s.GetSettingsAsync()).ReturnsAsync(BuildSettings());
			mocker.GetMock<IGarminAuthenticationService>()
				.Setup(a => a.GetGarminAuthenticationAsync())
				.ReturnsAsync(new GarminApiAuthentication { AuthStage = AuthStage.Completed });

			// Activity starts 2 hours after Peloton workout — outside the 900s window
			var farActivity = BuildActivity(startGmt: "2024-06-15 12:00:00");
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary> { farActivity });

			var result = await service.EnrichAsync(new[] { BuildWorkout() });

			result.Should().BeEmpty();
			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(It.IsAny<long>(), It.IsAny<GarminActivityUpdateRequest>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
			mocker.GetMock<IGarminMergeDb>().Verify(db => db.SaveAsync(It.IsAny<GarminMergeRecord>()), Times.Never);
		}

		[Test]
		public async Task EnrichAsync_When_MatchFound_Returns_WorkoutId_And_Updates_And_Saves()
		{
			var mocker = new AutoMocker();
			var service = mocker.CreateInstance<GarminActivityEnrichmentService>();

			var settings = new Settings();
			settings.Garmin.EnrichGarminActivities = true;
			settings.Garmin.Upload = true;
			settings.Garmin.ActivityMatchWindowSeconds = 900;
			mocker.GetMock<ISettingsService>().Setup(s => s.GetSettingsAsync()).ReturnsAsync(settings);

			mocker.GetMock<IGarminAuthenticationService>()
				.Setup(a => a.GetGarminAuthenticationAsync())
				.ReturnsAsync(new GarminApiAuthentication { AuthStage = AuthStage.Completed });
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary> { BuildActivity(activityId: 99) });
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.UpdateActivityAsync(It.IsAny<long>(), It.IsAny<GarminActivityUpdateRequest>(), It.IsAny<GarminApiAuthentication>()))
				.Returns(Task.CompletedTask);
			mocker.GetMock<IGarminMergeDb>()
				.Setup(db => db.SaveAsync(It.IsAny<GarminMergeRecord>()))
				.Returns(Task.CompletedTask);

			var result = await service.EnrichAsync(new[] { BuildWorkout("workout1") });

			// Verify search was called (confirms early-exit guards were passed)
			mocker.GetMock<IGarminApiClient>().Verify(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()), Times.Once);
			result.Should().ContainSingle().Which.PelotonWorkoutId.Should().Be("workout1");
			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(99, It.IsAny<GarminActivityUpdateRequest>(), It.IsAny<GarminApiAuthentication>()), Times.Once);
			mocker.GetMock<IGarminMergeDb>().Verify(db => db.SaveAsync(It.Is<GarminMergeRecord>(r =>
				r.PelotonWorkoutId == "workout1" &&
				r.GarminActivityId == 99 &&
				r.Source == MergeSource.Auto)), Times.Once);
		}

		[Test]
		public async Task EnrichAsync_When_SearchThrows_Continues_And_Returns_EmptyList()
		{
			var mocker = new AutoMocker();
			var service = mocker.CreateInstance<GarminActivityEnrichmentService>();
			mocker.GetMock<ISettingsService>().Setup(s => s.GetSettingsAsync()).ReturnsAsync(BuildSettings());
			mocker.GetMock<IGarminAuthenticationService>()
				.Setup(a => a.GetGarminAuthenticationAsync())
				.ReturnsAsync(new GarminApiAuthentication { AuthStage = AuthStage.Completed });
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ThrowsAsync(new Exception("API failure"));

			var result = await service.EnrichAsync(new[] { BuildWorkout() });

			result.Should().BeEmpty();
			mocker.GetMock<IGarminMergeDb>().Verify(db => db.SaveAsync(It.IsAny<GarminMergeRecord>()), Times.Never);
		}

		[Test]
		public async Task EnrichAsync_With_MultipleWorkouts_SameDayOnlyCallsSearchOnce()
		{
			var mocker = new AutoMocker();
			var service = mocker.CreateInstance<GarminActivityEnrichmentService>();
			mocker.GetMock<ISettingsService>().Setup(s => s.GetSettingsAsync()).ReturnsAsync(BuildSettings());
			mocker.GetMock<IGarminAuthenticationService>()
				.Setup(a => a.GetGarminAuthenticationAsync())
				.ReturnsAsync(new GarminApiAuthentication { AuthStage = AuthStage.Completed });
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary>());

			var workout1 = BuildWorkout("w1");
			var workout2 = BuildWorkout("w2");

			await service.EnrichAsync(new[] { workout1, workout2 });

			mocker.GetMock<IGarminApiClient>().Verify(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()), Times.Once);
		}
	}
}
