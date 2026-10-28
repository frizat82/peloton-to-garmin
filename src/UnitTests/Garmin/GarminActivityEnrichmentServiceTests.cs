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
using System.IO;
using System.Linq;
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

		private static GarminActivitySummary BuildActivity(long activityId = 12345, string startGmt = null, string typeKey = "cycling")
		{
			return new GarminActivitySummary
			{
				ActivityId = activityId,
				StartTimeGMT = startGmt ?? GarminStartGmt,
				ActivityType = new GarminActivityType { TypeKey = typeKey },
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

		private static P2GWorkout BuildClass(string title)
		{
			var workout = BuildWorkout();
			workout.Workout = workout.Workout with { Fitness_Discipline = FitnessDiscipline.Strength, Ride = new Ride { Title = title } };
			return workout;
		}

		/// <summary>The settings, sign-in and search for enriching one watch activity (id 99) of the given Garmin type.</summary>
		private static (AutoMocker Mocker, GarminActivityEnrichmentService Service) BuildEnrich(string watchTypeKey)
		{
			var mocker = new AutoMocker();
			var service = mocker.CreateInstance<GarminActivityEnrichmentService>();
			mocker.GetMock<ISettingsService>().Setup(s => s.GetSettingsAsync()).ReturnsAsync(BuildSettings());
			mocker.GetMock<IGarminAuthenticationService>()
				.Setup(a => a.GetGarminAuthenticationAsync())
				.ReturnsAsync(new GarminApiAuthentication { AuthStage = AuthStage.Completed });
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary> { BuildActivity(activityId: 99, typeKey: watchTypeKey) });
			return (mocker, service);
		}

		[TestCase("15 min Pilates: Express", "yoga", "pilates")]
		[TestCase("20 min Yoga Flow", "yoga", null)]
		[TestCase("15 min Pilates", "strength_training", null)]
		public async Task EnrichAsync_PilatesClassRecordedAsYoga_IsChangedToPilates(string classTitle, string watchTypeKey, string expectedTypeKey)
		{
			var (mocker, service) = BuildEnrich(watchTypeKey);

			await service.EnrichAsync(new[] { BuildClass(classTitle) });

			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(99, It.Is<GarminActivityUpdateRequest>(r => (r.ActivityType == null ? null : r.ActivityType.TypeKey) == expectedTypeKey), It.IsAny<GarminApiAuthentication>()), Times.Once);
		}

		[Test]
		public async Task EnrichAsync_YogaClassStackedWithPilates_StaysYoga()
		{
			var (mocker, service) = BuildEnrich("yoga");
			var pilates = BuildClass("15 min Pilates");
			var yoga = BuildClass("10 min Yoga Flow");
			yoga.Workout = yoga.Workout with { Id = "workout2" };

			await service.EnrichAsync(new[] { pilates, yoga });

			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(99, It.Is<GarminActivityUpdateRequest>(r => r.ActivityType == null), It.IsAny<GarminApiAuthentication>()), Times.Once);
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

		private const long WatchActivityId = 42;
		private const long NewActivityId = 99;

		/// <summary>
		/// A merge set up against a watch activity: Garmin "contains" the new activity once
		/// <paramref name="visibleAfterUploads"/> uploads have been made (never when null).
		/// </summary>
		private static readonly byte[] WatchFit = File.ReadAllBytes(Path.Join(Directory.GetCurrentDirectory(), "..", "..", "..", "Data", "Fenix_Incline.fit"));

		private static (AutoMocker Mocker, GarminActivityEnrichmentService Service, List<byte[]> Uploads) BuildMerge(
			int? visibleAfterUploads, Func<int, UploadResponse> respond = null, string watchTypeKey = "cycling")
		{
			var mocker = new AutoMocker();
			var service = mocker.CreateInstance<GarminActivityEnrichmentService>();
			service.DelayAsync = _ => Task.CompletedTask;

			var settings = BuildSettings();
			settings.Garmin.MergeFitWithWatch = true;
			mocker.GetMock<ISettingsService>().Setup(s => s.GetSettingsAsync()).ReturnsAsync(settings);
			mocker.GetMock<IGarminAuthenticationService>()
				.Setup(a => a.GetGarminAuthenticationAsync())
				.ReturnsAsync(new GarminApiAuthentication { AuthStage = AuthStage.Completed });

			var uploads = new List<byte[]>();
			var api = mocker.GetMock<IGarminApiClient>();
			api.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(() => visibleAfterUploads is not null && uploads.Count >= visibleAfterUploads
					? new List<GarminActivitySummary> { BuildActivity(WatchActivityId, typeKey: watchTypeKey), BuildActivity(NewActivityId, typeKey: watchTypeKey) }
					: new List<GarminActivitySummary> { BuildActivity(WatchActivityId, typeKey: watchTypeKey) });

			// Use a real FIT file so MergeWatchFitWithPeloton doesn't throw before reaching delete
			api.Setup(c => c.DownloadActivityFitAsync(WatchActivityId, It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(WatchFit);
			api.Setup(c => c.UploadActivity(It.IsAny<string>(), ".fit", It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync((string path, string _, GarminApiAuthentication _) =>
				{
					uploads.Add(File.ReadAllBytes(path));
					return respond?.Invoke(uploads.Count) ?? new UploadResponse { DetailedImportResult = new DetailedImportResult { Successes = new List<Success>(), Failures = new List<Failure>() } };
				});

			return (mocker, service, uploads);
		}

		private static UploadResponse Duplicate() => new()
		{
			DetailedImportResult = new DetailedImportResult
			{
				Successes = new List<Success>(),
				Failures = new List<Failure> { new() { Messages = new List<Messages> { new() { Code = 202, Content = "Duplicate Activity." } } } },
			}
		};

		[Test]
		public async Task FitMerge_WhenMergedActivityAppears_RenamesIt_AndDoesNotRestore()
		{
			var (mocker, service, uploads) = BuildMerge(visibleAfterUploads: 1);

			var result = (await service.EnrichAsync(new[] { BuildWorkout("w1") })).Single();

			result.MergeStatus.Should().Be(MergeStatus.Success);
			uploads.Should().HaveCount(1);
			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(NewActivityId, It.IsAny<GarminActivityUpdateRequest>(), It.IsAny<GarminApiAuthentication>()), Times.Once);
		}

		[Test]
		public async Task FitMerge_PilatesClassRecordedAsYoga_ChangesTheMergedActivityToPilates()
		{
			var (mocker, service, _) = BuildMerge(visibleAfterUploads: 1, watchTypeKey: "yoga");

			await service.EnrichAsync(new[] { BuildClass("15 min Pilates: Express") });

			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(NewActivityId, It.Is<GarminActivityUpdateRequest>(r => r.ActivityType.TypeId == 160 && r.ActivityType.TypeKey == "pilates"), It.IsAny<GarminApiAuthentication>()), Times.Once);
		}

		/// <summary>
		/// Right after the delete, Garmin can refuse the merged FIT as a duplicate of the activity it is still
		/// deleting. That must not count as success: P2G waits and uploads again.
		/// </summary>
		[Test]
		public async Task FitMerge_WhenGarminRejectsAsDuplicate_UploadsAgain()
		{
			var (_, service, uploads) = BuildMerge(visibleAfterUploads: 2, respond: n => n == 1 ? Duplicate() : null);

			var result = (await service.EnrichAsync(new[] { BuildWorkout("w1") })).Single();

			result.MergeStatus.Should().Be(MergeStatus.Success);
			uploads.Should().HaveCount(2);
		}

		/// <summary>
		/// The merged FIT is accepted but never shows up in Garmin. With the original already deleted,
		/// P2G must put the watch recording back instead of reporting success.
		/// </summary>
		[Test]
		public async Task FitMerge_WhenMergedActivityNeverAppears_RestoresOriginal()
		{
			// Three merged uploads never appear; the fourth upload (the original) does.
			var (_, service, uploads) = BuildMerge(visibleAfterUploads: 4);

			var result = (await service.EnrichAsync(new[] { BuildWorkout("w1") })).Single();

			result.MergeStatus.Should().Be(MergeStatus.OriginalRestored);
			uploads.Should().HaveCount(4);
			uploads.Last().Should().Equal(WatchFit);
		}

		[Test]
		public async Task FitMerge_UsesActivityIdFromGarminsReply()
		{
			var (mocker, service, uploads) = BuildMerge(visibleAfterUploads: null,
				respond: _ => new UploadResponse { DetailedImportResult = new DetailedImportResult { Successes = new List<Success> { new() { InternalId = 77 } }, Failures = new List<Failure>() } });

			var result = (await service.EnrichAsync(new[] { BuildWorkout("w1") })).Single();

			result.MergeStatus.Should().Be(MergeStatus.Success);
			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(77, It.IsAny<GarminActivityUpdateRequest>(), It.IsAny<GarminApiAuthentication>()), Times.Once);
		}

		[Test]
		public async Task FitMerge_PollsUploadIdForTheNewActivity()
		{
			var (mocker, service, _) = BuildMerge(visibleAfterUploads: null,
				respond: _ => new UploadResponse { DetailedImportResult = new DetailedImportResult { UploadId = 5, Successes = new List<Success>(), Failures = new List<Failure>() } });
			mocker.GetMock<IGarminApiClient>().Setup(c => c.PollUploadActivityIdAsync(5, It.IsAny<GarminApiAuthentication>())).ReturnsAsync(88);

			var result = (await service.EnrichAsync(new[] { BuildWorkout("w1") })).Single();

			result.MergeStatus.Should().Be(MergeStatus.Success);
			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(88, It.IsAny<GarminActivityUpdateRequest>(), It.IsAny<GarminApiAuthentication>()), Times.Once);
		}

		/// <summary>An unrelated activity later the same day must not be taken for the merged upload.</summary>
		[Test]
		public async Task FitMerge_IgnoresNewActivitiesAtOtherTimesOfDay()
		{
			var (mocker, service, uploads) = BuildMerge(visibleAfterUploads: null);
			var sameDayLater = BuildActivity(NewActivityId, startGmt: "2024-06-15 18:00:00");
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(() => uploads.Count == 0
					? new List<GarminActivitySummary> { BuildActivity(WatchActivityId) }
					: new List<GarminActivitySummary> { BuildActivity(WatchActivityId), sameDayLater });

			var result = (await service.EnrichAsync(new[] { BuildWorkout("w1") })).Single();

			result.MergeStatus.Should().Be(MergeStatus.RestoreFailed);
			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(NewActivityId, It.IsAny<GarminActivityUpdateRequest>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
		}

		/// <summary>A failing search after the delete must still lead to a restore, not abort the merge.</summary>
		[Test]
		public async Task FitMerge_WhenSearchFailsAfterDelete_StillRestores()
		{
			var (mocker, service, uploads) = BuildMerge(visibleAfterUploads: null);
			var searches = 0;
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(() => ++searches == 1
					? new List<GarminActivitySummary> { BuildActivity(WatchActivityId) }
					: throw new Exception("Garmin search unavailable"));

			var result = (await service.EnrichAsync(new[] { BuildWorkout("w1") })).Single();

			result.MergeStatus.Should().Be(MergeStatus.RestoreFailed);
			uploads.Should().HaveCount(6);
		}

		/// <summary>
		/// When the merged FIT fails to upload after the original watch activity has been
		/// deleted, the service must attempt to re-upload the original watch FIT so the activity is
		/// not permanently lost from Garmin.
		/// </summary>
		[Test]
		public async Task FitMerge_When_MergedUploadFails_AttemptRestoreOfOriginal()
		{
			var (mocker, service, _) = BuildMerge(visibleAfterUploads: null);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.UploadActivity(It.IsAny<string>(), ".fit", It.IsAny<GarminApiAuthentication>()))
				.ThrowsAsync(new Exception("Garmin upload service unavailable"));

			var result = (await service.EnrichAsync(new[] { BuildWorkout("w1") })).Single();

			result.MergeStatus.Should().Be(MergeStatus.RestoreFailed);
			mocker.GetMock<IGarminApiClient>()
				.Verify(c => c.DeleteActivityAsync(WatchActivityId, It.IsAny<GarminApiAuthentication>()), Times.Once);
			// 3 attempts for the merged FIT, then 3 to restore the original
			mocker.GetMock<IGarminApiClient>()
				.Verify(c => c.UploadActivity(It.IsAny<string>(), ".fit", It.IsAny<GarminApiAuthentication>()), Times.Exactly(6));
		}

		[TestCase("{\"detailedImportResult\":{\"uploadId\":\"123\",\"successes\":[],\"failures\":[]}}", 123L)]
		[TestCase("{\"detailedImportResult\":{\"uploadId\":456,\"successes\":[],\"failures\":[]}}", 456L)]
		[TestCase("{\"detailedImportResult\":{\"uploadId\":\"\",\"successes\":[],\"failures\":[]}}", null)]
		public void UploadResponse_ReadsUploadIdAsNumberOrText(string json, long? expected)
		{
			var response = System.Text.Json.JsonSerializer.Deserialize<UploadResponse>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

			response.DetailedImportResult.UploadId.Should().Be(expected);
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
