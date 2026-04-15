using System;
using System.Collections.Generic;

namespace Api.Contract;

public record TrainingStateGetResponse
{
	/// <summary>Chronic Training Load — 42-day EMA of daily TSS. Your "fitness" score.</summary>
	public double CTL { get; init; }

	/// <summary>Acute Training Load — 7-day EMA of daily TSS. Your "fatigue" score.</summary>
	public double ATL { get; init; }

	/// <summary>Training Stress Balance = CTL - ATL. Your "form" score. Positive = fresh, negative = tired.</summary>
	public double TSB { get; init; }

	public WorkoutRecommendationDto Recommendation { get; init; } = new();

	/// <summary>Per-day TSS for the last 14 days, newest first.</summary>
	public ICollection<DailyLoadDto> RecentLoad { get; init; } = new List<DailyLoadDto>();

	/// <summary>Peloton classes recommended based on today's training load — filtered to ones not taken in the last 60 days.</summary>
	public ICollection<SuggestedClassDto> SuggestedClasses { get; init; } = new List<SuggestedClassDto>();
}

public record WorkoutRecommendationDto
{
	public IntensityLevelDto Intensity { get; init; }
	public string Reason { get; init; } = string.Empty;
	public string SuggestedDiscipline { get; init; } = string.Empty;
	public int SuggestedDurationMinutes { get; init; }
	public string WorkoutCue { get; init; } = string.Empty;
}

public enum IntensityLevelDto
{
	Rest = 0,
	Recovery = 1,
	Easy = 2,
	Moderate = 3,
	Hard = 4,
	VeryHard = 5,
}

public record DailyLoadDto
{
	public DateTime Date { get; init; }
	public double TSS { get; init; }
	public string Discipline { get; init; } = string.Empty;
}

public record SuggestedClassDto
{
	public string Id { get; init; } = string.Empty;
	public string Title { get; init; } = string.Empty;
	public string Instructor { get; init; } = string.Empty;
	public int DurationMinutes { get; init; }
	public double DifficultyScore { get; init; }
	/// <summary>Estimated calorie burn from Peloton catalog metadata (kcal). 0 when not available.</summary>
	public int EstimatedCalories { get; init; }
	public string ImageUrl { get; init; } = string.Empty;
	public string Discipline { get; init; } = string.Empty;
	public string PelotonUrl { get; init; } = string.Empty;
}
