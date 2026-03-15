using Common.Dto;
using Common.Dto.Peloton;
using Common.Observe;
using Common.Service;
using Garmin.Auth;
using Garmin.Database;
using Garmin.Dto;
using Serilog;
using System;
using System.Collections.Generic;
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
}

public class GarminActivityEnrichmentService : IGarminActivityEnrichmentService
{
	private static readonly ILogger _logger = LogContext.ForClass<GarminActivityEnrichmentService>();

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

		if (auth.AuthStage == AuthStage.NeedMfaToken || auth.AuthStage == AuthStage.None)
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

		if (auth.AuthStage == AuthStage.NeedMfaToken || auth.AuthStage == AuthStage.None)
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
					await ApplyGroupUpdateAsync(garminActivityId, group, auth);
				}
				catch (Exception e)
				{
					_logger.Error(e, "Failed to enrich Garmin activity {GarminActivityId}. {Message}", garminActivityId, e.Message);
				}
			}
		}

		return results;
	}

	private async Task ApplyGroupUpdateAsync(long garminActivityId, List<(P2GWorkout P2GWorkout, GarminEnrichmentResult Result)> group, GarminApiAuthentication auth)
	{
		// Use the workout with the most total output as the "primary" (name source).
		// For ties or missing data, first in list wins.
		var primary = group
			.OrderByDescending(g => g.P2GWorkout.Workout.Total_Work)
			.First();

		var workoutIds = string.Join(", ", group.Select(g => g.Result.PelotonWorkoutId));
		_logger.Information("Enriching Garmin activity {GarminActivityId} with {Count} Peloton workout(s): {WorkoutIds}.",
			garminActivityId, group.Count, workoutIds);

		var updateRequest = new GarminActivityUpdateRequest
		{
			ActivityId = garminActivityId,
			ActivityName = BuildActivityName(primary.P2GWorkout.Workout),
			Description = group.Count == 1
				? BuildDescription(primary.P2GWorkout.Workout, primary.P2GWorkout.WorkoutSamples)
				: BuildCombinedDescription(group.Select(g => g.P2GWorkout)),
		};

		await _apiClient.UpdateActivityAsync(garminActivityId, updateRequest, auth);

		_logger.Information("Successfully enriched Garmin activity {GarminActivityId}.", garminActivityId);

		foreach (var (p2gWorkout, result) in group)
		{
			await _mergeDb.SaveAsync(new GarminMergeRecord
			{
				PelotonWorkoutId = result.PelotonWorkoutId,
				PelotonWorkoutTitle = result.PelotonWorkoutTitle,
				GarminActivityId = garminActivityId,
				MergedAt = DateTime.UtcNow,
				Source = MergeSource.Auto,
			});
		}
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

			var isBetter = best.Activity is null
				|| (isSportMatch && !best.IsSportMatch)
				|| (isSportMatch == best.IsSportMatch && effectiveDelta < best.DeltaSeconds);

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
}
