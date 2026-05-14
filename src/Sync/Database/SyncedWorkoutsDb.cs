using Common;
using Common.Database;
using Common.Observe;
using JsonFlatFileDataStore;
using Prometheus;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Sync.Database;

public record SyncedWorkout
{
	public string WorkoutId { get; init; } = string.Empty;
	public DateTime SyncedAt { get; init; }
}

public interface ISyncedWorkoutsDb
{
	Task<HashSet<string>> GetSyncedWorkoutIdsAsync();
	Task MarkSyncedAsync(IEnumerable<string> workoutIds);
}

public class SyncedWorkoutsDb : DbBase<SyncedWorkout>, ISyncedWorkoutsDb
{
	private static readonly ILogger _logger = LogContext.ForClass<SyncedWorkoutsDb>();
	private readonly DataStore _db;

	public SyncedWorkoutsDb(IFileHandling fileHandling) : base("SyncedWorkouts", fileHandling)
	{
		_db = new DataStore(DbPath);
	}

	public Task<HashSet<string>> GetSyncedWorkoutIdsAsync()
	{
		using var metrics = DbMetrics.DbActionDuration.WithLabels("get", DbName).NewTimer();
		using var tracing = Tracing.Trace($"{nameof(SyncedWorkoutsDb)}.{nameof(GetSyncedWorkoutIdsAsync)}", TagValue.Db);

		try
		{
			var ids = _db.GetCollection<SyncedWorkout>()
				.AsQueryable()
				.Select(w => w.WorkoutId)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			return Task.FromResult(ids);
		}
		catch (Exception e)
		{
			_logger.Error(e, "Failed to get synced workout IDs from db");
			return Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
		}
	}

	public async Task MarkSyncedAsync(IEnumerable<string> workoutIds)
	{
		using var metrics = DbMetrics.DbActionDuration.WithLabels("upsert", DbName).NewTimer();
		using var tracing = Tracing.Trace($"{nameof(SyncedWorkoutsDb)}.{nameof(MarkSyncedAsync)}", TagValue.Db);

		try
		{
			var collection = _db.GetCollection<SyncedWorkout>();
			var existing = collection.AsQueryable()
				.Select(w => w.WorkoutId)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			var now = DateTime.UtcNow;
			foreach (var id in workoutIds)
			{
				if (existing.Contains(id)) continue;
				await collection.InsertOneAsync(new SyncedWorkout { WorkoutId = id, SyncedAt = now });
			}
		}
		catch (Exception e)
		{
			_logger.Error(e, "Failed to mark workouts as synced in db");
		}
	}
}
