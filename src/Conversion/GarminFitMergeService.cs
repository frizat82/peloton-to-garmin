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

		// Cycling watches without a cadence/power sensor won't declare those fields,
		// but Garmin accepts new field definitions in cycling FITs. Always inject for cycling.
		// Rowing/strength keep the supplement-only guard to prevent 415 rejections.
		var sessionSport = allMessages
			.Where(m => m.Num == MesgNum.Session)
			.Select(m => (Sport?)new SessionMesg(m).GetSport())
			.FirstOrDefault();
		var isCycling = sessionSport == Sport.Cycling;
		_logger.Information("FIT merge: sport={Sport} isCycling={IsCycling}", sessionSport, isCycling);

		var mergedMessages = InjectPelotonIntoRecords(allMessages, pelotonSampleMap, totalDistanceMeters, avgSpeedMps, maxSpeedMps, avgCadence, maxCadence, avgPower, maxPower, isCycling);

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
		ushort? pelotonMaxPower,
		bool isCycling)
	{
		int enriched = 0;
		int speedInjected = 0;

		// Snapshot which field numbers are declared across ALL RecordMesgs.
		// We only write to fields the watch already declared — never create new ones.
		// (field 4=Cadence, 6=Speed, 7=Power, 29=Resistance, 136=EnhancedSpeed)
		var watchRecordFields = new HashSet<byte>(
			messages.Where(m => m.Num == MesgNum.Record)
					.SelectMany(m => m.Fields)
					.Select(f => f.Num));

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
				// Only write to fields the watch declared (field 6=Speed, 136=EnhancedSpeed).
				var watchSpeed = record.GetSpeed();
				var watchESpeed = record.GetEnhancedSpeed();
				bool watchHasRealSpeed = (watchSpeed >= minPlausibleSpeedMps && watchSpeed <= maxPlausibleSpeedMps)
					|| (watchESpeed >= minPlausibleSpeedMps && watchESpeed <= maxPlausibleSpeedMps);
				if (!watchHasRealSpeed && sample.SpeedMps is not null)
				{
					if (watchRecordFields.Contains(6)) record.SetSpeed(sample.SpeedMps.Value);
					if (watchRecordFields.Contains(136)) record.SetEnhancedSpeed(sample.SpeedMps.Value);
					speedInjected++;
				}

				// Power: cycling always injects (Peloton is the only source on an indoor bike;
				// Garmin accepts new field definitions in cycling FITs). Non-cycling: only if declared.
				if ((isCycling || watchRecordFields.Contains(7)) && record.GetPower() is null or 0 && sample.Power is not null)
					record.SetPower(sample.Power.Value);

				// Cadence: same logic as power
				if ((isCycling || watchRecordFields.Contains(4)) && record.GetCadence() is null or 0 && sample.Cadence is not null)
					record.SetCadence(sample.Cadence.Value);

				// Resistance: same logic as power
				if ((isCycling || watchRecordFields.Contains(29)) && record.GetResistance() is null or 0 && sample.Resistance is not null)
					record.SetResistance(sample.Resistance.Value);

				enriched++;
			}
			else
			{
				// No matching Peloton sample (pre/post-workout records). Clear any implausible
				// speed so the 0xFFFF sentinel doesn't spike the chart or inflate max speed.
				// GetSpeed/GetEnhancedSpeed return null when undeclared, so null > 30f is false
				// and these Set(0) calls are only reached for fields that actually exist.
				var watchSpeed = record.GetSpeed();
				var watchESpeed = record.GetEnhancedSpeed();
				bool hasGarbageSpeed = (watchSpeed > maxPlausibleSpeedMps) || (watchESpeed > maxPlausibleSpeedMps);
				if (hasGarbageSpeed)
				{
					if (watchRecordFields.Contains(6)) record.SetSpeed(0f);
					if (watchRecordFields.Contains(136)) record.SetEnhancedSpeed(0f);
				}
			}

			result.Add(record);
		}

		_logger.Information("Enriched {Enriched}/{Total} RecordMesg entries with Peloton data ({Speed} speed, cadence, power)", enriched, messages.Count, speedInjected);

		// Patch Session summary fields. All writes are gated on the watch having declared
		// the field — we supplement only, never create new field definitions.
		// Distance/speed: single-session indoor activities only (no GPS distance from watch).
		// Cadence/power: fills fields the watch left null/zero.
		var isSingleSession = result.Count(m => m.Num == MesgNum.Session) == 1;

		if (!isSingleSession)
			_logger.Information("Multi-sport activity — skipping Peloton distance patch to preserve per-segment distances");

		for (int i = 0; i < result.Count; i++)
		{
			if (result[i].Num != MesgNum.Session) continue;

			// Snapshot which field numbers the watch actually defined in this Session.
			var watchSessionFields = new HashSet<byte>(result[i].Fields.Select(f => f.Num));

			var session = new SessionMesg(result[i]);
			bool modified = false;

			// Distance / speed (field 9, 14, 15, 124, 125) — single session only.
			// Cycling bypasses field guard: indoor bikes have no GPS so watch session
			// won't declare these, but Garmin accepts them in cycling FITs.
			if (isSingleSession && pelotonTotalDistanceMeters > 0)
			{
				var existingDistance = session.GetTotalDistance();
				if (existingDistance is null or 0)
				{
					if (isCycling || watchSessionFields.Contains(9))
					{ session.SetTotalDistance(pelotonTotalDistanceMeters); modified = true; }
					if (pelotonAvgSpeedMps > 0 && (isCycling || watchSessionFields.Contains(14)))
					{ session.SetAvgSpeed(pelotonAvgSpeedMps); modified = true; }
					if (pelotonAvgSpeedMps > 0 && (isCycling || watchSessionFields.Contains(124)))
					{ session.SetEnhancedAvgSpeed(pelotonAvgSpeedMps); modified = true; }
					if (pelotonMaxSpeedMps > 0 && (isCycling || watchSessionFields.Contains(15)))
					{ session.SetMaxSpeed(pelotonMaxSpeedMps); modified = true; }
					if (pelotonMaxSpeedMps > 0 && (isCycling || watchSessionFields.Contains(125)))
					{ session.SetEnhancedMaxSpeed(pelotonMaxSpeedMps); modified = true; }

					if (modified)
						_logger.Information("Patched Session distance {Distance:F0}m, avg speed {Avg:F2}m/s",
							pelotonTotalDistanceMeters, pelotonAvgSpeedMps);
				}
				else
				{
					_logger.Information("Session already has distance {Existing:F0}m — skipping Peloton distance patch", existingDistance);
				}
			}

			// Cadence / power (field 18, 19, 20, 21).
			// Cycling bypasses field guard: watch without sensors won't declare these.
			if ((isCycling || watchSessionFields.Contains(18)) && session.GetAvgCadence() is null or 0 && pelotonAvgCadence is not null)
			{ session.SetAvgCadence(pelotonAvgCadence.Value); modified = true; }
			if ((isCycling || watchSessionFields.Contains(19)) && session.GetMaxCadence() is null or 0 && pelotonMaxCadence is not null)
			{ session.SetMaxCadence(pelotonMaxCadence.Value); modified = true; }
			if ((isCycling || watchSessionFields.Contains(20)) && session.GetAvgPower() is null or 0 && pelotonAvgPower is not null)
			{ session.SetAvgPower(pelotonAvgPower.Value); modified = true; }
			if ((isCycling || watchSessionFields.Contains(21)) && session.GetMaxPower() is null or 0 && pelotonMaxPower is not null)
			{ session.SetMaxPower(pelotonMaxPower.Value); modified = true; }

			if (modified)
				result[i] = session;
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
