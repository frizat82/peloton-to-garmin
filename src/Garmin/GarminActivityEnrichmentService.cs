using Common.Dto;
using Common.Dto.Peloton;
using Common.Observe;
using Common.Service;
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
using System.Text;
using System.Threading.Tasks;

namespace Garmin;

public interface IGarminActivityEnrichmentService
{
	/// <summary>
	/// Attempts to match each workout to an existing Garmin activity and enrich it with Peloton stats.
	/// Returns enrichment results for each successfully merged workout.
	/// </summary>
	Task<ICollection<GarminEnrichmentResult>> EnrichAsync(IEnumerable<P2GWorkout> workouts);

	/// <summary>
	/// Previews which workouts would match existing Garmin activities without making any changes.
	/// Returns a result for every workout (matched and unmatched).
	/// </summary>
	Task<ICollection<GarminEnrichmentResult>> PreviewAsync(IEnumerable<P2GWorkout> workouts);

	/// <summary>
	/// Uploads a previously backed-up watch FIT file to Garmin Connect.
	/// Used to manually restore an activity that was deleted during a failed merge.
	/// </summary>
	Task<bool> UploadFitBackupToGarminAsync(string fileName);
}

public class GarminActivityEnrichmentService : IGarminActivityEnrichmentService
{
	private static readonly ILogger _logger = LogContext.ForClass<GarminActivityEnrichmentService>();

	/// <summary>Uploads of a merged or restored FIT, spaced out so Garmin can finish processing the deleted original.</summary>
	private const int UploadAttempts = 3;
	private static readonly TimeSpan UploadRetryDelay = TimeSpan.FromSeconds(30);

	/// <summary>Waits between uploads and searches. Tests replace it so they don't actually wait.</summary>
	internal Func<TimeSpan, Task> DelayAsync { get; set; } = Task.Delay;

	private readonly ISettingsService _settingsService;
	private readonly IGarminApiClient _apiClient;
	private readonly IGarminAuthenticationService _authService;
	private readonly IGarminMergeDb _mergeDb;

	public GarminActivityEnrichmentService(ISettingsService settingsService, IGarminApiClient apiClient, IGarminAuthenticationService authService, IGarminMergeDb mergeDb)
	{
		_settingsService = settingsService;
		_apiClient = apiClient;
		_authService = authService;
		_mergeDb = mergeDb;
	}

	public async Task<ICollection<GarminEnrichmentResult>> EnrichAsync(IEnumerable<P2GWorkout> workouts)
	{
		using var tracing = Tracing.Trace($"{nameof(GarminActivityEnrichmentService)}.{nameof(EnrichAsync)}");

		var empty = new List<GarminEnrichmentResult>();
		var settings = await _settingsService.GetSettingsAsync();

		if (!settings.Garmin.EnrichGarminActivities)
			return empty;

		if (!settings.Garmin.Upload)
		{
			_logger.Debug("Garmin upload is disabled, skipping activity enrichment.");
			return empty;
		}

		var auth = await _authService.GetGarminAuthenticationAsync();

		if (!auth.IsSignedIn())
		{
			_logger.Warning("Not authenticated with Garmin, skipping activity enrichment.");
			return empty;
		}

		return await RunMatchingAsync(workouts, auth, settings, dryRun: false);
	}

	public async Task<ICollection<GarminEnrichmentResult>> PreviewAsync(IEnumerable<P2GWorkout> workouts)
	{
		using var tracing = Tracing.Trace($"{nameof(GarminActivityEnrichmentService)}.{nameof(PreviewAsync)}");

		var empty = new List<GarminEnrichmentResult>();
		var settings = await _settingsService.GetSettingsAsync();

		if (!settings.Garmin.EnrichGarminActivities)
			return empty;

		var auth = await _authService.GetGarminAuthenticationAsync();

		if (!auth.IsSignedIn())
		{
			_logger.Warning("Not authenticated with Garmin, skipping activity enrichment preview.");
			return empty;
		}

		return await RunMatchingAsync(workouts, auth, settings, dryRun: true);
	}

	private async Task<ICollection<GarminEnrichmentResult>> RunMatchingAsync(IEnumerable<P2GWorkout> workouts, GarminApiAuthentication auth, Settings settings, bool dryRun)
	{
		var results = new List<GarminEnrichmentResult>();

		// Group by date to avoid redundant search API calls for same-day workouts
		var workoutsByDate = workouts
			.Where(w => w.Workout is not null && w.WorkoutSamples is not null)
			.GroupBy(w => DateTimeOffset.FromUnixTimeSeconds(w.Workout.Start_Time).UtcDateTime.Date);

		foreach (var dateGroup in workoutsByDate)
		{
			ICollection<GarminActivitySummary> activities;
			try
			{
				activities = await _apiClient.SearchActivitiesAsync(dateGroup.Key, dateGroup.Key.AddDays(1), auth);
			}
			catch (Exception e)
			{
				_logger.Error(e, "Failed to search Garmin activities for date {Date}. {Message}", dateGroup.Key, e.Message);
				if (dryRun)
					foreach (var w in dateGroup)
						results.Add(NoMatchResult(w.Workout));
				continue;
			}

			if (activities is null || !activities.Any())
			{
				if (dryRun)
					_logger.Information("Preview: No Garmin activities returned for {Date:yyyy-MM-dd}.", dateGroup.Key);
				else
					_logger.Debug("No Garmin activities found for {Date}.", dateGroup.Key);
				if (dryRun)
					foreach (var w in dateGroup)
						results.Add(NoMatchResult(w.Workout));
				continue;
			}

			if (dryRun)
				_logger.Information("Preview: Found {Count} Garmin activities for {Date:yyyy-MM-dd}: {Names}",
					activities.Count, dateGroup.Key,
					string.Join(", ", activities.Select(a => $"{a.ActivityName} @ {a.StartTimeGMT}")));

			// Match all workouts first, then group by Garmin activity ID so multiple Peloton
			// workouts that land on the same Garmin activity get merged into a single update.
			var matchedGroups = new Dictionary<long, List<(P2GWorkout P2GWorkout, GarminEnrichmentResult Result)>>();

			foreach (var p2gWorkout in dateGroup)
			{
				var workout = p2gWorkout.Workout;
				var pelotonStartUtc = DateTimeOffset.FromUnixTimeSeconds(workout.Start_Time).UtcDateTime;
				var workoutTitle = BuildActivityName(workout) ?? workout.Name ?? workout.Id;

				_logger.Debug("Searching for Garmin activity matching Peloton workout {@WorkoutId} starting at {@StartTime}.", workout.Id, pelotonStartUtc);

				var match = FindBestMatch(activities, pelotonStartUtc, workout, settings.Garmin.ActivityMatchWindowSeconds);

				if (match is null)
				{
					if (dryRun)
					{
						var deltas = activities
							.Where(a => TryParseGarminStartTime(a.StartTimeGMT, out _))
							.Select(a =>
							{
								TryParseGarminStartTime(a.StartTimeGMT, out var t);
								var delta = Math.Abs((t - pelotonStartUtc).TotalSeconds);
								var overlap = a.Duration.HasValue && pelotonStartUtc >= t && pelotonStartUtc <= t.AddSeconds(a.Duration.Value);
								var suffix = overlap ? " [overlap]" : "";
								var dur = a.Duration.HasValue ? $" dur={a.Duration:F0}s" : "";
								return $"{a.ActivityName}: {delta:F0}s delta{suffix}{dur}";
							});
						_logger.Information("Preview: No match for '{WorkoutTitle}' (Peloton start {PelotonStart:HH:mm} UTC, window {Window}s). Garmin candidates: {Deltas}",
							workoutTitle, pelotonStartUtc, settings.Garmin.ActivityMatchWindowSeconds, string.Join(", ", deltas));
						results.Add(NoMatchResult(workout));
					}
					else
						_logger.Debug("No matching Garmin activity found within {Window}s for workout {@WorkoutId}.", settings.Garmin.ActivityMatchWindowSeconds, workout.Id);
					continue;
				}

				TryParseGarminStartTime(match.Value.Activity.StartTimeGMT, out var garminStartUtc);
				var enrichmentResult = new GarminEnrichmentResult
				{
					PelotonWorkoutId = workout.Id,
					PelotonWorkoutTitle = workoutTitle,
					GarminActivityId = match.Value.Activity.ActivityId,
					GarminActivityName = match.Value.Activity.ActivityName,
					GarminActivityStartTimeUtc = garminStartUtc == default ? null : garminStartUtc,
					MatchDeltaSeconds = match.Value.DeltaSeconds,
					HasMatch = true,
				};

				if (!matchedGroups.TryGetValue(match.Value.Activity.ActivityId, out var group))
				{
					group = new List<(P2GWorkout, GarminEnrichmentResult)>();
					matchedGroups[match.Value.Activity.ActivityId] = group;
				}
				group.Add((p2gWorkout, enrichmentResult));
			}

			foreach (var (garminActivityId, group) in matchedGroups)
			{
				// Always surface all matched results (for preview and post-sync display)
				results.AddRange(group.Select(g => g.Result));

				if (dryRun)
				{
					if (group.Count > 1)
						_logger.Information("Preview: {Count} Peloton workouts match Garmin activity {GarminActivityId} — will be merged into one update.",
							group.Count, garminActivityId);
					continue;
				}

				try
				{
					await ApplyGroupUpdateAsync(garminActivityId, group, auth, settings);
				}
				catch (Exception e)
				{
					_logger.Error(e, "Failed to enrich Garmin activity {GarminActivityId}. {Message}", garminActivityId, e.Message);
				}
			}
		}

		return results;
	}

	private async Task ApplyGroupUpdateAsync(long garminActivityId, List<(P2GWorkout P2GWorkout, GarminEnrichmentResult Result)> group, GarminApiAuthentication auth, Settings settings)
	{
		// Use the workout with the most total output as the "primary" (name source).
		// For ties or missing data, first in list wins.
		// Use the workout with the most total output as the primary (name source).
		// For ties or missing data, first in list wins.
		var primary = group
			.OrderByDescending(g => g.P2GWorkout.Workout.Total_Work)
			.First();

		var workoutIds = string.Join(", ", group.Select(g => g.Result.PelotonWorkoutId));
		_logger.Information("Enriching Garmin activity {GarminActivityId} with {Count} Peloton workout(s): {WorkoutIds}.",
			garminActivityId, group.Count, workoutIds);

		var mergeStatus = MergeStatus.Success;
		string mergeStatusDetail = null;
		if (settings.Garmin.MergeFitWithWatch)
		{
			(mergeStatus, mergeStatusDetail) = await ApplyFitMergeAsync(garminActivityId, primary, group, auth, settings);
		}
		else
		{
			var updateRequest = new GarminActivityUpdateRequest
			{
				ActivityId = garminActivityId,
				ActivityName = BuildActivityName(primary.P2GWorkout.Workout),
				Description = group.Count == 1
					? BuildDescription(primary.P2GWorkout.Workout, primary.P2GWorkout.WorkoutSamples)
					: BuildCombinedDescription(group.Select(g => g.P2GWorkout)),
			};

			// Aggregate numeric fields across all workouts in group
			PopulateStructuredFields(updateRequest, group.Select(g => g.P2GWorkout));

			await _apiClient.UpdateActivityAsync(garminActivityId, updateRequest, auth);
			_logger.Information("Successfully enriched Garmin activity {GarminActivityId} via metadata update.", garminActivityId);
		}

		foreach (var (p2gWorkout, result) in group)
		{
			result.MergeStatus = mergeStatus;
			result.StatusDetail = mergeStatusDetail;

			await _mergeDb.SaveAsync(new GarminMergeRecord
			{
				PelotonWorkoutId = result.PelotonWorkoutId,
				PelotonWorkoutTitle = result.PelotonWorkoutTitle,
				GarminActivityId = garminActivityId,
				MergedAt = DateTime.UtcNow,
				Source = MergeSource.Auto,
				Status = mergeStatus,
				StatusDetail = mergeStatusDetail,
			});
		}
	}

	/// <summary>
	/// Every grouped class's exercises, in time order, with offsets shifted to be
	/// relative to the primary workout's start (the time the merge aligns to).
	/// </summary>
	private static List<P2GExercise> CombineExercises(List<(P2GWorkout P2GWorkout, GarminEnrichmentResult Result)> group, long primaryStartUnix)
	{
		return group
			.Where(g => g.P2GWorkout.Exercises is not null)
			.OrderBy(g => g.P2GWorkout.Workout.Start_Time)
			.SelectMany(g => g.P2GWorkout.Exercises.Select(e => e with { StartOffsetSeconds = e.StartOffsetSeconds + (int)(g.P2GWorkout.Workout.Start_Time - primaryStartUnix) }))
			.ToList();
	}

	private async Task<(MergeStatus Status, string Detail)> ApplyFitMergeAsync(
		long garminActivityId,
		(P2GWorkout P2GWorkout, GarminEnrichmentResult Result) primary,
		List<(P2GWorkout P2GWorkout, GarminEnrichmentResult Result)> group,
		GarminApiAuthentication auth,
		Settings settings = null)
	{
		var workoutStart = DateTimeOffset.FromUnixTimeSeconds(primary.P2GWorkout.Workout.Start_Time).UtcDateTime;

		// Snapshot all activity IDs in the vicinity BEFORE we delete/upload anything,
		// so the post-upload search can identify only truly NEW activities and not
		// accidentally grab an existing nearby activity (e.g. a back-to-back ride).
		var preExistingIds = new HashSet<long> { garminActivityId };
		try
		{
			foreach (var a in await SearchAroundAsync(workoutStart, auth))
				preExistingIds.Add(a.ActivityId);
			_logger.Information("FIT merge: snapshotted {Count} pre-existing activity IDs near workout start", preExistingIds.Count);
		}
		catch (Exception e) { _logger.Warning(e, "FIT merge: could not snapshot pre-existing activities; post-upload ID detection may be less precise."); }

		_logger.Information("FIT merge: downloading watch FIT for Garmin activity {GarminActivityId}", garminActivityId);

		byte[] watchFitBytes;
		try
		{
			watchFitBytes = await _apiClient.DownloadActivityFitAsync(garminActivityId, auth);
		}
		catch (Exception e)
		{
			_logger.Warning(e, "FIT merge: failed to download watch FIT for activity {GarminActivityId} — skipping, no changes made. {Message}",
				garminActivityId, e.Message);
			return (MergeStatus.Success, null);
		}

		// Back up the original watch FIT before we delete anything — this is the
		// safety copy the user can download from the WebUI to recover HR/GPS data
		// if the merge or upload goes wrong.
		await SaveOriginalFitBackupAsync(watchFitBytes, garminActivityId, workoutStart,
			BuildActivityName(primary.P2GWorkout.Workout) ?? primary.P2GWorkout.Workout.Id);

		_logger.Information("FIT merge: merging Peloton metrics into watch FIT ({Bytes} bytes)", watchFitBytes.Length);
		var mergedFitBytes = GarminFitMergeService.MergeWatchFitWithPeloton(watchFitBytes, primary.P2GWorkout.WorkoutSamples, primary.P2GWorkout.Workout.Start_Time, primary.P2GWorkout.Workout, settings?.Format?.IncludeTimeInPowerZones ?? false,
			CombineExercises(group, primary.P2GWorkout.Workout.Start_Time), settings?.Format?.Strength?.DefaultSecondsPerRep ?? 0);

		_logger.Information("FIT merge: deleting original Garmin activity {GarminActivityId} before uploading merged FIT", garminActivityId);
		await _apiClient.DeleteActivityAsync(garminActivityId, auth);

		Task<(long? ActivityId, string Error)> UploadAndConfirm(byte[] fitBytes, string what) =>
			UploadAndConfirmAsync(fitBytes, what, workoutStart, preExistingIds, auth);

		var (newActivityId, mergeError) = await UploadAndConfirm(mergedFitBytes, "merged FIT");

		// The original is already deleted, so put the watch recording back rather than leave nothing in Garmin.
		// A merged upload that only now shows up still counts: it is checked once more before restoring.
		newActivityId ??= await FindNewActivityAsync(workoutStart, preExistingIds, auth, polls: 1);
		if (newActivityId is null)
		{
			_logger.Error("FIT merge: {Error} for {GarminActivityId} — restoring original watch FIT", mergeError, garminActivityId);
			var (restoredId, restoreError) = await UploadAndConfirm(watchFitBytes, "original watch FIT");
			if (restoredId is not null)
			{
				_logger.Warning("FIT merge: original watch FIT restored to Garmin as activity {RestoredId}", restoredId);
				return (MergeStatus.OriginalRestored, $"Merge upload failed: {mergeError}");
			}

			_logger.Error("FIT merge: CRITICAL — restore also failed for {GarminActivityId}. Download the backup from the Activity Merge page to recover manually.", garminActivityId);
			return (MergeStatus.RestoreFailed, $"Merge upload failed: {mergeError} | Restore also failed: {restoreError}");
		}

		var nameUpdate = new GarminActivityUpdateRequest
		{
			ActivityId = newActivityId.Value,
			ActivityName = BuildActivityName(primary.P2GWorkout.Workout),
			Description = BuildDescription(primary.P2GWorkout.Workout, primary.P2GWorkout.WorkoutSamples),
		};
		try
		{
			await _apiClient.UpdateActivityAsync(newActivityId.Value, nameUpdate, auth);
			_logger.Information("FIT merge: updated activity name to '{Name}' for new activity {NewId}", nameUpdate.ActivityName, newActivityId.Value);
		}
		catch (Exception e)
		{
			// The merge itself worked; a missing name must not stop it being recorded, or the next sync would merge again.
			_logger.Warning(e, "FIT merge: merged activity {NewId} uploaded but renaming it failed: {Message}", newActivityId.Value, e.Message);
		}
		return (MergeStatus.Success, null);
	}

	/// <summary>
	/// Uploads a FIT file and waits until Garmin shows the new activity. Garmin can refuse an upload made right after
	/// the original was deleted (it still counts as a duplicate) or accept it and never show it, so the upload is
	/// repeated after a pause. Returns the new activity's ID, or null with the last error once every attempt failed.
	/// </summary>
	private async Task<(long? ActivityId, string Error)> UploadAndConfirmAsync(byte[] fitBytes, string what,
		DateTime workoutStart, HashSet<long> preExistingIds, GarminApiAuthentication auth)
	{
		var path = Path.Combine(Path.GetTempPath(), $"p2g_{Guid.NewGuid():N}.fit");
		string error = null;
		try
		{
			await File.WriteAllBytesAsync(path, fitBytes);
			for (var attempt = 1; attempt <= UploadAttempts; attempt++)
			{
				if (attempt > 1)
					await DelayAsync(UploadRetryDelay * (attempt - 1));

				UploadResponse response = null;
				var retry = true;
				try
				{
					response = await _apiClient.UploadActivity(path, ".fit", auth);
					var rejection = response?.GetRejection();
					error = rejection is null ? null : $"Garmin rejected the {what}: {rejection}";
				}
				catch (FlurlHttpException e) when (e.StatusCode == 415)
				{
					error = $"Garmin refused the {what} as an unsupported file: {e.Message}";
					retry = false;
				}
				catch (Exception e)
				{
					error = $"uploading the {what} failed: {e.Message}";
				}

				var newActivityId = await ConfirmUploadAsync(response, accepted: error is null, workoutStart, preExistingIds, auth);
				if (newActivityId is not null)
					return (newActivityId, null);

				error ??= $"Garmin accepted the {what} but never showed it";
				_logger.Warning("FIT merge: {Error} (attempt {Attempt}/{Max})", error, attempt, UploadAttempts);
				if (!retry)
					break;
			}

			return (null, error);
		}
		finally
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}

	/// <summary>
	/// The activity an upload created. Garmin's own reply is used when it names one; otherwise Garmin is searched
	/// for a new activity at the workout's time. After a refused or failed upload only one quick search is made,
	/// in case an earlier attempt went through late.
	/// </summary>
	private async Task<long?> ConfirmUploadAsync(UploadResponse response, bool accepted, DateTime workoutStart, HashSet<long> preExistingIds, GarminApiAuthentication auth)
	{
		if (response?.NewActivityId is long fromReply)
			return fromReply;

		if (accepted && response?.DetailedImportResult?.UploadId is long uploadId)
		{
			try
			{
				if (await _apiClient.PollUploadActivityIdAsync(uploadId, auth) is long polled)
					return polled;
			}
			catch (Exception e) { _logger.Warning(e, "FIT merge: checking upload {UploadId} failed: {Message}", uploadId, e.Message); }
		}

		return await FindNewActivityAsync(workoutStart, preExistingIds, auth, polls: accepted ? 5 : 1);
	}

	private async Task<long?> FindNewActivityAsync(DateTime workoutStart, HashSet<long> preExistingIds, GarminApiAuthentication auth, int polls)
	{
		for (int attempt = 1; attempt <= polls; attempt++)
		{
			await DelayAsync(TimeSpan.FromSeconds(attempt == 1 ? 3 : 4));
			try
			{
				var newActivityId = (await SearchAroundAsync(workoutStart, auth))
					.Where(a => !preExistingIds.Contains(a.ActivityId))
					.Select(a => (long?)a.ActivityId)
					.FirstOrDefault();
				if (newActivityId is not null)
				{
					_logger.Information("FIT merge: found new activity {NewId} via search after {Attempt} attempt(s)", newActivityId, attempt);
					return newActivityId;
				}
			}
			catch (Exception e) { _logger.Warning(e, "FIT merge: searching Garmin for the new activity failed: {Message}", e.Message); }
			_logger.Information("FIT merge: new activity not visible yet (attempt {Attempt}/{Max})", attempt, polls);
		}
		return null;
	}

	/// <summary>Garmin activities that start within 15 minutes of the workout. Garmin's search itself only filters by day.</summary>
	private async Task<IEnumerable<GarminActivitySummary>> SearchAroundAsync(DateTime workoutStart, GarminApiAuthentication auth)
	{
		var window = TimeSpan.FromMinutes(15);
		var activities = await _apiClient.SearchActivitiesAsync(workoutStart - window, workoutStart + window, auth);
		return (activities ?? Array.Empty<GarminActivitySummary>())
			.Where(a => TryParseGarminStartTime(a.StartTimeGMT, out var start) && (start - workoutStart).Duration() <= window);
	}

	public async Task<bool> UploadFitBackupToGarminAsync(string fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
			throw new ArgumentException("Invalid file name.", nameof(fileName));

		var auth = await _authService.GetGarminAuthenticationAsync();
		if (!auth.IsSignedIn())
		{
			_logger.Warning("UploadFitBackup: not authenticated with Garmin.");
			return false;
		}

		var dir = GetFitBackupDirectory();
		var fullPath = Path.Join(dir, fileName);
		if (!File.Exists(fullPath))
			throw new FileNotFoundException("Backup file not found.", fullPath);

		_logger.Information("UploadFitBackup: uploading {FileName} to Garmin", fileName);
		await _apiClient.UploadActivity(fullPath, ".fit", auth);
		_logger.Information("UploadFitBackup: upload complete for {FileName}", fileName);
		return true;
	}


	private static GarminEnrichmentResult NoMatchResult(Workout workout) => new GarminEnrichmentResult
	{
		PelotonWorkoutId = workout.Id,
		PelotonWorkoutTitle = BuildActivityName(workout) ?? workout.Name ?? workout.Id,
		HasMatch = false,
	};

	private static (GarminActivitySummary Activity, double DeltaSeconds)? FindBestMatch(ICollection<GarminActivitySummary> activities, DateTime pelotonStartUtc, Workout workout, int windowSeconds)
	{
		var garminSportKey = GetGarminSportKey(workout);

		(GarminActivitySummary Activity, bool IsSportMatch, double DeltaSeconds) best = (null, false, double.MaxValue);

		foreach (var activity in activities)
		{
			if (!TryParseGarminStartTime(activity.StartTimeGMT, out var garminStartUtc))
				continue;

			var startDeltaSeconds = Math.Abs((garminStartUtc - pelotonStartUtc).TotalSeconds);

			// Also match if the Peloton workout start falls within the Garmin activity's recorded duration
			// (handles multi-sport sessions where Garmin starts recording before the Peloton class begins)
			var pelotonFallsWithinActivity = activity.Duration.HasValue
				&& pelotonStartUtc >= garminStartUtc
				&& pelotonStartUtc <= garminStartUtc.AddSeconds(activity.Duration.Value);

			var effectiveDelta = pelotonFallsWithinActivity ? 0 : startDeltaSeconds;

			if (effectiveDelta > windowSeconds)
				continue;

			var isSportMatch = garminSportKey is not null &&
								activity.ActivityType?.TypeKey?.Contains(garminSportKey, StringComparison.OrdinalIgnoreCase) == true;

			// Use startDeltaSeconds (actual gap between start times) to rank candidates —
			// not effectiveDelta — so a workout whose start time truly aligns with a
			// later Garmin activity always wins over an earlier activity it merely
			// "falls within". The effectiveDelta is only used above to gate admissibility.
			var isBetter = best.Activity is null
				|| (isSportMatch && !best.IsSportMatch)
				|| (isSportMatch == best.IsSportMatch && startDeltaSeconds < best.DeltaSeconds);

			if (isBetter)
				best = (activity, isSportMatch, startDeltaSeconds);
		}

		if (best.Activity is null)
			return null;

		return (best.Activity, best.DeltaSeconds);
	}

	private static bool TryParseGarminStartTime(string startTimeGmt, out DateTime result)
	{
		result = default;
		if (string.IsNullOrEmpty(startTimeGmt))
			return false;

		return DateTime.TryParse(startTimeGmt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out result);
	}

	private static string GetGarminSportKey(Workout workout)
	{
		return workout.Fitness_Discipline switch
		{
			FitnessDiscipline.Cycling or FitnessDiscipline.Bike_Bootcamp => "cycling",
			FitnessDiscipline.Running => "running",
			FitnessDiscipline.Walking => "walking",
			FitnessDiscipline.Strength => "hiit",
			FitnessDiscipline.Cardio => "cardio",
			FitnessDiscipline.Circuit => "multi_sport",
			FitnessDiscipline.Yoga => "yoga",
			FitnessDiscipline.Meditation => "meditation",
			FitnessDiscipline.Stretching => "flexibility",
			FitnessDiscipline.Caesar => "rowing",
			FitnessDiscipline.Caesar_Bootcamp => "multi_sport",
			_ => null
		};
	}

	private static string BuildCombinedDescription(IEnumerable<P2GWorkout> workouts)
	{
		var sb = new StringBuilder();
		var ordered = workouts.OrderBy(w => w.Workout.Start_Time).ToList();
		foreach (var w in ordered)
		{
			var title = BuildActivityName(w.Workout) ?? w.Workout.Name ?? w.Workout.Id;
			sb.AppendLine($"=== {title} ===");
			AppendStats(sb, w.Workout, w.WorkoutSamples);
			sb.AppendLine();
		}
		return sb.ToString().TrimEnd();
	}

	private static string BuildActivityName(Workout workout)
	{
		var title = workout.Ride?.Title;
		var instructorName = workout.Ride?.Instructor?.Name;

		// Peloton sometimes returns the fitness discipline name as the ride title
		// (e.g. "Caesar_Bootcamp", "Circuit") rather than the actual class title.
		// Use a friendly name in those cases.
		if (title == workout.Fitness_Discipline.ToString())
			title = workout.Fitness_Discipline switch
			{
				FitnessDiscipline.Caesar_Bootcamp => "Row Bootcamp",
				FitnessDiscipline.Circuit => "Tread Bootcamp",
				FitnessDiscipline.Bike_Bootcamp => "Cycling Bootcamp",
				_ => workout.Name,
			};

		if (string.IsNullOrWhiteSpace(title))
			return null;

		if (!string.IsNullOrWhiteSpace(instructorName))
			return $"{title} with {instructorName}";

		return title;
	}

	private static string BuildDescription(Workout workout, WorkoutSamples workoutSamples)
	{
		var sb = new StringBuilder();
		sb.AppendLine("=== Peloton Stats ===");
		AppendStats(sb, workout, workoutSamples);
		return sb.ToString().TrimEnd();
	}

	private static void AppendStats(StringBuilder sb, Workout workout, WorkoutSamples workoutSamples)
	{
		if (workout.Total_Work > 0)
			sb.AppendLine($"Total Output: {workout.Total_Work / 1000.0:F0} kJ");

		var outputMetric = GetMetric("output", workoutSamples);
		if (outputMetric?.Average_Value is not null)
			sb.AppendLine($"Avg Power: {outputMetric.Average_Value:F0}W  |  Max Power: {outputMetric.Max_Value:F0}W");

		var cadenceMetric = GetMetric("cadence", workoutSamples);
		if (cadenceMetric?.Average_Value is not null)
			sb.AppendLine($"Avg Cadence: {cadenceMetric.Average_Value:F0} rpm  |  Max Cadence: {cadenceMetric.Max_Value:F0} rpm");

		var resistanceMetric = GetMetric("resistance", workoutSamples);
		if (resistanceMetric?.Average_Value is not null)
			sb.AppendLine($"Avg Resistance: {resistanceMetric.Average_Value:F0}%  |  Max Resistance: {resistanceMetric.Max_Value:F0}%");

		var speedMetric = GetMetric("speed", workoutSamples) ?? GetMetric("split_pace", workoutSamples);
		if (speedMetric?.Average_Value is not null)
		{
			var unit = speedMetric.Display_Unit ?? "";
			sb.AppendLine($"Avg Speed: {speedMetric.Average_Value:F1} {unit}  |  Max Speed: {speedMetric.Max_Value:F1} {unit}");
		}

		var distanceSummary = workoutSamples?.Summaries?.FirstOrDefault(s => s.Slug == "distance");
		if (distanceSummary?.Value is not null)
			sb.AppendLine($"Distance: {distanceSummary.Value:F1} {distanceSummary.Display_Unit}");
	}

	private static void PopulateStructuredFields(GarminActivityUpdateRequest request, IEnumerable<P2GWorkout> workoutList)
	{
		// Only push fields the Garmin watch cannot measure itself.
		// HR and calories stay with Garmin (watch sensor is more accurate).
		// Distance stays with Garmin for tread/outdoor (GPS/accelerometer).
		// For indoor cycling Garmin has no distance source, so we push it there.

		double totalWork = 0;
		double? avgPower = null, maxPower = null;
		double? avgBikeCadence = null, maxBikeCadence = null;
		double? avgRunCadence = null, maxRunCadence = null;
		double? avgSpeed = null, maxSpeed = null;
		double indoorCyclingDistanceMeters = 0;
		int workoutCount = 0;

		foreach (var w in workoutList)
		{
			workoutCount++;
			var discipline = w.Workout.Fitness_Discipline;
			var isCycling = discipline is FitnessDiscipline.Cycling or FitnessDiscipline.Bike_Bootcamp;
			var isRunning = discipline is FitnessDiscipline.Running or FitnessDiscipline.Circuit; // Circuit = Tread Bootcamp

			// Total work (kJ) — accumulate across workouts
			if (w.Workout.Total_Work > 0)
				totalWork += w.Workout.Total_Work / 1000.0;

			// Power (cycling only)
			var outputMetric = GetMetric("output", w.WorkoutSamples);
			if (outputMetric?.Average_Value is not null)
				avgPower = avgPower is null ? outputMetric.Average_Value : (avgPower * (workoutCount - 1) + outputMetric.Average_Value) / workoutCount;
			if (outputMetric?.Max_Value is not null && (maxPower is null || outputMetric.Max_Value > maxPower))
				maxPower = outputMetric.Max_Value;

			// Cadence — bike vs run
			var cadenceMetric = GetMetric("cadence", w.WorkoutSamples);
			if (cadenceMetric?.Average_Value is not null)
			{
				if (isCycling)
					avgBikeCadence = avgBikeCadence is null ? cadenceMetric.Average_Value : (avgBikeCadence * (workoutCount - 1) + cadenceMetric.Average_Value) / workoutCount;
				else if (isRunning)
					avgRunCadence = avgRunCadence is null ? cadenceMetric.Average_Value : (avgRunCadence * (workoutCount - 1) + cadenceMetric.Average_Value) / workoutCount;
			}
			if (cadenceMetric?.Max_Value is not null)
			{
				if (isCycling && (maxBikeCadence is null || cadenceMetric.Max_Value > maxBikeCadence)) maxBikeCadence = cadenceMetric.Max_Value;
				if (isRunning && (maxRunCadence is null || cadenceMetric.Max_Value > maxRunCadence)) maxRunCadence = cadenceMetric.Max_Value;
			}

			// Speed — convert kph to m/s
			var speedMetric = GetMetric("speed", w.WorkoutSamples) ?? GetMetric("split_pace", w.WorkoutSamples);
			if (speedMetric?.Average_Value is not null)
			{
				var avgMs = speedMetric.Average_Value.Value / 3.6;
				avgSpeed = avgSpeed is null ? avgMs : (avgSpeed * (workoutCount - 1) + avgMs) / workoutCount;
			}
			if (speedMetric?.Max_Value is not null)
			{
				var maxMs = speedMetric.Max_Value.Value / 3.6;
				if (maxSpeed is null || maxMs > maxSpeed) maxSpeed = maxMs;
			}

			// Distance — only for indoor cycling (Garmin watch has no source for this)
			if (isCycling)
			{
				var distSummary = w.WorkoutSamples?.Summaries?.FirstOrDefault(s => s.Slug == "distance");
				if (distSummary?.Value is not null)
				{
					var unit = distSummary.Display_Unit?.ToLower() ?? "";
					indoorCyclingDistanceMeters += unit.Contains("mile") ? distSummary.Value.Value * 1609.344 : distSummary.Value.Value * 1000;
				}
			}
		}

		if (totalWork > 0) request.TotalWork = Math.Round(totalWork, 1);
		if (avgPower is not null) request.AvgPower = Math.Round(avgPower.Value, 1);
		if (maxPower is not null) request.MaxPower = Math.Round(maxPower.Value, 1);
		if (avgBikeCadence is not null) request.AvgBikeCadence = Math.Round(avgBikeCadence.Value, 1);
		if (maxBikeCadence is not null) request.MaxBikeCadence = Math.Round(maxBikeCadence.Value, 1);
		if (avgRunCadence is not null) request.AvgRunCadence = Math.Round(avgRunCadence.Value, 1);
		if (maxRunCadence is not null) request.MaxRunCadence = Math.Round(maxRunCadence.Value, 1);
		if (avgSpeed is not null) request.AvgSpeed = Math.Round(avgSpeed.Value, 3);
		if (maxSpeed is not null) request.MaxSpeed = Math.Round(maxSpeed.Value, 3);
		if (indoorCyclingDistanceMeters > 0) request.Distance = Math.Round(indoorCyclingDistanceMeters, 1);
	}

	private static Metric GetMetric(string slug, WorkoutSamples workoutSamples)
	{
		if (workoutSamples?.Metrics is null)
			return null;

		var metric = workoutSamples.Metrics.FirstOrDefault(m => m.Slug == slug);

		if (metric is null)
		{
			foreach (var m in workoutSamples.Metrics)
			{
				metric = m.Alternatives?.FirstOrDefault(a => a.Slug == slug);
				if (metric is not null)
					break;
			}
		}

		return metric;
	}

	// ─── FIT backup ──────────────────────────────────────────────────────────

	/// <summary>Returns the directory where original watch FIT backups are stored.</summary>
	public static string GetFitBackupDirectory()
	{
		return Path.GetFullPath(Path.Join(Common.Stateful.Statics.DefaultOutputDirectory, "fit-backups"));
	}

	/// <summary>
	/// Lists backed-up FIT files, newest first.
	/// Each entry is (filename, fullPath, sizeBytes, createdUtc).
	/// </summary>
	public static IReadOnlyList<(string FileName, string FullPath, long SizeBytes, DateTime CreatedUtc)> ListFitBackups()
	{
		var dir = GetFitBackupDirectory();
		if (!Directory.Exists(dir))
			return Array.Empty<(string, string, long, DateTime)>();

		return Directory.EnumerateFiles(dir, "*.fit")
			.Select(p =>
			{
				var info = new FileInfo(p);
				return (info.Name, p, info.Length, info.CreationTimeUtc);
			})
			.OrderByDescending(f => f.CreationTimeUtc)
			.ToList();
	}

	private async Task SaveOriginalFitBackupAsync(byte[] fitBytes, long garminActivityId, DateTime workoutStartUtc, string title)
	{
		try
		{
			var dir = GetFitBackupDirectory();
			Directory.CreateDirectory(dir);

			// Sanitize title for use in filename
			var safeTitle = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
			if (safeTitle.Length > 60) safeTitle = safeTitle[..60];

			var filename = $"{workoutStartUtc:yyyy-MM-dd}_{garminActivityId}_{safeTitle}.fit";
			var fullPath = Path.Join(dir, filename);

			if (File.Exists(fullPath) && new FileInfo(fullPath).Length > 0)
			{
				_logger.Debug("FIT merge: backup already exists for activity {ActivityId}, skipping", garminActivityId);
				return;
			}

			await File.WriteAllBytesAsync(fullPath, fitBytes);
			_logger.Information("FIT merge: saved original watch FIT backup to {Path} ({Bytes} bytes)", fullPath, fitBytes.Length);
		}
		catch (Exception e)
		{
			_logger.Warning(e, "FIT merge: could not save original FIT backup — continuing with merge anyway.");
		}
	}
}
