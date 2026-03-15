using System;
using System.Text.Json.Serialization;

namespace Garmin.Dto;

public class GarminActivitySummary
{
	public long ActivityId { get; set; }
	public string ActivityName { get; set; }
	public string StartTimeLocal { get; set; }
	public string StartTimeGMT { get; set; }
	public GarminActivityType ActivityType { get; set; }
	public double? Duration { get; set; }
}

public class GarminActivityType
{
	public string TypeKey { get; set; }
}

public class GarminActivityUpdateRequest
{
	public long ActivityId { get; set; }
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string ActivityName { get; set; }
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string Description { get; set; }
}

public class GarminMergeRecord
{
	public string PelotonWorkoutId { get; set; }
	public string PelotonWorkoutTitle { get; set; }
	public long GarminActivityId { get; set; }
	public DateTime MergedAt { get; set; }
	public MergeSource Source { get; set; }
}

public enum MergeSource { Auto, Manual }

public class GarminEnrichmentResult
{
	public string PelotonWorkoutId { get; set; }
	public string PelotonWorkoutTitle { get; set; }
	public long GarminActivityId { get; set; }
	public string GarminActivityName { get; set; }
	public DateTime? GarminActivityStartTimeUtc { get; set; }
	public double MatchDeltaSeconds { get; set; }
	public bool HasMatch { get; set; }
}
