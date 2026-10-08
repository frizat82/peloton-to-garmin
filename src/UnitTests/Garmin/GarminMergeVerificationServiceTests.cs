using Dynastream.Fit;
using FluentAssertions;
using Flurl.Http;
using Flurl.Http.Testing;
using Garmin;
using Garmin.Auth;
using Garmin.Database;
using Garmin.Dto;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DateTime = System.DateTime;
using File = System.IO.File;

namespace UnitTests.Garmin
{
	public class GarminMergeVerificationServiceTests
	{
		private static readonly DateTime WorkoutStart = new DateTime(2026, 10, 7, 16, 29, 0, System.DateTimeKind.Utc);

		private string _mergedFitPath;

		[SetUp]
		public void SetUp()
		{
			_mergedFitPath = Path.Join(Path.GetTempPath(), $"p2g_verify_test_{System.Guid.NewGuid()}.fit");
			File.WriteAllBytes(_mergedFitPath, BuildFit(withCadenceAndPower: true));
		}

		[TearDown]
		public void TearDown()
		{
			if (File.Exists(_mergedFitPath))
				File.Delete(_mergedFitPath);
		}

		private static byte[] BuildFit(bool withCadenceAndPower)
		{
			var fileId = new FileIdMesg();
			fileId.SetType(Dynastream.Fit.File.Activity);
			var messages = new List<Mesg> { fileId };
			for (uint i = 0; i < 10; i++)
			{
				var record = new RecordMesg();
				record.SetTimestamp(new Dynastream.Fit.DateTime(1_000_000_000 + i));
				record.SetHeartRate(120);
				if (withCadenceAndPower)
				{
					record.SetCadence(85);
					record.SetPower(200);
				}
				messages.Add(record);
			}

			using var ms = new MemoryStream();
			var encoder = new Encode(ProtocolVersion.V20);
			encoder.Open(ms);
			encoder.Write(messages);
			encoder.Close();
			return ms.ToArray();
		}

		private PendingMergeVerification BuildPending(long? garminActivityId = 100)
		{
			return new PendingMergeVerification
			{
				OriginalGarminActivityId = 42,
				WorkoutStartUtc = WorkoutStart,
				ActivityStartUtc = WorkoutStart.AddMinutes(1),
				ExpectedCadenceRecords = 10,
				ExpectedPowerRecords = 10,
				ActivityName = "60 min Power Zone Ride",
				Description = "desc",
				MergedFitPath = _mergedFitPath,
				GarminActivityId = garminActivityId,
				PreExistingActivityIds = new List<long> { 7, 42 },
				UploadedAtUtc = DateTime.UtcNow.AddMinutes(-31),
				CheckAfterUtc = DateTime.UtcNow.AddMinutes(-1),
			};
		}

		private static GarminActivitySummary Activity(long id, int startMinutesAfterWorkout = 1) => new GarminActivitySummary
		{
			ActivityId = id,
			StartTimeGMT = WorkoutStart.AddMinutes(startMinutesAfterWorkout).ToString("yyyy-MM-dd HH:mm:ss"),
		};

		private static AutoMocker BuildMocker(PendingMergeVerification pending)
		{
			var mocker = new AutoMocker();
			mocker.GetMock<IGarminMergeDb>()
				.Setup(db => db.GetPendingVerificationsAsync())
				.ReturnsAsync(new List<PendingMergeVerification> { pending });
			mocker.GetMock<IGarminAuthenticationService>()
				.Setup(a => a.GetGarminAuthenticationAsync())
				.ReturnsAsync(new GarminApiAuthentication { AuthStage = AuthStage.Completed });
			return mocker;
		}

		[Test]
		public async Task VerifyPending_When_NotDue_DoesNothing()
		{
			var pending = BuildPending();
			pending.CheckAfterUtc = DateTime.UtcNow.AddMinutes(10);
			var mocker = BuildMocker(pending);

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminApiClient>().VerifyNoOtherCalls();
			mocker.GetMock<IGarminMergeDb>().Verify(db => db.RemovePendingVerificationAsync(It.IsAny<long>()), Times.Never);
		}

		[Test]
		public async Task VerifyPending_When_GarminKeptTheData_CompletesTheCheck()
		{
			var pending = BuildPending();
			var mocker = BuildMocker(pending);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.DownloadActivityFitAsync(100, It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(BuildFit(withCadenceAndPower: true));

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminApiClient>().Verify(c => c.DeleteActivityAsync(It.IsAny<long>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
			mocker.GetMock<IGarminMergeDb>().Verify(db => db.RemovePendingVerificationAsync(42), Times.Once);
			File.Exists(_mergedFitPath).Should().BeFalse();
		}

		[Test]
		public async Task VerifyPending_When_GarminDroppedTheData_DeletesActivityAndSchedulesReupload()
		{
			var pending = BuildPending();
			var mocker = BuildMocker(pending);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.DownloadActivityFitAsync(100, It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(BuildFit(withCadenceAndPower: false));
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary> { Activity(7), Activity(100) });

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminApiClient>().Verify(c => c.DeleteActivityAsync(100, It.IsAny<GarminApiAuthentication>()), Times.Once);
			mocker.GetMock<IGarminApiClient>().Verify(c => c.UploadActivity(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
			pending.GarminActivityId.Should().BeNull();
			pending.DeletedGarminActivityId.Should().Be(100);
			pending.PreExistingActivityIds.Should().BeEquivalentTo(new[] { 7L, 100L });
			pending.CheckAfterUtc.Should().BeAfter(DateTime.UtcNow);
			mocker.GetMock<IGarminMergeDb>().Verify(db => db.UpsertPendingVerificationAsync(pending), Times.Once);
			File.Exists(_mergedFitPath).Should().BeTrue();
		}

		[Test]
		public async Task VerifyPending_When_DroppedAfterMaxReuploads_GivesUpWithoutDeleting()
		{
			var pending = BuildPending();
			pending.Reuploads = GarminMergeVerificationService.MaxReuploads;
			var mocker = BuildMocker(pending);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.DownloadActivityFitAsync(100, It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(BuildFit(withCadenceAndPower: false));

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminApiClient>().Verify(c => c.DeleteActivityAsync(It.IsAny<long>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
			mocker.GetMock<IGarminMergeDb>().Verify(db => db.RemovePendingVerificationAsync(42), Times.Once);
			File.Exists(_mergedFitPath).Should().BeTrue();
		}

		[Test]
		public async Task VerifyPending_When_ActivityNoLongerExists_CompletesTheCheck()
		{
			var pending = BuildPending();
			var mocker = BuildMocker(pending);
			using var httpTest = new HttpTest();
			httpTest.RespondWith(status: 404);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.DownloadActivityFitAsync(100, It.IsAny<GarminApiAuthentication>()))
				.Returns(async () => { await "http://garmin.test".GetBytesAsync(); return null; });

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminApiClient>().Verify(c => c.DeleteActivityAsync(It.IsAny<long>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
			mocker.GetMock<IGarminMergeDb>().Verify(db => db.RemovePendingVerificationAsync(42), Times.Once);
		}

		[Test]
		public async Task VerifyPending_When_DeletedActivityStillListed_WaitsBeforeReuploading()
		{
			var pending = BuildPending(garminActivityId: null);
			pending.DeletedGarminActivityId = 100;
			var mocker = BuildMocker(pending);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary> { Activity(7), Activity(100) });

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminApiClient>().Verify(c => c.UploadActivity(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
			pending.DeletedGarminActivityId.Should().Be(100);
			pending.CheckAfterUtc.Should().BeAfter(DateTime.UtcNow);
		}

		[Test]
		public async Task VerifyPending_When_DeleteHasGoneThrough_ReuploadsMergedFit()
		{
			var pending = BuildPending(garminActivityId: null);
			pending.DeletedGarminActivityId = 100;
			var mocker = BuildMocker(pending);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary> { Activity(7) });

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminApiClient>().Verify(c => c.UploadActivity(_mergedFitPath, ".fit", It.IsAny<GarminApiAuthentication>()), Times.Once);
			pending.Reuploads.Should().Be(1);
			pending.DeletedGarminActivityId.Should().BeNull();
			pending.GarminActivityId.Should().BeNull();
		}

		[Test]
		public async Task VerifyPending_When_ReuploadReturnsActivityId_RenamesThatActivity()
		{
			var pending = BuildPending(garminActivityId: null);
			pending.DeletedGarminActivityId = 100;
			var mocker = BuildMocker(pending);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary> { Activity(7) });
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.UploadActivity(_mergedFitPath, ".fit", It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new UploadResponse { DetailedImportResult = new DetailedImportResult { Successes = new List<Success> { new Success { InternalId = 500 } } } });

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(500, It.Is<GarminActivityUpdateRequest>(r => r.ActivityName == "60 min Power Zone Ride"), It.IsAny<GarminApiAuthentication>()), Times.Once);
			pending.GarminActivityId.Should().Be(500);
			pending.CheckAfterUtc.Should().BeAfter(DateTime.UtcNow);
		}

		[Test]
		public async Task VerifyPending_When_EarlierReuploadAlreadyLanded_AdoptsItInsteadOfUploadingAgain()
		{
			var pending = BuildPending(garminActivityId: null);
			pending.DeletedGarminActivityId = 100;
			pending.Reuploads = 1;
			var mocker = BuildMocker(pending);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary> { Activity(7), Activity(600) });

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminApiClient>().Verify(c => c.UploadActivity(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
			pending.GarminActivityId.Should().Be(600);
			pending.DeletedGarminActivityId.Should().BeNull();
		}

		[Test]
		public async Task VerifyPending_When_ReuploadAttemptsExhausted_GivesUpAndKeepsMergedFit()
		{
			var pending = BuildPending(garminActivityId: null);
			pending.DeletedGarminActivityId = 100;
			pending.Reuploads = GarminMergeVerificationService.MaxReuploads;
			var mocker = BuildMocker(pending);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary> { Activity(7) });

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminApiClient>().Verify(c => c.UploadActivity(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
			mocker.GetMock<IGarminMergeDb>().Verify(db => db.RemovePendingVerificationAsync(42), Times.Once);
			File.Exists(_mergedFitPath).Should().BeTrue();
		}

		[Test]
		public async Task VerifyPending_When_ReuploadFails_CountsTheAttempt()
		{
			var pending = BuildPending(garminActivityId: null);
			pending.DeletedGarminActivityId = 100;
			var mocker = BuildMocker(pending);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary> { Activity(7) });
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.UploadActivity(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GarminApiAuthentication>()))
				.ThrowsAsync(new System.Exception("409 duplicate"));

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			pending.Reuploads.Should().Be(1);
			pending.DeletedGarminActivityId.Should().Be(100);
			mocker.GetMock<IGarminMergeDb>().Verify(db => db.UpsertPendingVerificationAsync(pending), Times.Once);
		}

		[Test]
		public async Task VerifyPending_When_UploadedActivityUnknown_FindsAndRenamesItBeforeChecking()
		{
			var pending = BuildPending(garminActivityId: null);
			var mocker = BuildMocker(pending);
			var otherDayActivity = new GarminActivitySummary { ActivityId = 300, StartTimeGMT = WorkoutStart.AddHours(5).ToString("yyyy-MM-dd HH:mm:ss") };
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary> { otherDayActivity, Activity(250, startMinutesAfterWorkout: 8), Activity(7), Activity(200) });
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.DownloadActivityFitAsync(200, It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(BuildFit(withCadenceAndPower: true));

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(200,
				It.Is<GarminActivityUpdateRequest>(r => r.ActivityName == "60 min Power Zone Ride" && r.Description == "desc"),
				It.IsAny<GarminApiAuthentication>()), Times.Once);
			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(250, It.IsAny<GarminActivityUpdateRequest>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
			mocker.GetMock<IGarminMergeDb>().Verify(db => db.RemovePendingVerificationAsync(42), Times.Once);
		}

		[Test]
		public async Task VerifyPending_When_OnlyUnrelatedNewActivityNearby_LeavesItAlone()
		{
			var pending = BuildPending(garminActivityId: null);
			var mocker = BuildMocker(pending);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.SearchActivitiesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<GarminApiAuthentication>()))
				.ReturnsAsync(new List<GarminActivitySummary> { Activity(7), Activity(250, startMinutesAfterWorkout: 8) });

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminApiClient>().Verify(c => c.UpdateActivityAsync(It.IsAny<long>(), It.IsAny<GarminActivityUpdateRequest>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
			mocker.GetMock<IGarminApiClient>().Verify(c => c.DownloadActivityFitAsync(It.IsAny<long>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
			mocker.GetMock<IGarminApiClient>().Verify(c => c.DeleteActivityAsync(It.IsAny<long>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
		}

		[Test]
		public async Task VerifyPending_When_GarminCallFails_KeepsTheCheckForNextRun()
		{
			var pending = BuildPending();
			var mocker = BuildMocker(pending);
			mocker.GetMock<IGarminApiClient>()
				.Setup(c => c.DownloadActivityFitAsync(100, It.IsAny<GarminApiAuthentication>()))
				.ThrowsAsync(new System.Exception("Garmin unavailable"));

			await mocker.CreateInstance<GarminMergeVerificationService>().VerifyPendingAsync();

			mocker.GetMock<IGarminMergeDb>().Verify(db => db.RemovePendingVerificationAsync(It.IsAny<long>()), Times.Never);
			mocker.GetMock<IGarminApiClient>().Verify(c => c.DeleteActivityAsync(It.IsAny<long>(), It.IsAny<GarminApiAuthentication>()), Times.Never);
		}
	}
}
