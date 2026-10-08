using Common;
using Common.Dto;

namespace Api.Contract;

public class SettingsGetResponse
{
	public SettingsGetResponse()
	{
		App = new App();
		Format = new Format();
		Peloton = new SettingsPelotonGetResponse();
		Garmin = new SettingsGarminGetResponse();
		Notifications = new SettingsNotificationsGetResponse();
		EnvironmentOverrides = new List<string>();
	}

	public SettingsGetResponse(Settings settings)
	{
		App = settings.App;
		Format = settings.Format;

		Peloton = new SettingsPelotonGetResponse()
		{
			Email = settings.Peloton.Email,
			Password = null,
			ExcludeWorkoutTypes = settings.Peloton.ExcludeWorkoutTypes,
			NumWorkoutsToDownload = settings.Peloton.NumWorkoutsToDownload,
			IsPasswordSet = !string.IsNullOrEmpty(settings.Peloton.Password)
		};

		Garmin = new SettingsGarminGetResponse()
		{
			Email = settings.Garmin.Email,
			Password = null,
			TwoStepVerificationEnabled = settings.Garmin.TwoStepVerificationEnabled,
			FormatToUpload = settings.Garmin.FormatToUpload,
			Upload = settings.Garmin.Upload,
			IsPasswordSet = !string.IsNullOrEmpty(settings.Garmin.Password),
			EnrichGarminActivities = settings.Garmin.EnrichGarminActivities,
			MergeFitWithWatch = settings.Garmin.MergeFitWithWatch,
			ActivityMatchWindowSeconds = settings.Garmin.ActivityMatchWindowSeconds,
			Api = settings.Garmin.Api ?? new GarminApiSettings()
		};

		Notifications = new SettingsNotificationsGetResponse()
		{
			IsDiscordWebhookUrlSet = !string.IsNullOrWhiteSpace(settings.Notifications?.DiscordWebhookUrl),
			NotifyOnSuccess = settings.Notifications?.NotifyOnSuccess ?? false,
		};

		EnvironmentOverrides = new List<string>();
	}

	public App App { get; set; }
	public Format Format { get; set; }
	public SettingsPelotonGetResponse Peloton { get; set; }
	public SettingsGarminGetResponse Garmin { get; set; }
	public SettingsNotificationsGetResponse Notifications { get; set; }

	/// <summary>
	/// Settings set by environment variables, as "Section.Property" (e.g. "Notifications.DiscordWebhookUrl").
	/// They override the saved values, so the WebUI shows them as read-only.
	/// </summary>
	public ICollection<string> EnvironmentOverrides { get; set; }

	public bool IsSetByEnvironment(string section, string property) => EnvironmentOverrides?.Contains($"{section}.{property}") ?? false;
}

public class SettingsNotificationsGetResponse
{
	/// <summary>The webhook URL is a secret, so only whether one is set is returned.</summary>
	public bool IsDiscordWebhookUrlSet { get; set; }
	public bool NotifyOnSuccess { get; set; }
}

public class SettingsNotificationsPostRequest
{
	/// <summary>A new webhook URL to save; null or blank keeps the saved one.</summary>
	public string? DiscordWebhookUrl { get; set; }
	public bool RemoveDiscordWebhookUrl { get; set; }
	public bool NotifyOnSuccess { get; set; }
}

public class SettingsGarminGetResponse
{
	public bool IsPasswordSet { get; set; }
	public string? Email { get; set; }
	public string? Password { get; set; }
	public bool TwoStepVerificationEnabled { get; set; }
	public bool Upload { get; set; }
	public FileFormat FormatToUpload { get; set; }
	public bool EnrichGarminActivities { get; set; }
	public bool MergeFitWithWatch { get; set; }
	public int ActivityMatchWindowSeconds { get; set; } = 900;
	public GarminApiSettings Api { get; set; } = new GarminApiSettings();
}

public class SettingsGarminPostRequest
{
	public string? Email { get; set; }
	public string? Password { get; set; }
	public bool TwoStepVerificationEnabled { get; set; }
	public bool Upload { get; set; }
	public FileFormat FormatToUpload { get; set; }
	public bool EnrichGarminActivities { get; set; }
	public bool MergeFitWithWatch { get; set; }
	public int ActivityMatchWindowSeconds { get; set; } = 900;
	public GarminApiSettings Api { get; set; } = new GarminApiSettings();
}

public class SettingsPelotonGetResponse
{
	public SettingsPelotonGetResponse()
	{
		ExcludeWorkoutTypes = new List<WorkoutType>();
	}

	public bool IsConfigured => !string.IsNullOrWhiteSpace(Email) && IsPasswordSet;
	public bool IsPasswordSet { get; set; }
	public string? Email { get; set; }
	public string? Password { get; set; }
	public ICollection<WorkoutType> ExcludeWorkoutTypes { get; set; }
	public int NumWorkoutsToDownload { get; set; }
}

public class SettingsPelotonPostRequest
{
	public string? Email { get; set; }
	public string? Password { get; set; }
	public ICollection<WorkoutType>? ExcludeWorkoutTypes { get; set; }
	public int NumWorkoutsToDownload { get; set; }
}

public static class Mapping
{
	public static SettingsPelotonPostRequest Map(this SettingsPelotonGetResponse response)
	{
		return new SettingsPelotonPostRequest()
		{
			Email = response.Email,
			Password = response.Password,
			ExcludeWorkoutTypes = response.ExcludeWorkoutTypes,
			NumWorkoutsToDownload = response.NumWorkoutsToDownload
		};
	}

	public static PelotonSettings Map(this SettingsPelotonPostRequest request)
	{
		return new()
		{
			Email = request.Email,
			Password = request.Password,
			ExcludeWorkoutTypes = request.ExcludeWorkoutTypes,
			NumWorkoutsToDownload = request.NumWorkoutsToDownload,
		};
	}

	public static SettingsGarminPostRequest Map(this SettingsGarminGetResponse response)
	{
		return new SettingsGarminPostRequest()
		{
			Email = response.Email,
			Password = response.Password,
			TwoStepVerificationEnabled = response.TwoStepVerificationEnabled,
			FormatToUpload = response.FormatToUpload,
			Upload = response.Upload,
			EnrichGarminActivities = response.EnrichGarminActivities,
			MergeFitWithWatch = response.MergeFitWithWatch,
			ActivityMatchWindowSeconds = response.ActivityMatchWindowSeconds,
			Api = response.Api,
		};
	}

	public static GarminSettings Map(this SettingsGarminPostRequest request)
	{
		return new()
		{
			Email = request.Email,
			Password = request.Password,
			TwoStepVerificationEnabled = request.TwoStepVerificationEnabled,
			FormatToUpload = request.FormatToUpload,
			Upload = request.Upload,
			EnrichGarminActivities = request.EnrichGarminActivities,
			MergeFitWithWatch = request.MergeFitWithWatch,
			ActivityMatchWindowSeconds = request.ActivityMatchWindowSeconds,
			Api = request.Api,
		};
	}
}
