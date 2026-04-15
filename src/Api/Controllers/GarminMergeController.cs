using Api.Contract;
using Garmin.Database;
using Garmin.Dto;
using Microsoft.AspNetCore.Mvc;
using Sync;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Api.Controllers;

[ApiController]
[Route("api/garminmerge")]
[Produces("application/json")]
[Consumes("application/json")]
public class GarminMergeController : Controller
{
	private readonly IGarminMergeDb _mergeDb;
	private readonly ISyncService _syncService;

	public GarminMergeController(IGarminMergeDb mergeDb, ISyncService syncService)
	{
		_mergeDb = mergeDb;
		_syncService = syncService;
	}

	/// <summary>
	/// Returns recent merge records (Peloton workouts that were merged into existing Garmin activities).
	/// </summary>
	/// <response code="200">Returns the list of merge records.</response>
	[HttpGet]
	[ProducesResponseType(typeof(GarminMergeGetResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<GarminMergeGetResponse>> GetAsync()
	{
		var records = await _mergeDb.GetRecentAsync();
		var response = new GarminMergeGetResponse
		{
			Records = records.Select(r => new GarminMergeRecordDto
			{
				PelotonWorkoutId = r.PelotonWorkoutId,
				PelotonWorkoutTitle = r.PelotonWorkoutTitle,
				GarminActivityId = r.GarminActivityId,
				MergedAt = r.MergedAt,
				Source = r.Source == MergeSource.Manual ? MergeSourceDto.Manual : MergeSourceDto.Auto,
			}).ToList()
		};
		return Ok(response);
	}

	/// <summary>
	/// Manually triggers a merge for a specific Peloton workout into an existing Garmin activity.
	/// Re-runs the full sync for this workout ID, which will attempt enrichment and fall back to upload if no match is found.
	/// </summary>
	/// <response code="200">Merge attempted. See Success and Message for result.</response>
	/// <response code="400">Invalid request.</response>
	/// <summary>
	/// Previews which of the given Peloton workout IDs would match an existing Garmin device activity.
	/// No changes are made.
	/// </summary>
	/// <response code="200">Returns proposed matches for each workout.</response>
	/// <response code="400">Invalid request.</response>
	[HttpPost("preview")]
	[ProducesResponseType(typeof(GarminMergePreviewResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(Contract.ErrorResponse), StatusCodes.Status400BadRequest)]
	public async Task<ActionResult<GarminMergePreviewResponse>> PreviewAsync([FromBody] GarminMergePreviewRequest request)
	{
		if (request?.WorkoutIds == null || !request.WorkoutIds.Any())
			return BadRequest(new Contract.ErrorResponse("WorkoutIds is required."));

		var results = await _syncService.PreviewMergeAsync(request.WorkoutIds);

		var response = new GarminMergePreviewResponse
		{
			Items = results.Select(r => new GarminMergePreviewItem
			{
				PelotonWorkoutId = r.PelotonWorkoutId,
				PelotonWorkoutTitle = r.PelotonWorkoutTitle,
				GarminActivityId = r.HasMatch ? r.GarminActivityId : null,
				GarminActivityName = r.GarminActivityName,
				GarminActivityStartTimeUtc = r.GarminActivityStartTimeUtc,
				MatchDeltaSeconds = r.HasMatch ? r.MatchDeltaSeconds : null,
				HasMatch = r.HasMatch,
			}).ToList()
		};

		return Ok(response);
	}

	[HttpPost]
	[ProducesResponseType(typeof(GarminMergePostResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(Contract.ErrorResponse), StatusCodes.Status400BadRequest)]
	public async Task<ActionResult<GarminMergePostResponse>> PostAsync([FromBody] GarminMergePostRequest request)
	{
		if (string.IsNullOrWhiteSpace(request?.WorkoutId))
			return BadRequest(new Contract.ErrorResponse("WorkoutId is required."));

		try
		{
			var syncResult = await _syncService.SyncAsync(new[] { request.WorkoutId });

			if (!syncResult.SyncSuccess)
			{
				var errorMessage = syncResult.Errors?.FirstOrDefault()?.Message ?? "Sync failed. Check logs for details.";
				return Ok(new GarminMergePostResponse { Success = false, Message = errorMessage });
			}

			// Manual merge always processes a single workout, so EnrichedWorkoutIds contains it directly (no comma-joining).
			if (syncResult.MergeResults.Any(r => r.PelotonWorkoutId == request.WorkoutId))
				return Ok(new GarminMergePostResponse { Success = true, Message = "Workout merged into existing Garmin activity." });

			return Ok(new GarminMergePostResponse { Success = true, Message = "No matching Garmin activity found. Workout uploaded as new activity." });
		}
		catch (Exception e)
		{
			return StatusCode(StatusCodes.Status500InternalServerError, new Contract.ErrorResponse($"Unexpected error: {e.Message}"));
		}
	}
}
