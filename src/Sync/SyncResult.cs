using Common.Dto;
using Garmin.Dto;
using System.Collections.Generic;

namespace Sync;

public class SyncResult
{
	public SyncResult()
	{
		Errors = new List<ServiceError>();
		MergeResults = new List<GarminEnrichmentResult>();
	}

	public bool SyncSuccess { get; set; }
	public bool PelotonDownloadSuccess { get; set; }
	public bool? ConversionSuccess { get; set; }
	public bool? UploadToGarminSuccess { get; set; }
	public ICollection<ServiceError> Errors { get; set; }

	/// <summary>
	/// Peloton workouts that were matched to and enriched into an existing Garmin device activity.
	/// </summary>
	public ICollection<GarminEnrichmentResult> MergeResults { get; set; }
}
