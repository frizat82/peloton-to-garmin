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

		// Log timing alignment so mismatches are visible in the app log.
		// Per-second injection fails silently when timestamps don't overlap.
		LogTimingDiagnostics(allMessages, pelotonSampleMap, workoutStartUnix);

		var totalDistanceMeters = GetTotalDistanceMeters(pelotonSamples);
		var avgSpeedMps = GetAvgSpeedMetersPerSecond(pelotonSamples);
		var maxSpeedMps = GetMaxSpeedMetersPerSecond(pelotonSamples);
		var avgCadence = GetAvgCadence(pelotonSamples);
		var maxCadence = GetMaxCadence(pelotonSamples);
		var avgPower = GetAvgPower(pelotonSamples);
		var maxPower = GetMaxPower(pelotonSamples);

		var mergedMessages = InjectPelotonIntoRecords(allMessages, pelotonSampleMap, totalDistanceMeters, avgSpeedMps, maxSpeedMps, avgCadence, maxCadence, avgPower, maxPower);

		return EncodeMessages(mergedMessages);
	}

	private static void LogTimingDiagnostics(List<Mesg> messages, Dictionary<uint, PelotonSample> pelotonMap, long workoutStartUnix)
	{
		if (pelotonMap.Count == 0) return;

		var pelotonMinKey = pelotonMap.Keys.Min();
		var pelotonMaxKey = pelotonMap.Keys.Max();
		var pelotonStart = DateTimeOffset.FromUnixTimeSeconds(pelotonMinKey).UtcDateTime;
		var pelotonEnd = DateTimeOffset.FromUnixTimeSeconds(pelotonMaxKey).UtcDateTime;

		uint garminFirstUnix = 0;
		foreach (var mesg in messages)
		{
			if (mesg.Num != MesgNum.Record) continue;
			var r = new RecordMesg(mesg);
			var ts = r.GetTimestamp();
			if (ts is null) continue;
			garminFirstUnix = ts.GetTimeStamp() + 631065600u;
			break;
		}

		if (garminFirstUnix == 0)
		{
			_logger.Warning("FIT merge timing: could not find any timestamped RecordMesg in watch FIT — per-second injection will be skipped");
			return;
		}

		var garminStart = DateTimeOffset.FromUnixTimeSeconds(garminFirstUnix).UtcDateTime;
		var offsetSec = (long)garminFirstUnix - workoutStartUnix;
		var overlaps = garminFirstUnix <= pelotonMaxKey && pelotonMinKey <= garminFirstUnix + 7200;

		_logger.Information(
			"FIT merge timing: Garmin first record {GarminStart:HH:mm:ss} UTC, Peloton samples {PelotonStart:HH:mm:ss}–{PelotonEnd:HH:mm:ss} UTC, offset={Offset:+0;-0;0}s, overlap={Overlap}",
			garminStart, pelotonStart, pelotonEnd, offsetSec, overlaps);

		if (!overlaps)
			_logger.Warning("FIT merge timing: Garmin recording and Peloton samples do NOT overlap — per-second injection will produce 0 enriched records. Check that FIT merge is matching the correct Garmin activity.");
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
		var outputMetrics = allMetrics.FirstOrDefault(m => m.Slug == "output");
		var cadenceMetrics = GetCadenceSummary(samples);
		var speedMetrics = GetSpeedSummary(samples);
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
		float pelotonAvgSpeedMps,
		float pelotonMaxSpeedMps,
		byte? pelotonAvgCadence,
		byte? pelotonMaxCadence,
		ushort? pelotonAvgPower,
		ushort? pelotonMaxPower)
	{
		int enriched = 0;
		int speedInjected = 0;

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

			// Speed plausibility bounds used for both matched and unmatched records.
			// The FIT SDK promotes Speed's 0xFFFF sentinel (65535/1000 = 65.535 m/s = 235.9 km/h)
			// into GetEnhancedSpeed() without treating it as invalid (65535 ≠ uint32 0xFFFFFFFF).
			// Some watches also write EnhancedSpeed as a 1-byte field, giving ~0.09 m/s garbage.
			// Anything outside 0.5–30 m/s is treated as "no real data".
			const float minPlausibleSpeedMps = 0.5f;
			const float maxPlausibleSpeedMps = 30f;

			if (sample is not null)
			{
				// Speed: watch value wins if plausible; Peloton fills otherwise.
				// Set both Speed (field 6) and EnhancedSpeed (field 136) — Garmin Connect
				// uses EnhancedSpeed for the per-second chart.
				var watchSpeed = record.GetSpeed();
				var watchESpeed = record.GetEnhancedSpeed();
				bool watchHasRealSpeed = (watchSpeed >= minPlausibleSpeedMps && watchSpeed <= maxPlausibleSpeedMps)
					|| (watchESpeed >= minPlausibleSpeedMps && watchESpeed <= maxPlausibleSpeedMps);
				if (!watchHasRealSpeed && sample.SpeedMps is not null)
				{
					record.SetSpeed(sample.SpeedMps.Value);
					record.SetEnhancedSpeed(sample.SpeedMps.Value);
					speedInjected++;
				}

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
			else
			{
				// No matching Peloton sample (pre/post-workout records). Clear any implausible
				// speed so the 0xFFFF sentinel doesn't spike the chart or inflate max speed.
				var watchSpeed = record.GetSpeed();
				var watchESpeed = record.GetEnhancedSpeed();
				bool hasGarbageSpeed = (watchSpeed > maxPlausibleSpeedMps) || (watchESpeed > maxPlausibleSpeedMps);
				if (hasGarbageSpeed)
				{
					record.SetSpeed(0f);
					record.SetEnhancedSpeed(0f);
				}
			}

			result.Add(record);
		}

		_logger.Information("Enriched {Enriched}/{Total} RecordMesg entries with Peloton data ({Speed} speed, cadence, power)", enriched, messages.Count, speedInjected);

		// Patch Session with Peloton total distance only when the watch didn't record one
		// (e.g. indoor cycling/rowing with no GPS). Never patch Laps — multi-sport workouts
		// (Tread Bootcamp etc.) record per-segment distances that must not be overwritten.
		// Also skip patching when there are multiple Sessions (multi-sport activity) since
		// Peloton's total distance only covers the machine portion, not the whole workout.
		if (pelotonTotalDistanceMeters > 0)
		{
			var sessionMessages = result.Where(m => m.Num == MesgNum.Session).ToList();
			var isSingleSession = sessionMessages.Count == 1;

			if (isSingleSession)
			{
				var session = new SessionMesg(sessionMessages[0]);
				var existingDistance = session.GetTotalDistance();

				if (existingDistance is null or 0)
				{
					_logger.Information("Patching single Session with Peloton total distance {Distance:F0}m, avg speed {Avg:F2}m/s",
						pelotonTotalDistanceMeters, pelotonAvgSpeedMps);

					session.SetTotalDistance(pelotonTotalDistanceMeters);
					if (pelotonAvgSpeedMps > 0)
					{
						session.SetAvgSpeed(pelotonAvgSpeedMps);
						session.SetEnhancedAvgSpeed(pelotonAvgSpeedMps);
					}
					if (pelotonMaxSpeedMps > 0)
					{
						session.SetMaxSpeed(pelotonMaxSpeedMps);
						session.SetEnhancedMaxSpeed(pelotonMaxSpeedMps);
					}

					var idx = result.IndexOf(sessionMessages[0]);
					result[idx] = session;
				}
				else
				{
					_logger.Information("Session already has distance {Existing:F0}m — skipping Peloton distance patch", existingDistance);
				}
			}
			else
			{
				_logger.Information("Multi-sport activity ({Count} sessions) — skipping Peloton distance patch to preserve per-segment distances", sessionMessages.Count);
			}
		}

		// Patch Session cadence and power summary fields. Watch values always win;
		// Peloton fills fields the watch left null/zero (e.g. indoor bike has no
		// cadence sensor on most Garmin watches).
		for (int i = 0; i < result.Count; i++)
		{
			if (result[i].Num != MesgNum.Session) continue;

			// Snapshot which field numbers the watch actually defined in this Session.
			// We only supplement fields the watch already declared — never create new ones.
			// (Field 18=AvgCadence, 19=MaxCadence, 20=AvgPower, 21=MaxPower in FIT profile)
			var watchSessionFields = new HashSet<byte>(result[i].Fields.Select(f => f.Num));

			var session = new SessionMesg(result[i]);
			bool modified = false;

			if (watchSessionFields.Contains(18) && session.GetAvgCadence() is null or 0 && pelotonAvgCadence is not null)
			{ session.SetAvgCadence(pelotonAvgCadence.Value); modified = true; }
			if (watchSessionFields.Contains(19) && session.GetMaxCadence() is null or 0 && pelotonMaxCadence is not null)
			{ session.SetMaxCadence(pelotonMaxCadence.Value); modified = true; }
			if (watchSessionFields.Contains(20) && session.GetAvgPower() is null or 0 && pelotonAvgPower is not null)
			{ session.SetAvgPower(pelotonAvgPower.Value); modified = true; }
			if (watchSessionFields.Contains(21) && session.GetMaxPower() is null or 0 && pelotonMaxPower is not null)
			{ session.SetMaxPower(pelotonMaxPower.Value); modified = true; }

			if (modified)
			{
				_logger.Information("Patched Session cadence avg={AvgC} max={MaxC}, power avg={AvgP} max={MaxP}",
					pelotonAvgCadence, pelotonMaxCadence, pelotonAvgPower, pelotonMaxPower);
				result[i] = session;
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

	private static float GetMaxSpeedMetersPerSecond(WorkoutSamples samples)
	{
		var speedSummary = GetSpeedSummary(samples);
		if (speedSummary is null) return 0f;
		return ConvertToMetersPerSecond(speedSummary.Max_Value.GetValueOrDefault(), speedSummary.Display_Unit);
	}

	private static float GetAvgSpeedMetersPerSecond(WorkoutSamples samples)
	{
		var speedSummary = GetSpeedSummary(samples);
		if (speedSummary is null) return 0f;
		return ConvertToMetersPerSecond(speedSummary.Average_Value.GetValueOrDefault(), speedSummary.Display_Unit);
	}

	private static byte? GetAvgCadence(WorkoutSamples samples)
	{
		var metric = GetCadenceSummary(samples);
		if (metric?.Average_Value is null) return null;
		return (byte)Math.Min(metric.Average_Value.Value, 255);
	}

	private static byte? GetMaxCadence(WorkoutSamples samples)
	{
		var metric = GetCadenceSummary(samples);
		if (metric?.Max_Value is null) return null;
		return (byte)Math.Min(metric.Max_Value.Value, 255);
	}

	private static ushort? GetAvgPower(WorkoutSamples samples)
	{
		var metric = samples?.Metrics?.FirstOrDefault(m => m.Slug == "output");
		if (metric?.Average_Value is null) return null;
		return (ushort)metric.Average_Value.Value;
	}

	private static ushort? GetMaxPower(WorkoutSamples samples)
	{
		var metric = samples?.Metrics?.FirstOrDefault(m => m.Slug == "output");
		if (metric?.Max_Value is null) return null;
		return (ushort)metric.Max_Value.Value;
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
