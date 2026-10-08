using Api.Contract;
using Common;
using Common.Dto;
using Common.Service;
using Garmin.Auth;

namespace Api.Service;

public interface ISettingsUpdaterService
{
	Task<ServiceResult<App>> UpdateAppSettingsAsync(App updatedAppSettings);
	Task<ServiceResult<SettingsPelotonGetResponse>> UpdatePelotonSettingsAsync(SettingsPelotonPostRequest updatedPelotonSettings);
	Task<ServiceResult<Format>> UpdateFormatSettingsAsync(Format updatedFormatSettings);
	Task<ServiceResult<SettingsGarminGetResponse>> UpdateGarminSettingsAsync(SettingsGarminPostRequest updatedGarminSettings);
}
public class SettingsUpdaterService : ISettingsUpdaterService
{
	private const string InvalidNumWorkoutsToDownloadMessage = "Number of workouts to download must be greater than 0 when Automatic Polling is enabled.";

	private readonly IFileHandling _fileHandler;
	private readonly ISettingsService _settingsService;
	private readonly IGarminAuthenticationService _garminAuthService;

	public SettingsUpdaterService(IFileHandling fileHandler, ISettingsService settingsService, IGarminAuthenticationService garminAuthService)
	{
		_fileHandler = fileHandler;
		_settingsService = settingsService;
		_garminAuthService = garminAuthService;
	}

	public async Task<ServiceResult<App>> UpdateAppSettingsAsync(App updatedAppSettings)
	{
		var result = new ServiceResult<App>();

		if (updatedAppSettings is null)
		{
			result.Successful = false;
			result.Error = new ServiceError() { Message = "Updated AppSettings must not be null or empty." };
			return result;
		}

		if (updatedAppSettings.EnablePolling && updatedAppSettings.PollingIntervalSeconds <= 0)
		{
			result.Successful = false;
			result.Error = new ServiceError() { Message = "Polling interval must be greater than 0 seconds when Automatic Syncing is enabled." };
			return result;
		}

		var settings = await _settingsService.GetSettingsAsync();

		if (updatedAppSettings.EnablePolling && settings.Peloton.NumWorkoutsToDownload <= 0)
		{
			result.Successful = false;
			result.Error = new ServiceError() { Message = InvalidNumWorkoutsToDownloadMessage };
			return result;
		}
		settings.App = updatedAppSettings;

		await _settingsService.UpdateSettingsAsync(settings);
		var updatedSettings = await _settingsService.GetSettingsAsync();

		result.Result = updatedSettings.App;
		return result;
	}

	public async Task<ServiceResult<Format>> UpdateFormatSettingsAsync(Format updatedFormatSettings)
	{
		var result = new ServiceResult<Format>();

		if (updatedFormatSettings is null)
		{
			result.Successful = false;
			result.Error = new ServiceError() { Message = "Updated Format Settings must not be null or empty." };
			return result;
		}

		var settings = await _settingsService.GetSettingsAsync();
		settings.Format = updatedFormatSettings;

		await _settingsService.UpdateSettingsAsync(settings);
		var updatedSettings = await _settingsService.GetSettingsAsync();

		result.Result = updatedSettings.Format;
		return result;
	}

	public async Task<ServiceResult<SettingsGarminGetResponse>> UpdateGarminSettingsAsync(SettingsGarminPostRequest updatedGarminSettings)
	{
		var result = new ServiceResult<SettingsGarminGetResponse>();

		if (updatedGarminSettings is null)
		{
			result.Successful = false;
			result.Error = new ServiceError() { Message = "Updated Garmin Settings must not be null or empty." };
			return result;
		}

		if (!string.IsNullOrWhiteSpace(updatedGarminSettings.Password)
			&& updatedGarminSettings.Password.Contains('\\'))
		{
			result.Successful = false;
			result.Error = new ServiceError() { Message = "P2G does not support the `\\` character in passwords." };
			return result;
		}

		var settings = await _settingsService.GetSettingsAsync();

		// A null password means "unchanged" (the UI never receives the saved password back).
		var newPassword = updatedGarminSettings.Password ?? settings.Garmin.Password;
		if (settings.Garmin.Password != newPassword || settings.Garmin.Email != updatedGarminSettings.Email)
			await _garminAuthService.SignOutAsync();

		settings.Garmin = updatedGarminSettings.Map();

		await _settingsService.UpdateSettingsAsync(settings);
		var updatedSettings = await _settingsService.GetSettingsAsync();

		result.Result = new SettingsGetResponse(updatedSettings).Garmin;
		return result;
	}

	public async Task<ServiceResult<SettingsPelotonGetResponse>> UpdatePelotonSettingsAsync(SettingsPelotonPostRequest updatedPelotonSettings)
	{
		var result = new ServiceResult<SettingsPelotonGetResponse>();

		if (updatedPelotonSettings is null)
		{
			result.Successful = false;
			result.Error = new ServiceError() { Message = "Updated PelotonSettings must not be null or empty." };
			return result;
		}

		if (!string.IsNullOrWhiteSpace(updatedPelotonSettings.Password)
			&& updatedPelotonSettings.Password.Contains('\\'))
		{
			result.Successful = false;
			result.Error = new ServiceError() { Message = "P2G does not support the `\\` character in passwords." };
			return result;
		}

		var settings = await _settingsService.GetSettingsAsync();

		if (updatedPelotonSettings.NumWorkoutsToDownload <= 0
			&& settings.App.EnablePolling)
		{
			result.Successful = false;
			result.Error = new ServiceError() { Message = InvalidNumWorkoutsToDownloadMessage };
			return result;
		}

		settings.Peloton = updatedPelotonSettings.Map();

		await _settingsService.UpdateSettingsAsync(settings);
		var updatedSettings = await _settingsService.GetSettingsAsync();

		result.Result = new SettingsGetResponse(updatedSettings).Peloton;
		return result;
	}
}
