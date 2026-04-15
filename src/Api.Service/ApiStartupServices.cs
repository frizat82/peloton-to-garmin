using Api.Service;
using Api.Services;
using Common;
using Common.Database;
using Common.Service;
using Conversion;
using Garmin;
using Garmin.Auth;
using Garmin.Database;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Peloton;
using Peloton.AnnualChallenge;
using Peloton.Auth;
using Philosowaffle.Capability.ReleaseChecks;
using Sync;
using Sync.Database;

namespace SharedStartup;

public static class ApiStartupServices
{
	public static void ConfigureP2GApiServices(this IServiceCollection services)
	{
		// HOSTED SERVICES
		services.AddHostedService<BackgroundSyncJob>();

		// CACHE
		services.AddSingleton<IMemoryCache, MemoryCache>();

		// CONVERT
		services.AddSingleton<IConverter, FitConverter>();
		services.AddSingleton<IConverter, TcxConverter>();
		services.AddSingleton<IConverter, JsonConverter>();

		// GARMIN
		services.AddSingleton<IGarminUploader, GarminUploader>();
		services.AddSingleton<IGarminApiClient, Garmin.ApiClient>();
		services.AddSingleton<IGarminAuthenticationService, GarminAuthenticationService>();
		services.AddSingleton<IGarminActivityEnrichmentService, GarminActivityEnrichmentService>();
		services.AddSingleton<IGarminDb, GarminDb>();
		services.AddSingleton<IGarminMergeDb, GarminMergeDb>();

		// IO
		services.AddSingleton<IFileHandling, IOWrapper>();

		// MIGRATIONS
		services.AddSingleton<IDbMigrations, DbMigrations>();

		// PELOTON
		services.AddSingleton<ITrainingAnalysisService, TrainingAnalysisService>();
		services.AddSingleton<IPelotonAuthApiClient, PelotonAuthApiClient>();
		services.AddSingleton<IPelotonApi, Peloton.ApiClient>();
		services.AddSingleton<IPelotonService, PelotonService>();
		services.AddSingleton<IPelotonAnnualChallengeService, PelotonAnnualChallengeService>();
		services.AddSingleton<IAnnualChallengeService, AnnualChallengeService>();

		// RELEASE CHECKS
		services.AddGitHubReleaseChecker();

		// SETTINGS
		services.AddSingleton<ISettingsUpdaterService, SettingsUpdaterService>();
		services.AddSingleton<ISettingsDb, SettingsDb>();
		services.AddSingleton<ISettingsService, SettingsService>();

		// SYNC
		services.AddSingleton<ISyncStatusDb, SyncStatusDb>();
		services.AddSingleton<ISyncService, SyncService>();

		// SYSTEM INFO
		services.AddSingleton<IVersionInformationService, VersionInformationService>();
		services.AddSingleton<ISystemInfoService, SystemInfoService>();

		// USERS
		services.AddSingleton<IUsersDb, UsersDb>();
	}
}
