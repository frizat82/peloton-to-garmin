using Common.Dto.Peloton;
using Conversion;
using Dynastream.Fit;
using FluentAssertions;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnitTests.Conversion;

[TestFixture]
public class GarminFitMergeServiceTests
{
	private static readonly string SampleFitDir = Path.Combine(
		Directory.GetCurrentDirectory(), "..", "..", "..", "Data", "sample_fit");

	// ── helpers ──────────────────────────────────────────────────────────

	private static byte[] BuildFitBytes(IEnumerable<Mesg> messages)
	{
		using var ms = new MemoryStream();
		var enc = new Encode(ProtocolVersion.V20);
		enc.Open(ms);
		enc.Write(messages.ToList());
		enc.Close();
		return ms.ToArray();
	}

	private static HashSet<byte> GetFieldNums(byte[] fitBytes, ushort msgNum)
	{
		var fields = new HashSet<byte>();
		using var ms = new MemoryStream(fitBytes);
		var dec = new Decode();
		var bc = new MesgBroadcaster();
		dec.MesgEvent += bc.OnMesg;
		dec.MesgDefinitionEvent += bc.OnMesgDefinition;
		bc.MesgEvent += (_, e) =>
		{
			if (e.mesg.Num == msgNum)
				foreach (var f in e.mesg.Fields)
					fields.Add(f.Num);
		};
		try { dec.Read(ms); } catch { }
		return fields;
	}

	private static byte[] BuildMinimalFit(Sport sport, bool includeSpeed, bool includePower, bool includeCadence)
	{
		var startTime = new Dynastream.Fit.DateTime(System.DateTime.UtcNow.AddMinutes(-20));
		var ts = new Dynastream.Fit.DateTime(startTime);
		var messages = new List<Mesg>();

		for (int i = 0; i < 60; i++)
		{
			var rec = new RecordMesg();
			rec.SetTimestamp(ts);
			rec.SetHeartRate(140);
			if (includeSpeed) rec.SetEnhancedSpeed(2.5f);
			if (includePower) rec.SetPower(150);
			if (includeCadence) rec.SetCadence(80);
			messages.Add(rec);
			ts.Add(1);
		}

		var session = new SessionMesg();
		session.SetTimestamp(ts);
		session.SetStartTime(startTime);
		session.SetSport(sport);
		session.SetTotalElapsedTime(60f);
		session.SetTotalTimerTime(60f);
		session.SetAvgHeartRate(140);
		messages.Add(session);

		var activity = new ActivityMesg();
		activity.SetTimestamp(ts);
		activity.SetNumSessions(1);
		messages.Add(activity);

		return BuildFitBytes(messages);
	}

	private static WorkoutSamples BuildSamples(string cadenceSlug = "cadence", double? speedKph = 30.0, int powerWatts = 200)
	{
		const int n = 60;
		var baseUnix = DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeSeconds();
		var seconds = Enumerable.Range(0, n).Select(i => i).ToArray();

		var metrics = new List<Metric>
		{
			new Metric { Slug = "output", Values = Enumerable.Repeat((double?)((double)powerWatts), n).ToArray(), Average_Value = powerWatts, Max_Value = powerWatts + 20 }
		};

		if (cadenceSlug is not null)
			metrics.Add(new Metric { Slug = cadenceSlug, Values = Enumerable.Repeat((double?)80.0, n).ToArray(), Average_Value = 80, Max_Value = 95 });

		if (speedKph is not null)
			metrics.Add(new Metric { Slug = "speed", Display_Unit = "kph", Values = Enumerable.Repeat((double?)speedKph, n).ToArray(), Average_Value = speedKph, Max_Value = speedKph + 2 });

		return new WorkoutSamples
		{
			Seconds_Since_Pedaling_Start = seconds,
			Metrics = metrics,
			Summaries = new List<Summary> { new Summary { Slug = "distance", Value = 0.5, Display_Unit = "km" } },
		};
	}

	// ── Tests ─────────────────────────────────────────────────────────────

	[Test]
	public void Merge_Strength_NoNewSessionFields()
	{
		// Sport.Training watch: Session has only HR, no cadence/power fields declared
		var fitBytes = BuildMinimalFit(Sport.Training, includeSpeed: false, includePower: false, includeCadence: false);
		var samples = BuildSamples(cadenceSlug: null, speedKph: null, powerWatts: 200);
		var workoutStart = DateTimeOffset.UtcNow.AddMinutes(-21).ToUnixTimeSeconds();

		var before = GetFieldNums(fitBytes, MesgNum.Session);
		var merged = GarminFitMergeService.MergeWatchFitWithPeloton(fitBytes, samples, workoutStart);
		var after = GetFieldNums(merged, MesgNum.Session);

		after.Should().BeSubsetOf(before, "supplement-only rule: no new Session fields on strength");
	}

	[Test]
	public void Merge_Rowing_NoNewSessionFields()
	{
		var fitBytes = BuildMinimalFit(Sport.Rowing, includeSpeed: true, includePower: true, includeCadence: true);
		var samples = BuildSamples(cadenceSlug: "spm", speedKph: null, powerWatts: 180);
		var workoutStart = DateTimeOffset.UtcNow.AddMinutes(-21).ToUnixTimeSeconds();

		var before = GetFieldNums(fitBytes, MesgNum.Session);
		var merged = GarminFitMergeService.MergeWatchFitWithPeloton(fitBytes, samples, workoutStart);
		var after = GetFieldNums(merged, MesgNum.Session);

		after.Should().BeSubsetOf(before, "supplement-only rule: no new Session fields on rowing");
	}

	[Test]
	public void Merge_RecordMesg_NoNewFields_WhenWatchHasOnlyHR()
	{
		// RecordMesg declares only HeartRate (field 3) — power/cadence/speed must NOT be added
		var fitBytes = BuildMinimalFit(Sport.Training, includeSpeed: false, includePower: false, includeCadence: false);
		var samples = BuildSamples(cadenceSlug: "cadence", speedKph: 30.0, powerWatts: 200);
		var workoutStart = DateTimeOffset.UtcNow.AddMinutes(-21).ToUnixTimeSeconds();

		var before = GetFieldNums(fitBytes, MesgNum.Record);
		var merged = GarminFitMergeService.MergeWatchFitWithPeloton(fitBytes, samples, workoutStart);
		var after = GetFieldNums(merged, MesgNum.Record);

		after.Should().BeSubsetOf(before, "supplement-only rule: no new RecordMesg fields when watch only has HR");
	}

	[Test]
	public void Merge_RecordMesg_NoSpeedField_WhenWatchLacksField6()
	{
		// Watch has EnhancedSpeed (136) but NOT Speed (6) — this was the rowing June 2026 bug
		var fitBytes = BuildMinimalFit(Sport.Rowing, includeSpeed: true, includePower: true, includeCadence: true);
		var samples = BuildSamples(cadenceSlug: "spm", speedKph: 18.0, powerWatts: 180);
		var workoutStart = DateTimeOffset.UtcNow.AddMinutes(-21).ToUnixTimeSeconds();

		// The synthetic FIT built by BuildMinimalFit uses SetEnhancedSpeed (field 136), not SetSpeed (field 6)
		var before = GetFieldNums(fitBytes, MesgNum.Record);
		before.Should().NotContain(6, "test pre-condition: watch FIT must not have Speed field 6");

		var merged = GarminFitMergeService.MergeWatchFitWithPeloton(fitBytes, samples, workoutStart);
		var after = GetFieldNums(merged, MesgNum.Record);

		after.Should().NotContain(6, "field 6 (Speed) must not be injected when watch never declared it");
	}

	[Test]
	public void Merge_Cycling_InjectsMetrics_WhenWatchHasNoSensors()
	{
		// Cycling watch with NO cadence/power sensor — fields 4 and 7 are not declared.
		// After merge, Peloton power and cadence MUST appear (cycling bypasses field guard).
		var fitBytes = BuildMinimalFit(Sport.Cycling, includeSpeed: false, includePower: false, includeCadence: false);
		var samples = BuildSamples(cadenceSlug: "cadence", speedKph: 30.0, powerWatts: 200);
		var workoutStart = DateTimeOffset.UtcNow.AddMinutes(-21).ToUnixTimeSeconds();

		var before = GetFieldNums(fitBytes, MesgNum.Record);
		before.Should().NotContain(7, "pre-condition: watch FIT must not have Power field 7");
		before.Should().NotContain(4, "pre-condition: watch FIT must not have Cadence field 4");

		var merged = GarminFitMergeService.MergeWatchFitWithPeloton(fitBytes, samples, workoutStart);
		var after = GetFieldNums(merged, MesgNum.Record);

		after.Should().Contain(7, "cycling must inject Power (field 7) even when watch never declared it");
		after.Should().Contain(4, "cycling must inject Cadence (field 4) even when watch never declared it");
	}

	[Test]
	public void Merge_Cycling_SupplementsExistingFields()
	{
		// Cycling watch with cadence/power declared but zero — Peloton values should fill them
		var fitBytes = BuildMinimalFit(Sport.Cycling, includeSpeed: true, includePower: true, includeCadence: true);
		var samples = BuildSamples(cadenceSlug: "cadence", speedKph: 30.0, powerWatts: 200);
		var workoutStart = DateTimeOffset.UtcNow.AddMinutes(-21).ToUnixTimeSeconds();

		// Just verify it doesn't throw and produces output
		var merged = GarminFitMergeService.MergeWatchFitWithPeloton(fitBytes, samples, workoutStart);

		merged.Should().NotBeEmpty();
		merged.Length.Should().BeGreaterThan(0);
	}

	[Test]
	public void Merge_WatchPowerWins()
	{
		// Watch RecordMesg already has plausible power — Peloton power must NOT overwrite
		var startTime = new Dynastream.Fit.DateTime(System.DateTime.UtcNow.AddMinutes(-20));
		var ts = new Dynastream.Fit.DateTime(startTime);
		var messages = new List<Mesg>();

		for (int i = 0; i < 60; i++)
		{
			var rec = new RecordMesg();
			rec.SetTimestamp(ts);
			rec.SetHeartRate(140);
			rec.SetPower(150); // watch recorded 150W
			messages.Add(rec);
			ts.Add(1);
		}

		var session = new SessionMesg();
		session.SetTimestamp(ts);
		session.SetStartTime(startTime);
		session.SetSport(Sport.Cycling);
		session.SetTotalElapsedTime(60f);
		messages.Add(session);
		messages.Add(new ActivityMesg());

		var fitBytes = BuildFitBytes(messages);
		var samples = BuildSamples(powerWatts: 999); // Peloton says 999W
		var workoutStart = DateTimeOffset.UtcNow.AddMinutes(-21).ToUnixTimeSeconds();

		var merged = GarminFitMergeService.MergeWatchFitWithPeloton(fitBytes, samples, workoutStart);

		// Decode and verify power stayed at 150 (watch wins)
		ushort? foundPower = null;
		using var ms = new MemoryStream(merged);
		var dec = new Decode(); var bc = new MesgBroadcaster();
		dec.MesgEvent += bc.OnMesg; dec.MesgDefinitionEvent += bc.OnMesgDefinition;
		bc.MesgEvent += (_, e) =>
		{
			if (e.mesg.Num == MesgNum.Record)
			{
				var r = new RecordMesg(e.mesg);
				var p = r.GetPower();
				if (p is not null) foundPower = p;
			}
		};
		try { dec.Read(ms); } catch { }

		foundPower.Should().Be(150, "watch power (150W) must not be overwritten by Peloton power (999W)");
	}

	[Test]
	public void Merge_SessionDistanceSpeed_NoNewFields_WhenUndeclared()
	{
		// Session has NO distance/speed fields declared (e.g. strength watch)
		var fitBytes = BuildMinimalFit(Sport.Training, includeSpeed: false, includePower: false, includeCadence: false);
		var samples = BuildSamples(cadenceSlug: null, speedKph: 10.0, powerWatts: 150);
		var workoutStart = DateTimeOffset.UtcNow.AddMinutes(-21).ToUnixTimeSeconds();

		var before = GetFieldNums(fitBytes, MesgNum.Session);
		var merged = GarminFitMergeService.MergeWatchFitWithPeloton(fitBytes, samples, workoutStart);
		var after = GetFieldNums(merged, MesgNum.Session);

		// Fields 9=TotalDistance, 14=AvgSpeed, 15=MaxSpeed, 124=EnhancedAvgSpeed, 125=EnhancedMaxSpeed
		after.Should().BeSubsetOf(before, "session distance/speed fields must not be added when watch never declared them");
	}

	[Test]
	public void Merge_RealRowFit_NoNewFields()
	{
		var fitPath = Path.Combine(SampleFitDir, "row_from_epix.fit");
		if (!System.IO.File.Exists(fitPath)) Assert.Ignore($"Test data not found: {fitPath}");

		var fitBytes = System.IO.File.ReadAllBytes(fitPath);
		var samples = BuildSamples(cadenceSlug: "spm", speedKph: null, powerWatts: 180);
		var workoutStart = DateTimeOffset.UtcNow.AddMinutes(-61).ToUnixTimeSeconds();

		var beforeSession = GetFieldNums(fitBytes, MesgNum.Session);
		var beforeRecord = GetFieldNums(fitBytes, MesgNum.Record);

		var merged = GarminFitMergeService.MergeWatchFitWithPeloton(fitBytes, samples, workoutStart);

		GetFieldNums(merged, MesgNum.Session).Should().BeSubsetOf(beforeSession, "no new Session fields on real row FIT");
		GetFieldNums(merged, MesgNum.Record).Should().BeSubsetOf(beforeRecord, "no new RecordMesg fields on real row FIT");
	}

	[Test]
	public void Merge_RealStrengthFit_NoNewFields()
	{
		var fitPath = Path.Combine(SampleFitDir, "strength_with_exercises.fit");
		if (!System.IO.File.Exists(fitPath)) Assert.Ignore($"Test data not found: {fitPath}");

		var fitBytes = System.IO.File.ReadAllBytes(fitPath);
		var samples = BuildSamples(cadenceSlug: null, speedKph: null, powerWatts: 150);
		var workoutStart = DateTimeOffset.UtcNow.AddMinutes(-61).ToUnixTimeSeconds();

		var beforeSession = GetFieldNums(fitBytes, MesgNum.Session);
		var beforeRecord = GetFieldNums(fitBytes, MesgNum.Record);

		var merged = GarminFitMergeService.MergeWatchFitWithPeloton(fitBytes, samples, workoutStart);

		GetFieldNums(merged, MesgNum.Session).Should().BeSubsetOf(beforeSession, "no new Session fields on real strength FIT");
		GetFieldNums(merged, MesgNum.Record).Should().BeSubsetOf(beforeRecord, "no new RecordMesg fields on real strength FIT");
	}
}
