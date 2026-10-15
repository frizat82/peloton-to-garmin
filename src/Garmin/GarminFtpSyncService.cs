using Common.Dto;
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

	/// <summary>How long a confirmed FTP is trusted before Garmin is checked again, in case it was changed there.</summary>
	private static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(1);

	private readonly ISettingsService _settingsService;
	private readonly IGarminApiClient _apiClient;
	private readonly IGarminAuthenticationService _authService;

	private ushort? _confirmedFtp;
	private DateTime _confirmedAt;

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

		if (ftp == _confirmedFtp && DateTime.UtcNow - _confirmedAt < RecheckAfter)
			return;

		var auth = await _authService.GetGarminAuthenticationAsync();
		if (!auth.IsSignedIn())
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

		if (Math.Round(cycling.FunctionalThresholdPower ?? 0) != ftp)
		{
			_logger.Information("FTP sync: updating Garmin cycling FTP from {GarminFtp} to Peloton's {PelotonFtp}", cycling.FunctionalThresholdPower, ftp);
			SetZones(cycling, ftp.Value);
			await _apiClient.UpdatePowerZonesAsync(cycling, auth);
			_logger.Information("FTP sync: Garmin cycling FTP set to {Ftp}", ftp);
		}

		_confirmedFtp = ftp;
		_confirmedAt = DateTime.UtcNow;
	}

	private static void SetZones(GarminPowerZones zones, ushort ftp)
	{
		double Floor(int zone) => Math.Round(ftp * PowerZoneModel.Floors[zone - 1]);

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
