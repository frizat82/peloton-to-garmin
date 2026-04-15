using Api.Contract;
using Api.Controllers;
using Common.Dto;
using FluentAssertions;
using Garmin;
using Garmin.Database;
using Garmin.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Moq.AutoMock;
using NUnit.Framework;
using Sync;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UnitTests.Api.Controllers
{
	public class GarminMergeControllerTests
	{
		[Test]
		public async Task GetAsync_Returns_Records_From_Db()
		{
			var mocker = new AutoMocker();
			var controller = mocker.CreateInstance<GarminMergeController>();
			mocker.GetMock<IGarminMergeDb>()
				.Setup(db => db.GetRecentAsync(It.IsAny<int>()))
				.ReturnsAsync(new List<GarminMergeRecord>
				{
					new GarminMergeRecord
					{
						PelotonWorkoutId = "w1",
						PelotonWorkoutTitle = "Cycling Class",
						GarminActivityId = 42,
						MergedAt = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc),
						Source = MergeSource.Auto,
					}
				});

			var actionResult = await controller.GetAsync();

			var result = actionResult.Result as OkObjectResult;
			result.Should().NotBeNull();
			var response = result.Value as GarminMergeGetResponse;
			response.Records.Should().ContainSingle();
		}

		[Test]
		public async Task PostAsync_When_WorkoutId_IsNull_Returns_400()
		{
			var mocker = new AutoMocker();
			var controller = mocker.CreateInstance<GarminMergeController>();

			var actionResult = await controller.PostAsync(new GarminMergePostRequest { WorkoutId = null });

			var result = actionResult.Result as BadRequestObjectResult;
			result.Should().NotBeNull();
		}

		[Test]
		public async Task PostAsync_When_WorkoutId_IsWhitespace_Returns_400()
		{
			var mocker = new AutoMocker();
			var controller = mocker.CreateInstance<GarminMergeController>();

			var actionResult = await controller.PostAsync(new GarminMergePostRequest { WorkoutId = "   " });

			var result = actionResult.Result as BadRequestObjectResult;
			result.Should().NotBeNull();
		}

		[Test]
		public async Task PostAsync_When_Workout_Was_Enriched_Returns_MergedMessage()
		{
			var mocker = new AutoMocker();
			var controller = mocker.CreateInstance<GarminMergeController>();
			var syncResult = new SyncResult { SyncSuccess = true, MergeResults = new List<GarminEnrichmentResult> { new GarminEnrichmentResult { PelotonWorkoutId = "workout1", HasMatch = true } } };
			mocker.GetMock<ISyncService>()
				.Setup(s => s.SyncAsync(It.IsAny<IEnumerable<string>>(), null, false))
				.ReturnsAsync(syncResult);

			var actionResult = await controller.PostAsync(new GarminMergePostRequest { WorkoutId = "workout1" });

			var result = actionResult.Result as OkObjectResult;
			result.Should().NotBeNull();
			var response = result.Value as GarminMergePostResponse;
			response.Success.Should().BeTrue();
			response.Message.Should().Contain("merged");
		}

		[Test]
		public async Task PostAsync_When_Workout_Was_Not_Enriched_Returns_UploadedMessage()
		{
			var mocker = new AutoMocker();
			var controller = mocker.CreateInstance<GarminMergeController>();
			var syncResult = new SyncResult { SyncSuccess = true, MergeResults = new List<GarminEnrichmentResult>() };
			mocker.GetMock<ISyncService>()
				.Setup(s => s.SyncAsync(It.IsAny<IEnumerable<string>>(), null, false))
				.ReturnsAsync(syncResult);

			var actionResult = await controller.PostAsync(new GarminMergePostRequest { WorkoutId = "workout1" });

			var result = actionResult.Result as OkObjectResult;
			result.Should().NotBeNull();
			var response = result.Value as GarminMergePostResponse;
			response.Success.Should().BeTrue();
			response.Message.Should().Contain("uploaded");
		}

		[Test]
		public async Task PostAsync_When_SyncFailed_Returns_ErrorMessage()
		{
			var mocker = new AutoMocker();
			var controller = mocker.CreateInstance<GarminMergeController>();
			var syncResult = new SyncResult { SyncSuccess = false };
			syncResult.Errors.Add(new ServiceError { Message = "Peloton auth failed." });
			mocker.GetMock<ISyncService>()
				.Setup(s => s.SyncAsync(It.IsAny<IEnumerable<string>>(), null, false))
				.ReturnsAsync(syncResult);

			var actionResult = await controller.PostAsync(new GarminMergePostRequest { WorkoutId = "workout1" });

			var result = actionResult.Result as OkObjectResult;
			result.Should().NotBeNull();
			var response = result.Value as GarminMergePostResponse;
			response.Success.Should().BeFalse();
			response.Message.Should().Be("Peloton auth failed.");
		}

		[Test]
		public async Task PostAsync_When_SyncThrows_Returns_500()
		{
			var mocker = new AutoMocker();
			var controller = mocker.CreateInstance<GarminMergeController>();
			mocker.GetMock<ISyncService>()
				.Setup(s => s.SyncAsync(It.IsAny<IEnumerable<string>>(), null, false))
				.ThrowsAsync(new Exception("Unexpected boom."));

			var actionResult = await controller.PostAsync(new GarminMergePostRequest { WorkoutId = "workout1" });

			var result = actionResult.Result as ObjectResult;
			result.Should().NotBeNull();
			result.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
		}
	}
}
