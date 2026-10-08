using Common.Helpers;
using Common.Observe;
using Conversion;
using Flurl.Http;
using Garmin.Auth;
using Garmin.Database;
using Garmin.Dto;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Garmin;

public interface IGarminMergeVerificationService
{
	/// <summary>
	/// Checks merged uploads that are due and re-uploads any that Garmin stored without cadence and power.
	/// </summary>
	Task VerifyPendingAsync();
}

/// <summary>
/// Garmin occasionally keeps a merged upload but drops its cadence and power, when the upload lands
/// before Garmin has finished deleting the original watch activity. This checks each merged upload
/// after a delay and, if the data is missing, deletes it and uploads the merged FIT again.
/// </summary>
public class GarminMergeVerificationService : IGarminMergeVerificationService
{
	private static readonly ILogger _logger = LogContext.ForClass<GarminMergeVerificationService>();

	public static readonly TimeSpan VerifyDelay = TimeSpan.FromMinutes(30);
	// Re-uploading straight after the delete would hit the same race, so wait at least this long.
	public static readonly TimeSpan ReuploadDelay = TimeSpan.FromMinutes(2);
	public const int MaxReuploads = 3;
	private static readonly TimeSpan GiveUpFindingActivityAfter = TimeSpan.FromHours(2);
	private static readonly TimeSpan NearbyWindow = TimeSpan.FromMinutes(15);

	private readonly IGarminApiClient _apiClient;
	private readonly IGarminAuthenticationService _authService;
	private readonly IGarminMergeDb _mergeDb;

	public GarminMergeVerificationService(IGarminApiClient apiClient, IGarminAuthenticationService authService, IGarminMergeDb mergeDb)
	{
		_apiClient = apiClient;
		_authService = authService;
		_mergeDb = mergeDb;
	}

	/// <summary>
	/// Merged FITs awaiting a check sit next to the original-watch backups, so the WebUI backup list
	/// can download or upload one by hand if the check gives up.
	/// </summary>
	public static string GetPendingFitPath(long originalGarminActivityId)
	{
		return Path.Join(GarminActivityEnrichmentService.GetFitBackupDirectory(), $"{originalGarminActivityId}_merged.fit");
	}

	public async Task VerifyPendingAsync()
	{
		using var tracing = Tracing.Trace($"{nameof(GarminMergeVerificationService)}.{nameof(VerifyPendingAsync)}");

		var due = (await _mergeDb.GetPendingVerificationsAsync())
			.Where(p => p.CheckAfterUtc <= DateTime.UtcNow)
			.ToList();
		if (due.Count == 0)
			return;

		var auth = await _authService.GetGarminAuthenticationAsync();
		if (auth.AuthStage == AuthStage.NeedMfaToken || auth.AuthStage == AuthStage.None)
		{
			_logger.Warning("Merge check: not authenticated with Garmin, skipping {Count} pending check(s).", due.Count);
			return;
		}

		foreach (var pending in due)
		{
			try
			{
				await ProcessAsync(pending, auth);
			}
			catch (Exception e)
			{
				_logger.Error(e, "Merge check: failed for original activity {OriginalId}, will try again on the next run. {Message}", pending.OriginalGarminActivityId, e.Message);
			}
		}
	}

	private async Task ProcessAsync(PendingMergeVerification pending, GarminApiAuthentication auth)
	{
		if (!File.Exists(pending.MergedFitPath))
		{
			_logger.Warning("Merge check: merged FIT {Path} is missing, dropping the check for {OriginalId}", pending.MergedFitPath, pending.OriginalGarminActivityId);
			await _mergeDb.RemovePendingVerificationAsync(pending.OriginalGarminActivityId);
			return;
		}

		if (pending.DeletedGarminActivityId is not null)
		{
			await ReuploadAsync(pending, auth);
			return;
		}

		if (pending.GarminActivityId is null && !await TryResolveUploadedActivityAsync(pending, auth))
			return;

		var activityId = pending.GarminActivityId.Value;
		byte[] storedFit;
		try
		{
			storedFit = await _apiClient.DownloadActivityFitAsync(activityId, auth);
		}
		catch (FlurlHttpException e) when (e.StatusCode == 404)
		{
			_logger.Information("Merge check: activity {ActivityId} no longer exists in Garmin, nothing to check", activityId);
			await CompleteAsync(pending);
			return;
		}

		var stored = GarminFitMergeService.CountCadenceAndPowerRecords(storedFit);
		var dropped = (pending.ExpectedCadenceRecords > 0 && stored.CadenceRecords == 0)
			|| (pending.ExpectedPowerRecords > 0 && stored.PowerRecords == 0);

		if (!dropped)
		{
			_logger.Information("Merge check: Garmin activity {ActivityId} has its cadence ({Cadence} records) and power ({Power} records)", activityId, stored.CadenceRecords, stored.PowerRecords);
			await CompleteAsync(pending);
			return;
		}

		if (pending.Reuploads >= MaxReuploads)
		{
			_logger.Error("Merge check: Garmin activity {ActivityId} is still missing cadence and power after {Count} re-uploads. Giving up; the merged FIT is kept in the WebUI FIT backups as {Path}", activityId, pending.Reuploads, pending.MergedFitPath);
			await _mergeDb.RemovePendingVerificationAsync(pending.OriginalGarminActivityId);
			return;
		}

		_logger.Warning("Merge check: Garmin dropped cadence and power from activity {ActivityId} (stored {Cadence}/{Power} records, expected {ExpectedCadence}/{ExpectedPower}). Deleting it to upload the merged FIT again.",
			activityId, stored.CadenceRecords, stored.PowerRecords, pending.ExpectedCadenceRecords, pending.ExpectedPowerRecords);

		// Save the new state before deleting, so a delete that succeeds on Garmin but errors here
		// still leads to a re-upload instead of the check giving up on a missing activity.
		var nearby = await SearchNearWorkoutAsync(pending, auth);
		pending.PreExistingActivityIds = nearby.Select(a => a.ActivityId).Append(activityId).Distinct().ToList();
		pending.GarminActivityId = null;
		pending.DeletedGarminActivityId = activityId;
		pending.CheckAfterUtc = DateTime.UtcNow + ReuploadDelay;
		await _mergeDb.UpsertPendingVerificationAsync(pending);

		await _apiClient.DeleteActivityAsync(activityId, auth);
	}

	private async Task ReuploadAsync(PendingMergeVerification pending, GarminApiAuthentication auth)
	{
		var deletedId = pending.DeletedGarminActivityId.Value;
		var nearby = await SearchNearWorkoutAsync(pending, auth);
		if (nearby.Any(a => a.ActivityId == deletedId))
		{
			_logger.Information("Merge check: Garmin still lists deleted activity {ActivityId}, deleting again and waiting before re-uploading", deletedId);
			pending.CheckAfterUtc = DateTime.UtcNow + ReuploadDelay;
			await _mergeDb.UpsertPendingVerificationAsync(pending);
			try
			{
				await _apiClient.DeleteActivityAsync(deletedId, auth);
			}
			catch (Exception e)
			{
				_logger.Warning(e, "Merge check: repeat delete of {ActivityId} failed, will check again on the next run", deletedId);
			}
			return;
		}

		// An earlier attempt may have reached Garmin even though it errored here; don't upload a second copy.
		var alreadyUploaded = FindMergedUpload(pending, nearby);
		if (alreadyUploaded is not null)
		{
			_logger.Information("Merge check: found activity {ActivityId} from an earlier re-upload", alreadyUploaded);
			await AdoptUploadedActivityAsync(pending, alreadyUploaded.Value, auth);
			return;
		}

		if (pending.Reuploads >= MaxReuploads)
		{
			_logger.Error("Merge check: could not re-upload the merged FIT for original activity {OriginalId} after {Count} attempts. Upload {Path} from the WebUI FIT backups to restore it", pending.OriginalGarminActivityId, pending.Reuploads, pending.MergedFitPath);
			await _mergeDb.RemovePendingVerificationAsync(pending.OriginalGarminActivityId);
			return;
		}

		// Count the attempt before uploading so repeated upload failures still reach the limit.
		pending.Reuploads++;
		await _mergeDb.UpsertPendingVerificationAsync(pending);

		UploadResponse response = null;
		await RetryHelper.RetryWithBackoffAsync(
			async () => response = await _apiClient.UploadActivity(pending.MergedFitPath, ".fit", auth),
			logger: _logger,
			operationName: $"Merge check re-upload {pending.OriginalGarminActivityId}",
			isRetriable: ex => !(ex is FlurlHttpException fhe && fhe.StatusCode == 415));
		_logger.Information("Merge check: re-uploaded merged FIT for original activity {OriginalId} (re-upload {Count}/{Max})", pending.OriginalGarminActivityId, pending.Reuploads, MaxReuploads);

		pending.DeletedGarminActivityId = null;
		pending.UploadedAtUtc = DateTime.UtcNow;
		// Garmin decides whether to keep the data while it processes the upload, so a short wait is enough.
		pending.CheckAfterUtc = pending.UploadedAtUtc + ReuploadDelay;

		var newActivityId = response?.DetailedImportResult?.Successes?.FirstOrDefault()?.InternalId;
		if (newActivityId is null && response?.DetailedImportResult?.UploadId is long uploadId)
			newActivityId = await _apiClient.PollUploadActivityIdAsync(uploadId, auth);

		if (newActivityId is null)
			await _mergeDb.UpsertPendingVerificationAsync(pending);
		else
			await AdoptUploadedActivityAsync(pending, newActivityId.Value, auth);
	}

	private async Task<bool> TryResolveUploadedActivityAsync(PendingMergeVerification pending, GarminApiAuthentication auth)
	{
		var activityId = FindMergedUpload(pending, await SearchNearWorkoutAsync(pending, auth));
		if (activityId is not null)
		{
			await AdoptUploadedActivityAsync(pending, activityId.Value, auth);
			return true;
		}

		if (DateTime.UtcNow - pending.UploadedAtUtc > GiveUpFindingActivityAfter)
		{
			_logger.Warning("Merge check: could not find the uploaded activity for original {OriginalId}, giving up on the check; the merged FIT is kept in the WebUI FIT backups as {Path}", pending.OriginalGarminActivityId, pending.MergedFitPath);
			await _mergeDb.RemovePendingVerificationAsync(pending.OriginalGarminActivityId);
		}
		return false;
	}

	// The merged FIT keeps the watch's start time, so only a new activity starting at that same moment is ours.
	// Anything else nearby (a stretch, a second class) is never touched.
	private static long? FindMergedUpload(PendingMergeVerification pending, IEnumerable<GarminActivitySummary> nearby)
	{
		return nearby
			.Where(a => !pending.PreExistingActivityIds.Contains(a.ActivityId)
				&& GarminActivityEnrichmentService.TryParseGarminStartTime(a.StartTimeGMT, out var start)
				&& Math.Abs((start - pending.ActivityStartUtc).TotalSeconds) <= 2)
			.Select(a => (long?)a.ActivityId)
			.FirstOrDefault();
	}

	private async Task AdoptUploadedActivityAsync(PendingMergeVerification pending, long activityId, GarminApiAuthentication auth)
	{
		await _apiClient.UpdateActivityAsync(activityId, new GarminActivityUpdateRequest
		{
			ActivityId = activityId,
			ActivityName = pending.ActivityName,
			Description = pending.Description,
		}, auth);

		pending.GarminActivityId = activityId;
		pending.DeletedGarminActivityId = null;
		await _mergeDb.UpsertPendingVerificationAsync(pending);
	}

	private async Task<ICollection<GarminActivitySummary>> SearchNearWorkoutAsync(PendingMergeVerification pending, GarminApiAuthentication auth)
	{
		var activities = await _apiClient.SearchActivitiesAsync(pending.WorkoutStartUtc - NearbyWindow, pending.WorkoutStartUtc + NearbyWindow, auth);
		// The search works on whole dates, so keep only activities that start near this workout.
		return activities?
			.Where(a => GarminActivityEnrichmentService.TryParseGarminStartTime(a.StartTimeGMT, out var start)
				&& (start - pending.WorkoutStartUtc).Duration() <= NearbyWindow)
			.ToList()
			?? new List<GarminActivitySummary>();
	}

	private async Task CompleteAsync(PendingMergeVerification pending)
	{
		await _mergeDb.RemovePendingVerificationAsync(pending.OriginalGarminActivityId);
		File.Delete(pending.MergedFitPath);
	}
}
