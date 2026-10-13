using Common.Dto.Peloton;
using Common.Observe;
using Common.Service;
using Garmin.Auth;
using Garmin.Dto;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Garmin;

public interface IGarminFtpSyncService
{
	/// <summary>Updates Garmin's cycling FTP and power zones when they differ from the rider's Peloton FTP.</summary>
	Task SyncCyclingFtpAsync(UserData userData);
}

public class GarminFtpSyncService : IGarminFtpSyncService
{
	private static readonly ILogger _logger = LogContext.ForClass<GarminFtpSyncService>();

	/// <summary>Lowest point of zones 1-7 as a share of FTP. Peloton and Garmin's default % FTP zones both use these.</summary>
	private static readonly double[] ZoneFloors = { 0, .55, .75, .90, 1.05, 1.20, 1.50 };

	private readonly ISettingsService _settingsService;
	private readonly IGarminApiClient _apiClient;
	private readonly IGarminAuthenticationService _authService;

	public GarminFtpSyncService(ISettingsService settingsService, IGarminApiClient apiClient, IGarminAuthenticationService authService)
	{
		_settingsService = settingsService;
		_apiClient = apiClient;
		_authService = authService;
	}

	public async Task SyncCyclingFtpAsync(UserData userData)
	{
		using var tracing = Tracing.Trace($"{nameof(GarminFtpSyncService)}.{nameof(SyncCyclingFtpAsync)}");

		var settings = await _settingsService.GetSettingsAsync();
		if (!settings.Garmin.Upload || !settings.Garmin.SyncCyclingFtp)
			return;

		var ftp = userData?.GetMeasuredCyclingFtp();
		if (ftp is null)
		{
			_logger.Information("FTP sync: Peloton has no measured cycling FTP yet, leaving Garmin unchanged.");
			return;
		}

		var auth = await _authService.GetGarminAuthenticationAsync();
		if (auth.AuthStage == AuthStage.NeedMfaToken || auth.AuthStage == AuthStage.None)
		{
			_logger.Warning("FTP sync: not signed in to Garmin, skipping.");
			return;
		}

		var allZones = await _apiClient.GetPowerZonesAsync(auth);
		var cycling = allZones?.FirstOrDefault(z => z.Sport == GarminPowerZones.CyclingSport);
		if (cycling is null)
		{
			_logger.Warning("FTP sync: Garmin returned no cycling power zones, skipping.");
			return;
		}

		if (cycling.FunctionalThresholdPower == ftp)
			return;

		_logger.Information("FTP sync: updating Garmin cycling FTP from {GarminFtp} to Peloton's {PelotonFtp}", cycling.FunctionalThresholdPower, ftp);
		SetZones(cycling, ftp.Value);

		// Send every sport back unchanged apart from cycling, so other sports' zones can't be lost.
		await _apiClient.UpdatePowerZonesAsync(allZones, auth);
	}

	private static void SetZones(GarminPowerZones zones, ushort ftp)
	{
		double Floor(int zone) => Math.Round(ftp * ZoneFloors[zone - 1]);

		zones.FunctionalThresholdPower = ftp;
		zones.Zone1Floor = Floor(1);
		zones.Zone2Floor = Floor(2);
		zones.Zone3Floor = Floor(3);
		zones.Zone4Floor = Floor(4);
		zones.Zone5Floor = Floor(5);
		zones.Zone6Floor = Floor(6);
		zones.Zone7Floor = Floor(7);
	}
}
