using Common.Dto;
using Common.Observe;
using Dynastream.Fit;
using Serilog;
using System.Collections.Generic;
using System.Linq;

namespace Conversion;

/// <summary>
/// Turns a Peloton class's exercise list into native Garmin FIT exercise sets.
/// Garmin shows these as the activity's exercise list and builds its muscle map from them.
/// </summary>
public static class ExerciseSets
{
	private static readonly ILogger _logger = LogContext.ForStatic("ExerciseSets");

	public static List<SetMesg> Build(ICollection<P2GExercise> exercises, Dynastream.Fit.DateTime startTime, int defaultSecondsPerRep)
	{
		using var tracing = Tracing.Trace($"{nameof(ExerciseSets)}.{nameof(Build)}");

		var sets = new List<SetMesg>();
		if (defaultSecondsPerRep <= 0)
			defaultSecondsPerRep = 3;

		if (exercises is null || exercises.Count <= 0)
			return sets;

		ushort stepIndex = 0;
		foreach (var p2gExercise in exercises)
		{
			var isRest = p2gExercise.Id.IsRest();
			GarminExercise exercise = null;
			if (!isRest)
			{
				if (ExerciseMapping.IgnoredPelotonExercises.Contains(p2gExercise.Id))
					continue;

				if (!ExerciseMapping.StrengthExerciseMappings.TryGetValue(p2gExercise.Id, out exercise))
				{
					_logger.Information($"Found Peloton Strength exercise with no Garmin mapping: {p2gExercise.Name} {p2gExercise.Id}");
					continue;
				}
			}

			var setMesg = new SetMesg();
			var setStart = startTime.GetDateTime().AddSeconds(p2gExercise.StartOffsetSeconds);
			setMesg.SetStartTime(new Dynastream.Fit.DateTime(setStart));
			setMesg.SetTimestamp(new Dynastream.Fit.DateTime(setStart.AddSeconds(p2gExercise.DurationSeconds)));
			setMesg.SetDuration(p2gExercise.DurationSeconds);

			if (!isRest)
			{
				setMesg.SetCategory(0, exercise.ExerciseCategory);
				setMesg.SetCategorySubtype(0, exercise.ExerciseName);
			}

			setMesg.SetMessageIndex(stepIndex);
			setMesg.SetSetType(isRest ? SetType.Rest : SetType.Active);
			setMesg.SetWktStepIndex(stepIndex);

			var reps = p2gExercise.Reps;
			if (p2gExercise.Type == MovementTargetType.Time)
			{
				if (reps is null || reps <= 0)
					reps = p2gExercise.DurationSeconds / defaultSecondsPerRep;
			}

			setMesg.SetRepetitions((ushort)reps);

			if (p2gExercise.Weight?.Value > 0)
			{
				setMesg.SetWeight(FitConverter.ConvertWeightToKilograms(p2gExercise.Weight.Value, p2gExercise.Weight.Unit));
			}

			stepIndex++;
			sets.Add(setMesg);
		}

		return sets;
	}
}
