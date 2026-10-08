using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Garmin.Dto;

public class GarminActivitySummary
{
	[JsonPropertyName("activityId")]
	public long ActivityId { get; set; }
	[JsonPropertyName("activityName")]
	public string ActivityName { get; set; }
	[JsonPropertyName("startTimeLocal")]
	public string StartTimeLocal { get; set; }
	[JsonPropertyName("startTimeGMT")]
	public string StartTimeGMT { get; set; }
	[JsonPropertyName("activityType")]
	public GarminActivityType ActivityType { get; set; }
	[JsonPropertyName("duration")]
	public double? Duration { get; set; }
	[JsonPropertyName("averageHR")]
	public double? AverageHR { get; set; }
	[JsonPropertyName("maxHR")]
	public double? MaxHR { get; set; }
}

public class GarminActivityType
{
	[JsonPropertyName("typeKey")]
	public string TypeKey { get; set; }
}

public class GarminActivityUpdateRequest
{
	[JsonPropertyName("activityId")]
	public long ActivityId { get; set; }
	[JsonPropertyName("activityName")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string ActivityName { get; set; }
	[JsonPropertyName("description")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string Description { get; set; }
	/// <summary>Distance in meters.</summary>
	[JsonPropertyName("distance")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? Distance { get; set; }
	/// <summary>Calories in kcal.</summary>
	[JsonPropertyName("calories")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? Calories { get; set; }
	/// <summary>Average power in watts.</summary>
	[JsonPropertyName("avgPower")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? AvgPower { get; set; }
	/// <summary>Max power in watts.</summary>
	[JsonPropertyName("maxPower")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? MaxPower { get; set; }
	/// <summary>Average bike cadence in rpm.</summary>
	[JsonPropertyName("avgBikeCadence")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? AvgBikeCadence { get; set; }
	/// <summary>Max bike cadence in rpm.</summary>
	[JsonPropertyName("maxBikeCadence")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? MaxBikeCadence { get; set; }
	/// <summary>Average speed in meters/second.</summary>
	[JsonPropertyName("avgSpeed")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? AvgSpeed { get; set; }
	/// <summary>Max speed in meters/second.</summary>
	[JsonPropertyName("maxSpeed")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? MaxSpeed { get; set; }
	/// <summary>Average heart rate in bpm.</summary>
	[JsonPropertyName("avgHr")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? AvgHr { get; set; }
	/// <summary>Max heart rate in bpm.</summary>
	[JsonPropertyName("maxHr")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? MaxHr { get; set; }
	/// <summary>Total work in kJ.</summary>
	[JsonPropertyName("totalWork")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? TotalWork { get; set; }
	/// <summary>Average running cadence in steps/min (one foot).</summary>
	[JsonPropertyName("avgRunCadence")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? AvgRunCadence { get; set; }
	/// <summary>Max running cadence in steps/min (one foot).</summary>
	[JsonPropertyName("maxRunCadence")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public double? MaxRunCadence { get; set; }
}

public class GarminMergeRecord
{
	public string PelotonWorkoutId { get; set; }
	public string PelotonWorkoutTitle { get; set; }
	public long GarminActivityId { get; set; }
	public DateTime MergedAt { get; set; }
	public MergeSource Source { get; set; }
	public MergeStatus Status { get; set; } = MergeStatus.Success;
	public string StatusDetail { get; set; }
}

/// <summary>
/// A merged FIT upload that still needs checking. Garmin occasionally keeps the activity but drops
/// its cadence and power, so the upload is checked later and re-sent if the data is missing.
/// </summary>
public class PendingMergeVerification
{
	public long OriginalGarminActivityId { get; set; }
	public string PelotonWorkoutId { get; set; }
	public DateTime WorkoutStartUtc { get; set; }
	public string ActivityName { get; set; }
	public string Description { get; set; }
	public string MergedFitPath { get; set; }
	/// <summary>The uploaded activity, or null while it is unknown or deleted for a re-upload.</summary>
	public long? GarminActivityId { get; set; }
	/// <summary>Activities near the workout start that existed before the latest upload.</summary>
	public List<long> PreExistingActivityIds { get; set; } = new();
	public DateTime UploadedAtUtc { get; set; }
	public DateTime CheckAfterUtc { get; set; }
	/// <summary>Set when a bad upload has been deleted and the merged FIT is waiting to be re-uploaded.</summary>
	public long? DeletedGarminActivityId { get; set; }
	public int Reuploads { get; set; }
}

public enum MergeSource { Auto, Manual }

public enum MergeStatus
{
	Success,
	OriginalRestored,
	RestoreFailed,
}

public class GarminEnrichmentResult
{
	public string PelotonWorkoutId { get; set; }
	public string PelotonWorkoutTitle { get; set; }
	public long GarminActivityId { get; set; }
	public string GarminActivityName { get; set; }
	public DateTime? GarminActivityStartTimeUtc { get; set; }
	public double MatchDeltaSeconds { get; set; }
	public bool HasMatch { get; set; }
	public MergeStatus MergeStatus { get; set; } = MergeStatus.Success;
	public string? StatusDetail { get; set; }
}
