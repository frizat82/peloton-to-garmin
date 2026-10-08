using Common.Database;
using Common.Dto;
using Common.Dto.Garmin;
using Common.Dto.Peloton;
using Common.Observe;
using Common.Stateful;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Common.Service;

public class SettingsService : ISettingsService
{
	private static readonly ILogger _logger = LogContext.ForClass<SettingsService>();
	private static readonly object _lock = new object();
	private static readonly string PelotonApiAuthKey = "PelotonApiAuth";

	private readonly ISettingsDb _db;
	private readonly IMemoryCache _cache;
	private readonly IConfiguration _configurationLoader;
	private readonly IFileHandling _fileHandler;
	private readonly IReadOnlyList<EnvironmentOverride> _environmentOverrides;

	/// <summary>A P2G_FORMAT__* or P2G_NOTIFICATIONS__* environment variable that overrides one saved setting.</summary>
	private record EnvironmentOverride(string Section, PropertyInfo Property, object Value);

	public SettingsService(ISettingsDb db, IMemoryCache cache, IConfiguration configurationLoader, IFileHandling fileHandler)
	{
		_db = db;
		_cache = cache;
		_configurationLoader = configurationLoader;
		_fileHandler = fileHandler;
		_environmentOverrides = LoadEnvironmentOverrides(configurationLoader);
	}

	public async Task<Settings> GetSettingsAsync()
	{
		using var tracing = Tracing.Trace($"{nameof(SettingsService)}.{nameof(GetSettingsAsync)}");

		var settings = (await _db.GetSettingsAsync(1)) ?? new Settings(); // hardcode to admin user for now

		if (settings.Format is null)
			settings.Format = new Settings().Format;

		if (settings.Format.DeviceInfoSettings is null)
			settings.Format.DeviceInfoSettings = Format.DefaultDeviceInfoSettings;

		if (!settings.Format.DeviceInfoSettings.TryGetValue(WorkoutType.None, out var _))
			settings.Format.DeviceInfoSettings.Add(WorkoutType.None, Format.DefaultDeviceInfoSettings[WorkoutType.None]);

		settings.Notifications ??= new NotificationSettings();
		foreach (var o in _environmentOverrides)
			o.Property.SetValue(SectionOf(settings, o.Section), o.Value);

		return settings;
	}

	public async Task UpdateSettingsAsync(Settings updatedSettings)
	{
		using var tracing = Tracing.Trace($"{nameof(SettingsService)}.{nameof(UpdateSettingsAsync)}");

		var originalSettings = await _db.GetSettingsAsync(1) ?? new Settings(); // hardcode to admin user for now

		if (updatedSettings.Garmin.Password is null)
			updatedSettings.Garmin.Password = originalSettings.Garmin.Password;

		if (updatedSettings.Peloton.Password is null)
			updatedSettings.Peloton.Password = originalSettings.Peloton.Password;

		// Environment overrides are applied on read; keep the saved values for those keys rather than
		// saving the environment's values as if they had been chosen in the WebUI.
		originalSettings.Notifications ??= new NotificationSettings();
		updatedSettings.Notifications ??= new NotificationSettings();
		foreach (var o in _environmentOverrides)
			o.Property.SetValue(SectionOf(updatedSettings, o.Section), o.Property.GetValue(SectionOf(originalSettings, o.Section)));

		ClearPelotonApiAuthentication(originalSettings.Peloton.Email);
		ClearPelotonApiAuthentication(updatedSettings.Peloton.Email);

		await _db.UpsertSettingsAsync(1, updatedSettings); // hardcode to admin user for now
	}

	public IReadOnlyCollection<string> GetEnvironmentOverrides()
	{
		return _environmentOverrides.Select(o => $"{o.Section}.{o.Property.Name}").ToList();
	}

	private static bool IsSimpleType(Type type)
	{
		type = Nullable.GetUnderlyingType(type) ?? type;
		return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);
	}

	private static object SectionOf(Settings settings, string section) => section == nameof(Settings.Format) ? settings.Format : settings.Notifications;

	/// <summary>
	/// Reads the P2G_FORMAT__* and P2G_NOTIFICATIONS__* environment variables once. Only environment variables count
	/// (not configuration files), and only top-level values such as P2G_FORMAT__INCLUDETIMEINPOWERZONES; nested keys
	/// and values that can't be converted are ignored with a warning.
	/// </summary>
	private static IReadOnlyList<EnvironmentOverride> LoadEnvironmentOverrides(IConfiguration configuration)
	{
		var envProviders = (configuration as IConfigurationRoot)?.Providers
			.OfType<Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider>()
			.ToList<IConfigurationProvider>();
		if (envProviders is null || envProviders.Count == 0)
			return Array.Empty<EnvironmentOverride>();

		var environment = new ConfigurationRoot(envProviders);
		var overrides = new List<EnvironmentOverride>();
		foreach (var (section, type) in new[] { (nameof(Settings.Format), typeof(Format)), (nameof(Settings.Notifications), typeof(NotificationSettings)) })
		{
			foreach (var child in environment.GetSection(section).GetChildren())
			{
				var property = type.GetProperty(child.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
				if (property is null || !property.CanWrite || child.Value is null || !IsSimpleType(property.PropertyType))
				{
					_logger.Warning("Ignoring environment variable for {Section}:{Key}; only top-level {Section} settings can be overridden.", section, child.Key, section);
					continue;
				}

				try
				{
					overrides.Add(new EnvironmentOverride(section, property, child.Get(property.PropertyType)));
				}
				catch (Exception e)
				{
					_logger.Warning(e, "Ignoring environment variable for {Section}:{Key}: '{Value}' is not a valid value.", section, child.Key, child.Value);
				}
			}
		}
		return overrides;
	}

	public PelotonApiAuthentication GetPelotonApiAuthentication(string pelotonEmail)
	{
		using var tracing = Tracing.Trace($"{nameof(SettingsService)}.{nameof(GetPelotonApiAuthentication)}");

		lock (_lock)
		{
			var key = $"{PelotonApiAuthKey}:{pelotonEmail}";
			return _cache.Get<PelotonApiAuthentication>(key);
		}
	}

	public void SetPelotonApiAuthentication(PelotonApiAuthentication authentication)
	{
		using var tracing = Tracing.Trace($"{nameof(SettingsService)}.{nameof(SetPelotonApiAuthentication)}");

		lock (_lock)
		{
			var key = $"{PelotonApiAuthKey}:{authentication.Email}";
			_cache.Set(key, authentication, new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) });
		}
	}

	public void ClearPelotonApiAuthentication(string pelotonEmail)
	{
		using var tracing = Tracing.Trace($"{nameof(SettingsService)}.{nameof(ClearPelotonApiAuthentication)}");

		lock (_lock)
		{
			var key = $"{PelotonApiAuthKey}:{pelotonEmail}";
			_cache.Remove(key);
		}
	}

	public Task<AppConfiguration> GetAppConfigurationAsync()
	{
		var appConfiguration = new AppConfiguration();
		ConfigurationSetup.LoadConfigValues(_configurationLoader, appConfiguration);

		return Task.FromResult(appConfiguration);
	}

	public async Task<GarminDeviceInfo> GetCustomDeviceInfoAsync(Workout workout)
	{
		using var tracing = Tracing.Trace($"{nameof(SettingsService)}.{nameof(GetCustomDeviceInfoAsync)}");

		var workoutType = WorkoutType.None;
		if (workout is object)
			workoutType = workout.GetWorkoutType();

		GarminDeviceInfo userProvidedDeviceInfo = null;

		var settings = await GetSettingsAsync();

		if (settings?.Format?.DeviceInfoSettings is object)
		{
			settings.Format.DeviceInfoSettings.TryGetValue(workoutType, out userProvidedDeviceInfo);

			if (userProvidedDeviceInfo is null)
				settings.Format.DeviceInfoSettings.TryGetValue(WorkoutType.None, out userProvidedDeviceInfo);
		}

		if (userProvidedDeviceInfo is null)
		{
			Format.DefaultDeviceInfoSettings.TryGetValue(workoutType, out userProvidedDeviceInfo);

			if (userProvidedDeviceInfo is null)
				Format.DefaultDeviceInfoSettings.TryGetValue(WorkoutType.None, out userProvidedDeviceInfo);
		}

		return userProvidedDeviceInfo;
	}
}
