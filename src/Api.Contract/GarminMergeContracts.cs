using System;
using System.Collections.Generic;

namespace Api.Contract;

public enum MergeSourceDto { Auto, Manual }

public record GarminMergeGetResponse
{
	public GarminMergeGetResponse()
	{
		Records = new List<GarminMergeRecordDto>();
	}

	public ICollection<GarminMergeRecordDto> Records { get; init; }
}

public record GarminMergeRecordDto
{
	public string? PelotonWorkoutId { get; init; }
	public string? PelotonWorkoutTitle { get; init; }
	public long GarminActivityId { get; init; }
	public DateTime MergedAt { get; init; }
	public MergeSourceDto Source { get; init; }
}

public record GarminMergePostRequest
{
	public string? WorkoutId { get; init; }
}

public record GarminMergePostResponse
{
	public bool Success { get; init; }
	public string? Message { get; init; }
	public long? GarminActivityId { get; init; }
}

public record GarminMergePreviewRequest
{
	public ICollection<string> WorkoutIds { get; init; } = new List<string>();
}

public record GarminMergePreviewItem
{
	public string? PelotonWorkoutId { get; init; }
	public string? PelotonWorkoutTitle { get; init; }
	public long? GarminActivityId { get; init; }
	public string? GarminActivityName { get; init; }
	public DateTime? GarminActivityStartTimeUtc { get; init; }
	public double? MatchDeltaSeconds { get; init; }
	public bool HasMatch { get; init; }
}

public record GarminMergePreviewResponse
{
	public ICollection<GarminMergePreviewItem> Items { get; init; } = new List<GarminMergePreviewItem>();
}

public record FitBackupListResponse
{
	public ICollection<FitBackupItem> Items { get; init; } = new List<FitBackupItem>();
}

public record FitBackupItem
{
	public string FileName { get; init; } = string.Empty;
	public long SizeBytes { get; init; }
	public DateTime CreatedUtc { get; init; }
}
