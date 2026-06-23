using Common.Dto.Peloton;
using Conversion;
using Dynastream.Fit;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnitTests.Garmin;

/// <summary>
/// Diagnostic test: decode real FIT backup files, run them through MergeWatchFitWithPeloton,
/// then compare field numbers before vs after to find what we're injecting that causes HTTP 415.
/// </summary>
[TestFixture]
public class FitMergeFieldDiffTests
{
	private static readonly string DownloadsDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "fit-backups");

	private static readonly string OutputDir = Path.Combine(
		Directory.GetCurrentDirectory(), "..", "..", "..", "..", "Api", "output", "fit-backups");

	[Test]
	public void DiffFields_RowingFit()
	{
		var fitPath = Path.Combine(DownloadsDir,
			"2026-06-19_23308810534_15 min Tabata Row with Alex Karwoski.fit");
		RunDiff("ROWING", fitPath, BuildRowingSamples());
	}

	[Test]
	public void DiffFields_StrengthFit()
	{
		var fitPath = Path.Combine(DownloadsDir,
			"2026-06-19_23308809916_30 min Upper Body Strength with Rad Lopez.fit");
		RunDiff("STRENGTH", fitPath, BuildStrengthSamples());
	}

	[Test]
	public void DiffFields_CyclingFit()
	{
		var dir = Path.GetFullPath(OutputDir);
		var fitPath = Directory.Exists(dir)
			? Directory.EnumerateFiles(dir, "*.fit").OrderByDescending(f => f).FirstOrDefault()
			: null;

		if (fitPath is null)
		{
			// Fall back to the test data directory
			fitPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "Data", "Fenix_Incline.fit");
		}

		RunDiff("CYCLING", fitPath, BuildCyclingSamples());
	}

	// ── helpers ──────────────────────────────────────────────────────────────

	private static void RunDiff(string label, string fitPath, WorkoutSamples samples)
	{
		Assert.That(System.IO.File.Exists(fitPath), Is.True,
			$"FIT file not found: {fitPath}");

		var originalBytes = System.IO.File.ReadAllBytes(fitPath);
		var workoutStartUnix = DateTimeOffset.UtcNow.AddMinutes(-60).ToUnixTimeSeconds();

		// Infer workout start from first record in the FIT
		workoutStartUnix = GetFirstRecordTimestamp(originalBytes) ?? workoutStartUnix;

		byte[] mergedBytes;
		try
		{
			mergedBytes = GarminFitMergeService.MergeWatchFitWithPeloton(originalBytes, samples, workoutStartUnix - 10);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[{label}] MergeWatchFitWithPeloton THREW: {ex.Message}");
			Assert.Fail($"Merge threw for {label}: {ex.Message}");
			return;
		}

		var beforeFields = GetMessageFields(originalBytes);
		var afterFields = GetMessageFields(mergedBytes);

		Console.WriteLine($"\n══════════════════════════════════════════");
		Console.WriteLine($"  {label}: {Path.GetFileName(fitPath)}");
		Console.WriteLine($"  Original: {originalBytes.Length} bytes  →  Merged: {mergedBytes.Length} bytes");
		Console.WriteLine($"══════════════════════════════════════════");

		foreach (var msgType in new[] { MesgNum.Record, MesgNum.Session, MesgNum.Lap, MesgNum.Activity })
		{
			var msgName = msgType == MesgNum.Record ? "RecordMesg" :
						  msgType == MesgNum.Session ? "SessionMesg" :
						  msgType == MesgNum.Lap ? "LapMesg" : "ActivityMesg";

			var before = beforeFields.GetValueOrDefault(msgType, new HashSet<byte>());
			var after = afterFields.GetValueOrDefault(msgType, new HashSet<byte>());

			var added = after.Except(before).OrderBy(f => f).ToList();
			var removed = before.Except(after).OrderBy(f => f).ToList();

			if (before.Count == 0 && after.Count == 0) continue;

			Console.WriteLine($"\n  [{msgName}]");
			Console.WriteLine($"    Before fields: {string.Join(", ", before.OrderBy(f => f))}");
			Console.WriteLine($"    After  fields: {string.Join(", ", after.OrderBy(f => f))}");
			if (added.Any())
				Console.WriteLine($"    *** ADDED:   {string.Join(", ", added.Select(f => $"{f} ({FieldName(msgType, f)})"))}");
			if (removed.Any())
				Console.WriteLine($"    --- REMOVED: {string.Join(", ", removed)}");
		}

		// Key assertion: log sport from session
		var sport = GetSportFromBytes(originalBytes);
		Console.WriteLine($"\n  Sport from FIT: {sport}");
		Console.WriteLine();
	}

	/// <summary>
	/// Decodes a FIT file and returns, per message type, the set of field numbers
	/// that actually appear across ALL messages of that type.
	/// </summary>
	private static Dictionary<ushort, HashSet<byte>> GetMessageFields(byte[] fitBytes)
	{
		var result = new Dictionary<ushort, HashSet<byte>>();

		using var stream = new MemoryStream(fitBytes);
		var decoder = new Decode();
		var broadcaster = new MesgBroadcaster();
		decoder.MesgEvent += broadcaster.OnMesg;
		decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;

		broadcaster.MesgEvent += (_, e) =>
		{
			var mesg = e.mesg;
			if (!result.TryGetValue(mesg.Num, out var fields))
			{
				fields = new HashSet<byte>();
				result[mesg.Num] = fields;
			}
			foreach (var field in mesg.Fields)
				fields.Add(field.Num);
		};

		try { decoder.Read(stream); } catch { /* partial decode is fine for diagnostics */ }

		return result;
	}

	private static long? GetFirstRecordTimestamp(byte[] fitBytes)
	{
		long? ts = null;
		using var stream = new MemoryStream(fitBytes);
		var decoder = new Decode();
		var broadcaster = new MesgBroadcaster();
		decoder.MesgEvent += broadcaster.OnMesg;
		decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
		broadcaster.MesgEvent += (_, e) =>
		{
			if (ts is not null) return;
			if (e.mesg.Num != MesgNum.Record) return;
			var r = new RecordMesg(e.mesg);
			var t = r.GetTimestamp();
			if (t is not null)
				ts = (long)(t.GetTimeStamp() + 631065600u);
		};
		try { decoder.Read(stream); } catch { }
		return ts;
	}

	private static string GetSportFromBytes(byte[] fitBytes)
	{
		string sport = "unknown";
		using var stream = new MemoryStream(fitBytes);
		var decoder = new Decode();
		var broadcaster = new MesgBroadcaster();
		decoder.MesgEvent += broadcaster.OnMesg;
		decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
		broadcaster.MesgEvent += (_, e) =>
		{
			if (e.mesg.Num != MesgNum.Session) return;
			var s = new SessionMesg(e.mesg);
			var sp = s.GetSport();
			if (sp is not null) sport = sp.ToString();
		};
		try { decoder.Read(stream); } catch { }
		return sport;
	}

	private static string FieldName(ushort msgNum, byte fieldNum)
	{
		if (msgNum == MesgNum.Record)
		{
			return fieldNum switch
			{
				253 => "Timestamp",
				0 => "PositionLat",
				1 => "PositionLong",
				2 => "Altitude",
				3 => "HeartRate",
				4 => "Cadence",
				5 => "Distance",
				6 => "Speed",
				7 => "Power",
				8 => "CompressedSpeedDistance",
				9 => "Grade",
				10 => "Resistance",
				29 => "Resistance(alt)",
				78 => "EnhancedAltitude",
				136 => "EnhancedSpeed",
				_ => "?"
			};
		}
		if (msgNum == MesgNum.Session)
		{
			return fieldNum switch
			{
				253 => "Timestamp",
				2 => "StartTime",
				5 => "Sport",
				6 => "SubSport",
				7 => "TotalElapsedTime",
				8 => "TotalTimerTime",
				9 => "TotalDistance",
				10 => "TotalCycles/Strokes",
				11 => "TotalCalories",
				14 => "AvgSpeed",
				15 => "MaxSpeed",
				16 => "AvgHeartRate",
				17 => "MaxHeartRate",
				18 => "AvgCadence",
				19 => "MaxCadence",
				20 => "AvgPower",
				21 => "MaxPower",
				22 => "TotalAscent",
				23 => "TotalDescent",
				24 => "TotalTrainingEffect",
				25 => "FirstLapIndex",
				26 => "NumLaps",
				28 => "Trigger",
				34 => "NormalizedPower",
				124 => "EnhancedAvgSpeed",
				125 => "EnhancedMaxSpeed",
				_ => "?"
			};
		}
		return "?";
	}

	// ── synthetic Peloton samples ─────────────────────────────────────────────

	private static WorkoutSamples BuildCyclingSamples()
	{
		return BuildSamples(cadenceSlug: "cadence", speedKph: 30.0, powerWatts: 200, resistancePct: 50);
	}

	private static WorkoutSamples BuildRowingSamples()
	{
		// Rowing uses spm cadence and pace (min/500m)
		return BuildSamples(cadenceSlug: "spm", pace500m: 2.0, powerWatts: 180, resistancePct: 0);
	}

	private static WorkoutSamples BuildStrengthSamples()
	{
		// Strength: output only, no speed/cadence
		return BuildSamples(cadenceSlug: null, speedKph: null, powerWatts: 150, resistancePct: 0);
	}

	private static WorkoutSamples BuildSamples(
		string cadenceSlug,
		double? speedKph = null,
		double? pace500m = null,
		int powerWatts = 0,
		int resistancePct = 0)
	{
		const int durationSec = 900; // 15 min
		var baseUnix = DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeSeconds();

		var seconds = Enumerable.Range(0, durationSec).Select(i => i).ToArray();
		var values = Enumerable.Repeat((double?)((double)powerWatts), durationSec).ToArray();

		var metrics = new List<Metric>();

		metrics.Add(new Metric
		{
			Slug = "output",
			Values = values,
			Average_Value = powerWatts,
			Max_Value = powerWatts + 20,
		});

		if (cadenceSlug is not null)
		{
			metrics.Add(new Metric
			{
				Slug = cadenceSlug,
				Values = Enumerable.Repeat((double?)80.0, durationSec).ToArray(),
				Average_Value = 80,
				Max_Value = 95,
			});
		}

		if (speedKph is not null)
		{
			metrics.Add(new Metric
			{
				Slug = "speed",
				Display_Unit = "kph",
				Values = Enumerable.Repeat((double?)speedKph, durationSec).ToArray(),
				Average_Value = speedKph,
				Max_Value = speedKph + 2,
			});
		}
		else if (pace500m is not null)
		{
			metrics.Add(new Metric
			{
				Slug = "pace",
				Display_Unit = "/500m",
				Values = Enumerable.Repeat((double?)pace500m, durationSec).ToArray(),
				Average_Value = pace500m,
				Max_Value = pace500m - 0.1,
			});
		}

		if (resistancePct > 0)
		{
			metrics.Add(new Metric
			{
				Slug = "resistance",
				Values = Enumerable.Repeat((double?)resistancePct, durationSec).ToArray(),
				Average_Value = resistancePct,
				Max_Value = resistancePct + 5,
			});
		}

		return new WorkoutSamples
		{
			Seconds_Since_Pedaling_Start = seconds,
			Metrics = metrics,
			Summaries = new List<Summary>
			{
				new Summary { Slug = "distance", Value = 2.5, Display_Unit = "km" }
			},
		};
	}
}
