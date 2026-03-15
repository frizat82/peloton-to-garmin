using Common;
using Common.Database;
using Common.Observe;
using Garmin.Dto;
using JsonFlatFileDataStore;
using Prometheus;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Garmin.Database;

public interface IGarminMergeDb
{
	Task<ICollection<GarminMergeRecord>> GetRecentAsync(int count = 50);
	Task SaveAsync(GarminMergeRecord record);
}

public class GarminMergeDb : DbBase<GarminMergeRecord>, IGarminMergeDb
{
	private static readonly ILogger _logger = LogContext.ForClass<GarminMergeDb>();

	private readonly DataStore _db;

	public GarminMergeDb(IFileHandling fileHandling) : base("GarminMerge", fileHandling)
	{
		_db = new DataStore(DbPath);
	}

	public Task<ICollection<GarminMergeRecord>> GetRecentAsync(int count = 50)
	{
		using var metrics = DbMetrics.DbActionDuration
								.WithLabels("get", DbName)
								.NewTimer();
		using var tracing = Tracing.Trace($"{nameof(GarminMergeDb)}.{nameof(GetRecentAsync)}", TagValue.Db)
									.WithTable(DbName);
		try
		{
			var collection = _db.GetCollection<GarminMergeRecord>();
			ICollection<GarminMergeRecord> result = collection.AsQueryable()
				.OrderByDescending(r => r.MergedAt)
				.Take(count)
				.ToList();
			return Task.FromResult(result);
		}
		catch (Exception e)
		{
			_logger.Error(e, "Failed to get merge records from db");
			return Task.FromResult<ICollection<GarminMergeRecord>>(new List<GarminMergeRecord>());
		}
	}

	public Task SaveAsync(GarminMergeRecord record)
	{
		using var metrics = DbMetrics.DbActionDuration
								.WithLabels("insert", DbName)
								.NewTimer();
		using var tracing = Tracing.Trace($"{nameof(GarminMergeDb)}.{nameof(SaveAsync)}", TagValue.Db)
									.WithTable(DbName);

		var collection = _db.GetCollection<GarminMergeRecord>();
		return collection.InsertOneAsync(record);
	}
}
