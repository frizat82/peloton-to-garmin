using Api.Contract;
using Common.Dto;
using Common.Dto.Peloton;
using Garmin;
using Garmin.Auth;
using Garmin.Dto;
using Peloton;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Sync;

public class TrainingAnalysisService : ITrainingAnalysisService
{
	private static readonly ILogger _logger = Log.ForContext<TrainingAnalysisService>();

	// EMA decay constants: alpha = 2 / (window + 1)
	private const double CtlAlpha = 2.0 / (42 + 1); // 42-day fitness
	private const double AtlAlpha = 2.0 / (7 + 1);  // 7-day fatigue

	// TSS-per-hour estimates for non-power disciplines
	private static readonly Dictionary<FitnessDiscipline, double> DisciplineIntensity = new()
	{
		[FitnessDiscipline.Circuit] = 65,  // Tread Bootcamp — hard by nature
		[FitnessDiscipline.Running] = 55,
		[FitnessDiscipline.Walking] = 30,
		[FitnessDiscipline.Cardio] = 50,
		[FitnessDiscipline.Bike_Bootcamp] = 60,
		[FitnessDiscipline.Strength] = 40,
		[FitnessDiscipline.Stretching] = 10,
		[FitnessDiscipline.Yoga] = 15,
		[FitnessDiscipline.Meditation] = 5,
		[FitnessDiscipline.Caesar] = 55,  // Rowing
		[FitnessDiscipline.Caesar_Bootcamp] = 60,
		[FitnessDiscipline.Cycling] = 55,  // fallback if no FTP
	};

	private readonly IPelotonService _pelotonService;
	private readonly IGarminApiClient _garminApiClient;
	private readonly IGarminAuthenticationService _garminAuthService;

	public TrainingAnalysisService(IPelotonService pelotonService, IGarminApiClient garminApiClient, IGarminAuthenticationService garminAuthService)
	{
		_pelotonService = pelotonService;
		_garminApiClient = garminApiClient;
		_garminAuthService = garminAuthService;
	}

	public async Task<TrainingStateGetResponse> GetTrainingStateAsync()
	{
		// Fetch 180 days: 60 for EMA warmup + 120 more for a rich class suggestion pool
		var since = DateTime.UtcNow.AddDays(-180);
		UserData? userData = null;

		try { userData = await _pelotonService.GetUserDataAsync(); }
		catch (Exception ex) { _logger.Warning(ex, "Could not fetch Peloton user data; FTP-based TSS unavailable."); }

		ServiceResult<ICollection<Workout>>? serviceResult = null;
		try { serviceResult = await _pelotonService.GetWorkoutsSinceAsync(since); }
		catch (Exception ex)
		{
			_logger.Error(ex, "Failed to fetch Peloton workouts for training analysis.");
			return EmptyState();
		}

		if (serviceResult is null || !serviceResult.Successful || serviceResult.Result is null)
			return EmptyState();

		var completedWorkouts = serviceResult.Result
			.Where(w => w.Status == "COMPLETE" && w.End_Time.HasValue)
			.OrderBy(w => w.Start_Time)
			.ToList();

		if (completedWorkouts.Count == 0)
			return EmptyState();

		// Fetch Garmin HR data for the same window (single call) to enable hrTSS for non-cycling workouts
		ICollection<GarminActivitySummary> garminActivities = Array.Empty<GarminActivitySummary>();
		try
		{
			var auth = await _garminAuthService.GetGarminAuthenticationAsync();
			if (auth is not null && auth.AuthStage == Garmin.Dto.AuthStage.Completed)
			{
				garminActivities = await _garminApiClient.SearchActivitiesAsync(since, DateTime.UtcNow, auth);
				_logger.Information("Training: fetched {Count} Garmin activities for hrTSS ({WithHr} have avg HR)",
					garminActivities.Count,
					garminActivities.Count(a => a.AverageHR > 0));
			}
			else
			{
				_logger.Information("Training: Garmin not authenticated (stage={Stage}); using discipline-based TSS estimates.",
					auth?.AuthStage.ToString() ?? "null");
			}
		}
		catch (Exception ex) { _logger.Warning(ex, "Could not fetch Garmin activities for hrTSS; falling back to discipline estimates."); }

		// Derive user max HR using 90th-percentile of activity MaxHR values.
		// Raw max is unreliable — one optical-HR artifact spike (e.g. 228 bpm) would inflate
		// the denominator and deflate every hrTSS score. 90th-pct filters outliers while
		// still capturing genuine peak effort.
		var maxHrValues = garminActivities
			.Where(a => a.MaxHR > 0)
			.Select(a => a.MaxHR!.Value)
			.OrderBy(v => v)
			.ToList();
		double userMaxHR = 0;
		if (maxHrValues.Count > 0)
		{
			var p90Index = (int)Math.Ceiling(maxHrValues.Count * 0.90) - 1;
			userMaxHR = maxHrValues[Math.Clamp(p90Index, 0, maxHrValues.Count - 1)];
		}
		_logger.Information("Training: userMaxHR={MaxHR} (90th-pct of {Count} Garmin activities with HR data; raw max was {RawMax})",
			userMaxHR, maxHrValues.Count, maxHrValues.Count > 0 ? maxHrValues.Last() : 0);

		// Build map of date → total TSS for that day
		var dailyTss = BuildDailyTss(completedWorkouts, userData, garminActivities, userMaxHR);

		// Run EMA from 60 days ago through today, one day at a time
		var today = DateTime.UtcNow.Date;
		var startDate = today.AddDays(-60);

		double ctl = 0, atl = 0;
		for (var d = startDate; d <= today; d = d.AddDays(1))
		{
			var tss = dailyTss.TryGetValue(d, out var t) ? t : 0;
			ctl = ctl + CtlAlpha * (tss - ctl);
			atl = atl + AtlAlpha * (tss - atl);
		}

		var tsb = ctl - atl;

		// Recent 14-day detail for the chart
		var recentLoad = Enumerable.Range(0, 14)
			.Select(i => today.AddDays(-13 + i))
			.Select(d =>
			{
				var dayWorkouts = completedWorkouts
					.Where(w => DateTimeOffset.FromUnixTimeSeconds(w.Start_Time).UtcDateTime.Date == d)
					.ToList();

				return new DailyLoadDto
				{
					Date = d,
					TSS = dailyTss.TryGetValue(d, out var t) ? Math.Round(t, 1) : 0,
					Discipline = dayWorkouts.Count > 0
						? string.Join(", ", dayWorkouts.Select(w => FriendlyDiscipline(w.Fitness_Discipline)).Distinct())
						: string.Empty,
				};
			})
			.ToList();

		var recommendation = BuildRecommendation(ctl, atl, tsb, completedWorkouts);

		var cutoff60 = DateTime.UtcNow.AddDays(-60);

		// Ride IDs done in the last 60 days — excluded from suggestions
		var recentRideIds = completedWorkouts
			.Where(w => w.Ride?.Id is not null
				&& DateTimeOffset.FromUnixTimeSeconds(w.Start_Time).UtcDateTime >= cutoff60)
			.Select(w => w.Ride!.Id)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var suggestedClasses = await GetSuggestedClassesAsync(recommendation.Intensity, recentRideIds, completedWorkouts);

		return new TrainingStateGetResponse
		{
			CTL = Math.Round(ctl, 1),
			ATL = Math.Round(atl, 1),
			TSB = Math.Round(tsb, 1),
			Recommendation = recommendation,
			RecentLoad = recentLoad,
			SuggestedClasses = suggestedClasses,
		};
	}

	// ─── TSS calculation ─────────────────────────────────────────────────────

	private static Dictionary<DateTime, double> BuildDailyTss(
		IEnumerable<Workout> workouts, UserData? userData,
		ICollection<GarminActivitySummary> garminActivities, double userMaxHR)
	{
		var daily = new Dictionary<DateTime, double>();

		foreach (var w in workouts)
		{
			var date = DateTimeOffset.FromUnixTimeSeconds(w.Start_Time).UtcDateTime.Date;
			var tss = ComputeTss(w, userData, garminActivities, userMaxHR);
			daily[date] = daily.TryGetValue(date, out var existing) ? existing + tss : tss;
		}

		return daily;
	}

	private static double ComputeTss(Workout w, UserData? userData,
		ICollection<GarminActivitySummary> garminActivities, double userMaxHR)
	{
		var durationSec = w.End_Time!.Value - w.Start_Time;
		if (durationSec <= 0) return 0;

		// Power-based TSS for cycling when we have FTP + output
		if (w.Fitness_Discipline is FitnessDiscipline.Cycling or FitnessDiscipline.Bike_Bootcamp
			&& w.Total_Work > 0)
		{
			var ftp = GetFtp(userData);
			if (ftp > 0)
			{
				var avgPowerWatts = w.Total_Work / durationSec;
				var intensityFactor = avgPowerWatts / ftp;
				var tss = durationSec * avgPowerWatts * intensityFactor / (ftp * 3600.0) * 100.0;
				return Math.Min(tss, 400);
			}
		}

		// HR-based TSS (hrTSS) using Garmin watch data when available
		// hrTSS = duration(h) × (avgHR / maxHR)² × 100
		if (userMaxHR > 0 && garminActivities.Count > 0)
		{
			var workoutStart = DateTimeOffset.FromUnixTimeSeconds(w.Start_Time).UtcDateTime;

			GarminActivitySummary? match = null;
			double bestDelta = double.MaxValue;
			foreach (var a in garminActivities)
			{
				if (!DateTime.TryParse(a.StartTimeGMT, System.Globalization.CultureInfo.InvariantCulture,
					System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
					out var garminStart)) continue;
				var delta = Math.Abs((garminStart - workoutStart).TotalMinutes);
				if (delta <= 15 && delta < bestDelta)
				{
					bestDelta = delta;
					match = a;
				}
			}

			if (match is not null)
			{
				// Garmin's activity list API omits averageHR for multi-sport types (Tread Bootcamp).
				// Fall back to maxHR × 0.82 — typical avgHR/maxHR ratio for a hard-effort session.
				double? effectiveAvgHR = match.AverageHR > 0 ? match.AverageHR
					: match.MaxHR > 0 ? match.MaxHR!.Value * 0.82
					: null;

				if (effectiveAvgHR is not null)
				{
					var hrSource = match.AverageHR > 0 ? "avgHR" : $"maxHR({match.MaxHR})×0.82";
					var hrRatio = effectiveAvgHR.Value / userMaxHR;
					var hrTss = (durationSec / 3600.0) * hrRatio * hrRatio * 100.0;
					_logger.Information("hrTSS: {Discipline} on {Date} → Garmin '{Name}' delta={Delta:F1}min {HrSource}={EffHR:F0} maxHR={Max} tss={TSS:F1}",
						w.Fitness_Discipline, workoutStart.Date.ToString("MM-dd"), match.ActivityName, bestDelta, hrSource, effectiveAvgHR, userMaxHR, hrTss);
					return Math.Min(hrTss, 400);
				}
			}

			_logger.Information("hrTSS miss: {Discipline} on {Date:MM-dd} @ {Start:HH:mm} UTC — no Garmin match within 15 min (have {Count} candidates, closest={ClosestDelta:F1}min)",
				w.Fitness_Discipline, workoutStart, workoutStart,
				garminActivities.Count,
				garminActivities
					.Where(a => DateTime.TryParse(a.StartTimeGMT, System.Globalization.CultureInfo.InvariantCulture,
						System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out _))
					.Select(a =>
					{
						DateTime.TryParse(a.StartTimeGMT, System.Globalization.CultureInfo.InvariantCulture,
							System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t);
						return Math.Abs((t - workoutStart).TotalMinutes);
					})
					.DefaultIfEmpty(double.MaxValue).Min());
		}

		// Fallback: duration-based estimate by discipline
		var intensityPerHour = DisciplineIntensity.TryGetValue(w.Fitness_Discipline, out var rate) ? rate : 45;
		return (durationSec / 3600.0) * intensityPerHour;
	}

	private static int GetFtp(UserData? userData)
	{
		if (userData is null) return 0;
		if (userData.Cycling_Workout_Ftp > 0) return userData.Cycling_Workout_Ftp;
		if (userData.Cycling_Ftp > 0) return userData.Cycling_Ftp;
		if (userData.Estimated_Cycling_Ftp > 0) return userData.Estimated_Cycling_Ftp;
		return 0;
	}

	// ─── Recommendation engine ───────────────────────────────────────────────

	private static WorkoutRecommendationDto BuildRecommendation(
		double ctl, double atl, double tsb,
		IReadOnlyList<Workout> allWorkouts)
	{
		var today = DateTime.UtcNow.Date;

		// Days since last workout
		var lastWorkout = allWorkouts
			.Where(w => DateTimeOffset.FromUnixTimeSeconds(w.Start_Time).UtcDateTime.Date < today)
			.OrderByDescending(w => w.Start_Time)
			.FirstOrDefault();
		var daysSinceLast = lastWorkout is null ? 99
			: (today - DateTimeOffset.FromUnixTimeSeconds(lastWorkout.Start_Time).UtcDateTime.Date).Days;

		// How many of last 2 days were hard efforts
		var last2DaysHard = allWorkouts
			.Where(w =>
			{
				var d = DateTimeOffset.FromUnixTimeSeconds(w.Start_Time).UtcDateTime.Date;
				return d >= today.AddDays(-2) && d < today
					&& w.Fitness_Discipline is FitnessDiscipline.Circuit
						or FitnessDiscipline.Running
						or FitnessDiscipline.Cycling
						or FitnessDiscipline.Bike_Bootcamp;
			})
			.Count();

		// Override: mandatory rest/recovery signals
		if (tsb < -30)
			return MakeRecommendation(IntensityLevelDto.Rest, tsb, daysSinceLast,
				"TSB is very low — your body is overreached. Take a full rest day.",
				"Rest", 0, "No workout today. Sleep and nutrition are your training right now.");

		if (tsb < -15 || last2DaysHard >= 2)
		{
			var reason = last2DaysHard >= 2
				? "2+ hard sessions back-to-back — recovery prevents injury."
				: $"TSB {tsb:F0}: accumulated fatigue is high.";
			return MakeRecommendation(IntensityLevelDto.Recovery, tsb, daysSinceLast,
				reason,
				"Cycling or Stretching", 20,
				"Easy Zone 1-2 spin or stretching. Keep HR under 130. No intervals.");
		}

		// Fresh after a gap — can handle hard
		if (daysSinceLast >= 3 && tsb >= 0)
			return MakeRecommendation(IntensityLevelDto.Hard, tsb, daysSinceLast,
				$"{daysSinceLast} days off + positive form (TSB {tsb:+0;-0}) — you're fresh.",
				"Circuit (Tread Bootcamp)", 60,
				"60 min tread bootcamp. Push Z4/Z5 in the run intervals. Own it.");

		// Form-based intensity
		if (tsb >= 10)
			return MakeRecommendation(IntensityLevelDto.VeryHard, tsb, daysSinceLast,
				$"TSB {tsb:+0;-0}: you're very fresh — peak performance window.",
				"Circuit (Tread Bootcamp)", 60,
				"60 min tread bootcamp. This is a PR day. Go Z4/Z5 hard throughout.");

		if (tsb >= 0)
			return MakeRecommendation(IntensityLevelDto.Hard, tsb, daysSinceLast,
				$"TSB {tsb:+0;-0}: good form, fitness is building.",
				"Circuit (Tread Bootcamp)", 60,
				"60 min tread bootcamp. Strong effort — target Z3/Z4 with Z5 pushes in intervals.");

		if (tsb >= -10)
			return MakeRecommendation(IntensityLevelDto.Moderate, tsb, daysSinceLast,
				$"TSB {tsb:+0;-0}: mild fatigue accumulating — back off just a touch.",
				"Cycling or Circuit", 45,
				"45-60 min moderate effort. Z2/Z3. Not a junk miles day — stay controlled.");

		// tsb < -10
		return MakeRecommendation(IntensityLevelDto.Easy, tsb, daysSinceLast,
			$"TSB {tsb:+0;-0}: fatigue building — protect your fitness gains.",
			"Cycling or Stretching", 30,
			"Easy 30 min Z1/Z2 ride or stretching. Let adaptation happen.");
	}

	private static WorkoutRecommendationDto MakeRecommendation(
		IntensityLevelDto intensity, double tsb, int daysSinceLast,
		string reason, string discipline, int durationMin, string cue)
	{
		return new WorkoutRecommendationDto
		{
			Intensity = intensity,
			Reason = reason,
			SuggestedDiscipline = discipline,
			SuggestedDurationMinutes = durationMin,
			WorkoutCue = cue,
		};
	}

	// ─── Class suggestions ────────────────────────────────────────────────────

	private Task<ICollection<SuggestedClassDto>> GetSuggestedClassesAsync(
		IntensityLevelDto intensity, HashSet<string> recentRideIds, IReadOnlyList<Workout> allWorkouts)
	{
		// Map intensity → target discipline slug (matches Ride.Fitness_Discipline string), duration band, difficulty range.
		// Duration bands are generous — the key discriminator is difficulty.
		var (targetDisciplineSlug, targetDiscipline, minDurationSec, maxDurationSec, minDifficulty, maxDifficulty) = intensity switch
		{
			IntensityLevelDto.VeryHard => ("circuit", FitnessDiscipline.Circuit, 1800, 5400, 7.5, 10.0),
			IntensityLevelDto.Hard => ("circuit", FitnessDiscipline.Circuit, 1800, 5400, 6.0, 9.5),
			IntensityLevelDto.Moderate => ("cycling", FitnessDiscipline.Cycling, 1200, 4200, 4.0, 8.5),
			IntensityLevelDto.Easy => ("cycling", FitnessDiscipline.Cycling, 900, 3600, 2.0, 6.5),
			IntensityLevelDto.Recovery => ("cycling", FitnessDiscipline.Cycling, 900, 3600, 0.0, 5.5),
			IntensityLevelDto.Rest => ("stretching", FitnessDiscipline.Stretching, 300, 3600, 0.0, 10.0),
			_ => ("circuit", FitnessDiscipline.Circuit, 1800, 5400, 5.0, 9.0),
		};

		// Build a de-duplicated pool of unique rides from the 180-day workout history,
		// excluding classes done in the last 60 days so the suggestion feels fresh.
		var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var candidateRides = new List<Ride>();

		foreach (var w in allWorkouts.OrderByDescending(w => w.Start_Time))
		{
			var ride = w.Ride;
			if (ride?.Id is null) continue;
			if (recentRideIds.Contains(ride.Id)) continue;
			if (!seenIds.Add(ride.Id)) continue;
			// Archived = retired from Peloton's browse catalog but content still accessible; include it.
			if (!string.Equals(ride.Fitness_Discipline, targetDisciplineSlug, StringComparison.OrdinalIgnoreCase)) continue;
			if ((ride.Duration ?? 0) < minDurationSec || (ride.Duration ?? 0) > maxDurationSec) continue;
			if (ride.Difficulty_Estimate < minDifficulty || ride.Difficulty_Estimate > maxDifficulty) continue;
			candidateRides.Add(ride);
		}

		_logger.Information("Suggestions: intensity={Intensity} discipline={Discipline} dur=[{Min},{Max}]s diff=[{DMin:F1},{DMax:F1}] — {Pool} candidates from 180-day history",
					intensity, targetDisciplineSlug, minDurationSec, maxDurationSec, minDifficulty, maxDifficulty, candidateRides.Count);

		ICollection<SuggestedClassDto> result = candidateRides
			.OrderByDescending(r => r.Overall_Estimate)
			.Take(6)
			.Select(r => new SuggestedClassDto
			{
				Id = r.Id,
				Title = r.Title ?? string.Empty,
				Instructor = r.Instructor?.Name ?? string.Empty,
				DurationMinutes = (r.Duration ?? minDurationSec) / 60,
				DifficultyScore = Math.Round(r.Difficulty_Estimate, 1),
				EstimatedCalories = (int)(r.Estimated_Calories_Output ?? 0),
				ImageUrl = r.Image_Url?.ToString() ?? string.Empty,
				Discipline = r.Fitness_Discipline_Display_Name ?? FriendlyDiscipline(targetDiscipline),
				PelotonUrl = PelotonClassUrl(targetDiscipline, r.Id),
			})
			.ToList();

		return Task.FromResult(result);
	}

	// ─── Helpers ─────────────────────────────────────────────────────────────

	private static string FriendlyDiscipline(FitnessDiscipline d) => d switch
	{
		FitnessDiscipline.Circuit => "Tread Bootcamp",
		FitnessDiscipline.Running => "Tread",
		FitnessDiscipline.Walking => "Walk",
		FitnessDiscipline.Cycling => "Ride",
		FitnessDiscipline.Bike_Bootcamp => "Bike Bootcamp",
		FitnessDiscipline.Strength => "Strength",
		FitnessDiscipline.Stretching => "Stretch",
		FitnessDiscipline.Yoga => "Yoga",
		FitnessDiscipline.Cardio => "Cardio",
		FitnessDiscipline.Caesar => "Row",
		FitnessDiscipline.Caesar_Bootcamp => "Row Bootcamp",
		FitnessDiscipline.Meditation => "Meditation",
		_ => d.ToString(),
	};

	private static string PelotonClassUrl(FitnessDiscipline discipline, string rideId)
	{
		var slug = discipline switch
		{
			FitnessDiscipline.Cycling => "cycling",
			FitnessDiscipline.Bike_Bootcamp => "cycling",
			FitnessDiscipline.Circuit => "running",
			FitnessDiscipline.Running => "running",
			FitnessDiscipline.Walking => "running",
			FitnessDiscipline.Strength => "strength",
			FitnessDiscipline.Stretching => "stretching",
			FitnessDiscipline.Yoga => "yoga",
			FitnessDiscipline.Meditation => "meditation",
			FitnessDiscipline.Caesar => "rowing",
			FitnessDiscipline.Caesar_Bootcamp => "rowing",
			FitnessDiscipline.Cardio => "cardio",
			_ => "classes",
		};
		return $"https://members.onepeloton.com/classes/{slug}?modal=classDetailsModal&classId={rideId}";
	}

	private static TrainingStateGetResponse EmptyState() => new TrainingStateGetResponse
	{
		CTL = 0,
		ATL = 0,
		TSB = 0,
		Recommendation = new WorkoutRecommendationDto
		{
			Intensity = IntensityLevelDto.Moderate,
			Reason = "Not enough workout data to analyze. Complete a few workouts and check back.",
			SuggestedDiscipline = "Circuit (Tread Bootcamp)",
			SuggestedDurationMinutes = 60,
			WorkoutCue = "Start with a 60 min tread bootcamp and sync it — we'll have a real recommendation next time.",
		},
	};
}
