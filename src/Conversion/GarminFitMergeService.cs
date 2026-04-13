using Common.Dto;
using Common.Dto.Peloton;
using Common.Observe;
using Dynastream.Fit;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Conversion;

/// <summary>
/// Merges Peloton workout metrics (power, cadence, speed, resistance) into a
/// Garmin watch-recorded FIT file. Watch data is authoritative — Peloton values
/// are only written into fields the watch left empty.
/// </summary>
public static class GarminFitMergeService
{
	private static readonly ILogger _logger = LogContext.ForStatic("GarminFitMergeService");

	/// <summary>
	/// Decodes the watch FIT bytes, injects Peloton metrics per second, and
	/// returns the merged FIT as a new byte array ready for upload.
	/// </summary>
	/// <param name="watchFitBytes">Raw FIT bytes downloaded from Garmin Connect.</param>
	/// <param name="pelotonSamples">Peloton workout samples (power, cadence, speed, resistance).</param>
	/// <param name="workoutStartUnix">Workout start time as Unix epoch seconds (from Workout.Start_Time).</param>
	public static byte[] MergeWatchFitWithPeloton(byte[] watchFitBytes, WorkoutSamples pelotonSamples, long workoutStartUnix)
	{
		using var tracing = Tracing.Trace($"{nameof(GarminFitMergeService)}.{nameof(MergeWatchFitWithPeloton)}");

		var allMessages = DecodeAllMessages(watchFitBytes);

		var pelotonSampleMap = BuildPelotonSampleMap(pelotonSamples, workoutStartUnix);

		var totalDistanceMeters = GetTotalDistanceMeters(pelotonSamples);
		var avgSpeedMps = GetAvgSpeedMetersPerSecond(pelotonSamples);

		var mergedMessages = InjectPelotonIntoRecords(allMessages, pelotonSampleMap, totalDistanceMeters, avgSpeedMps);

		return EncodeMessages(mergedMessages);
	}

	// ─── Decode ──────────────────────────────────────────────────────────────

	private static List<Mesg> DecodeAllMessages(byte[] fitBytes)
	{
		var messages = new List<Mesg>();

		using var stream = new MemoryStream(fitBytes);
		var decoder = new Decode();
		var broadcaster = new MesgBroadcaster();

		decoder.MesgEvent += broadcaster.OnMesg;
		decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;

		// Capture every message generically — preserves all watch data
		broadcaster.MesgEvent += (sender, e) => messages.Add(e.mesg);

		decoder.Read(stream);

		_logger.Information("Decoded {Count} messages from watch FIT", messages.Count);
		return messages;
	}

	// ─── Peloton sample map ───────────────────────────────────────────────────

	private record PelotonSample(float? SpeedMps, ushort? Power, byte? Cadence, byte? Resistance);

	/// <summary>
	/// Builds a map of Unix timestamp → Peloton sample values, aligned to the
	/// workout start time. Peloton samples are 1/sec via Seconds_Since_Pedaling_Start.
	/// </summary>
	private static Dictionary<uint, PelotonSample> BuildPelotonSampleMap(WorkoutSamples samples, long workoutStartUnix)
	{
		var map = new Dictionary<uint, PelotonSample>();

		if (samples?.Seconds_Since_Pedaling_Start is null || !samples.Seconds_Since_Pedaling_Start.Any())
			return map;

		var allMetrics = samples.Metrics ?? new List<Metric>();
		var outputMetrics    = allMetrics.FirstOrDefault(m => m.Slug == "output");
		var cadenceMetrics   = GetCadenceSummary(samples);
		var speedMetrics     = GetSpeedSummary(samples);
		var resistanceMetrics = allMetrics.FirstOrDefault(m => m.Slug == "resistance");

		var secondsList = samples.Seconds_Since_Pedaling_Start.ToList();

		for (int i = 0; i < secondsList.Count; i++)
		{
			var secondsOffset = secondsList[i];
			var timestamp = (uint)(workoutStartUnix + secondsOffset);

			float? speedMps = null;
			if (speedMetrics is not null && i < speedMetrics.Values.Length)
				speedMps = ConvertToMetersPerSecond(speedMetrics.GetValue(i), speedMetrics.Display_Unit);

			ushort? power = null;
			if (outputMetrics is not null && i < outputMetrics.Values.Length)
				power = (ushort?)outputMetrics.Values[i];

			byte? cadence = null;
			if (cadenceMetrics is not null && i < cadenceMetrics.Values.Length)
				cadence = (byte?)cadenceMetrics.Values[i];

			byte? resistance = null;
			if (resistanceMetrics is not null && i < resistanceMetrics.Values.Length)
			{
				var resistancePct = resistanceMetrics.Values[i] / 100.0 ?? 0;
				resistance = (byte)(254 * resistancePct);
			}

			map[timestamp] = new PelotonSample(speedMps, power, cadence, resistance);
		}

		_logger.Information("Built Peloton sample map with {Count} entries", map.Count);
		return map;
	}

	// ─── Merge ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Iterates all messages. For RecordMesg entries, injects Peloton values
	/// into fields the watch left null/zero. Also accumulates distance from speed
	/// when the watch has no GPS distance, and patches Session/Lap summaries.
	/// All other messages pass through unchanged.
	/// </summary>
	private static List<Mesg> InjectPelotonIntoRecords(
		List<Mesg> messages,
		Dictionary<uint, PelotonSample> pelotonMap,
		float pelotonTotalDistanceMeters,
		float pelotonAvgSpeedMps)
	{
		int enriched = 0;

		var result = new List<Mesg>(messages.Count);
		foreach (var mesg in messages)
		{
			if (mesg.Num != MesgNum.Record)
			{
				result.Add(mesg);
				continue;
			}

			var record = new RecordMesg(mesg);
			var tsField = record.GetTimestamp();
			if (tsField is null)
			{
				result.Add(record);
				continue;
			}

			// FIT timestamps are seconds since 1989-12-31 00:00:00 UTC (Garmin epoch)
			// Convert to Unix epoch for lookup
			var fitEpochOffsetSeconds = 631065600u;
			var unixTs = tsField.GetTimeStamp() + fitEpochOffsetSeconds;

			// Allow ±1 second tolerance
			PelotonSample? sample = null;
			if (pelotonMap.TryGetValue(unixTs, out var exact))
				sample = exact;
			else if (pelotonMap.TryGetValue(unixTs - 1, out var prev))
				sample = prev;
			else if (pelotonMap.TryGetValue(unixTs + 1, out var next))
				sample = next;

			if (sample is not null)
			{
				// Speed: watch value wins; Peloton fills if null/zero
				if (record.GetSpeed() is null or 0 && sample.SpeedMps is not null)
					record.SetSpeed(sample.SpeedMps.Value);

				// Power: watch value wins
				if (record.GetPower() is null or 0 && sample.Power is not null)
					record.SetPower(sample.Power.Value);

				// Cadence: watch value wins
				if (record.GetCadence() is null or 0 && sample.Cadence is not null)
					record.SetCadence(sample.Cadence.Value);

				// Resistance: FIT native field — watch never has this, Peloton always fills it
				if (record.GetResistance() is null or 0 && sample.Resistance is not null)
					record.SetResistance(sample.Resistance.Value);

				enriched++;
			}

			result.Add(record);
		}

		_logger.Information("Enriched {Enriched}/{Total} RecordMesg entries with Peloton data", enriched, messages.Count);

		// Patch Session and Lap with Peloton's exact distance/avg speed totals
		if (pelotonTotalDistanceMeters > 0)
		{
			_logger.Information("Patching Session/Lap with Peloton total distance {Distance:F0}m, avg speed {Avg:F2}m/s",
				pelotonTotalDistanceMeters, pelotonAvgSpeedMps);

			for (int i = 0; i < result.Count; i++)
			{
				if (result[i].Num == MesgNum.Session)
				{
					var session = new SessionMesg(result[i]);
					session.SetTotalDistance(pelotonTotalDistanceMeters);
					if (pelotonAvgSpeedMps > 0)
					{
						session.SetAvgSpeed(pelotonAvgSpeedMps);
						session.SetEnhancedAvgSpeed(pelotonAvgSpeedMps);
					}
					result[i] = session;
				}
				else if (result[i].Num == MesgNum.Lap)
				{
					var lap = new LapMesg(result[i]);
					lap.SetTotalDistance(pelotonTotalDistanceMeters);
					if (pelotonAvgSpeedMps > 0)
					{
						lap.SetAvgSpeed(pelotonAvgSpeedMps);
						lap.SetEnhancedAvgSpeed(pelotonAvgSpeedMps);
					}
					result[i] = lap;
				}
			}
		}

		return result;
	}

	// ─── Encode ──────────────────────────────────────────────────────────────

	private static byte[] EncodeMessages(List<Mesg> messages)
	{
		using var stream = new MemoryStream();
		var encoder = new Encode(ProtocolVersion.V20);
		try
		{
			encoder.Open(stream);
			encoder.Write(messages);
		}
		finally
		{
			encoder.Close();
		}

		return stream.ToArray();
	}

	// ─── Peloton metric helpers (mirrors FitConverter logic) ─────────────────

	private static float GetTotalDistanceMeters(WorkoutSamples samples)
	{
		var summary = samples?.Summaries?.FirstOrDefault(s => s.Slug == "distance");
		if (summary is null) return 0f;
		return ConvertDistanceToMeters((float)summary.Value.GetValueOrDefault(), summary.Display_Unit);
	}

	private static float GetAvgSpeedMetersPerSecond(WorkoutSamples samples)
	{
		var speedSummary = GetSpeedSummary(samples);
		if (speedSummary is null) return 0f;
		return ConvertToMetersPerSecond(speedSummary.Average_Value.GetValueOrDefault(), speedSummary.Display_Unit);
	}

	private static Metric GetCadenceSummary(WorkoutSamples samples)
	{
		var allMetrics = samples?.Metrics ?? new List<Metric>();
		return allMetrics.FirstOrDefault(m => m.Slug == "cadence")
			?? allMetrics.FirstOrDefault(m => m.Slug == "spm");  // rowing strokes-per-minute
	}

	private static Metric GetSpeedSummary(WorkoutSamples samples)
	{
		var allMetrics = samples?.Metrics ?? new List<Metric>();
		return allMetrics.FirstOrDefault(m => m.Slug == "speed")
			?? allMetrics.FirstOrDefault(m => m.Slug == "pace");
	}

	private static float ConvertToMetersPerSecond(double? value, string displayUnit)
	{
		float val = (float)value.GetValueOrDefault();
		if (val <= 0) return 0.0f;

		var unit = UnitHelpers.GetSpeedUnit(displayUnit);
		switch (unit)
		{
			case SpeedUnit.KilometersPerHour:
			case SpeedUnit.MilesPerHour:
				var meters = ConvertDistanceToMeters(val, displayUnit);
				return meters / 3600f;
			case SpeedUnit.MinutesPer500Meters:
				float secondsPer500m = val * 60f;
				return 500f / secondsPer500m;
			default:
				_logger.Error("Found unknown speed unit {Unit}", unit);
				return 0;
		}
	}

	private static float ConvertDistanceToMeters(float value, string unit)
	{
		var distanceUnit = UnitHelpers.GetDistanceUnit(unit);
		switch (distanceUnit)
		{
			case DistanceUnit.Kilometers:
				return value * 1000f;
			case DistanceUnit.Miles:
				return value * 1609.34f;
			case DistanceUnit.Feet:
				return value * 0.3048f;
			case DistanceUnit.FiveHundredMeters:
				return value / 500f;
			case DistanceUnit.Meters:
			default:
				return value;
		}
	}
}
