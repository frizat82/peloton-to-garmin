using Common;
using Common.Database;
using Common.Http;
using Common.Observe;
using Common.Service;
using Common.Stateful;
using ConsoleClient;
using Conversion;
using Garmin;
using Garmin.Auth;
using Garmin.Database;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Peloton;
using Peloton.Auth;
using Philosowaffle.Capability.ReleaseChecks;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Settings.Configuration;
using Sync;
using Sync.Database;
using System;
using System.IO;

Statics.AppType = Constants.ConsoleAppName;
Statics.MetricPrefix = Constants.ConsoleAppName;
Statics.TracingService = Constants.ConsoleAppName;

using IHost host = CreateHostBuilder(args).Build();
await host.RunAsync();

static IHostBuilder CreateHostBuilder(string[] args)
{
	return Host.CreateDefaultBuilder(args)
		.ConfigureAppConfiguration(configBuilder =>
		{
			configBuilder.Sources.Clear();

			var configDirectory = Statics.DefaultConfigDirectory;
			if (args.Length > 0) configDirectory = args[0];

			Statics.ConfigPath = Path.Join(configDirectory, "configuration.local.json");

			configBuilder
				.AddJsonFile(Statics.ConfigPath, optional: true, reloadOnChange: true)
				.AddEnvironmentVariables(prefix: $"{Constants.EnvironmentVariablePrefix}_")
				.AddCommandLine(args)
				.Build();

		})
		.UseSerilog((ctx, logConfig) =>
		{
			logConfig
				.ReadFrom.Configuration(ctx.Configuration, new ConfigurationReaderOptions() { SectionName = "Observability:Serilog" })
				.Enrich.WithSpan()
				.Enrich.FromLogContext();

			logConfig.WriteTo.Console();

			logConfig.WriteTo.File(
				Path.Join(Statics.DefaultOutputDirectory, $"{Statics.AppType}_log.txt"),
				rollingInterval: RollingInterval.Day,
				retainedFileCountLimit: 2,
				shared: false,
				hooks: new CaptureFilePathHook());
		})
		.ConfigureServices((hostContext, services) =>
		{
			// CACHE
			services.AddSingleton<IMemoryCache, MemoryCache>();

			// IO
			services.AddSingleton<IFileHandling, IOWrapper>();

			// SETTINGS
			services.AddSingleton<ISettingsDb, SettingsDb>();
			services.AddSingleton<ISettingsService>((serviceProvider) =>
			{
				var settingService = new SettingsService(serviceProvider.GetService<ISettingsDb>(), serviceProvider.GetService<IMemoryCache>(), serviceProvider.GetService<IConfiguration>(), serviceProvider.GetService<IFileHandling>());
				var memCache = serviceProvider.GetService<IMemoryCache>();
				var fileHandler = serviceProvider.GetService<IFileHandling>();
				return new FileBasedSettingsService(serviceProvider.GetService<IConfiguration>(), settingService, memCache, fileHandler);
			});

			// PELOTON
			services.AddSingleton<IPelotonAuthApiClient, PelotonAuthApiClient>();
			services.AddSingleton<IPelotonApi, Peloton.ApiClient>();
			services.AddSingleton<IPelotonService, PelotonService>();

			// GARMIN
			services.AddSingleton<IGarminAuthenticationService, GarminAuthenticationService>();
			services.AddSingleton<IGarminUploader, GarminUploader>();
			services.AddSingleton<IGarminApiClient, Garmin.ApiClient>();
			services.AddSingleton<IGarminActivityEnrichmentService, GarminActivityEnrichmentService>();
			services.AddSingleton<IGarminDb, GarminDb>();
			services.AddSingleton<IGarminMergeDb, GarminMergeDb>();

			// RELEASE CHECKS
			services.AddGitHubReleaseChecker();

			// SYNC
			services.AddSingleton<ISyncStatusDb, SyncStatusDb>();
			services.AddSingleton<ISyncService, SyncService>();

			// CONVERT
			services.AddSingleton<IConverter, FitConverter>();
			services.AddSingleton<IConverter, TcxConverter>();
			services.AddSingleton<IConverter, JsonConverter>();

			// HTTP
			var config = new AppConfiguration();
			ConfigurationSetup.LoadConfigValues(hostContext.Configuration, config);

			services.AddHostedService<Startup>();
		});
}
