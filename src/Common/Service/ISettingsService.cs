using Common.Dto;
using Common.Dto.Garmin;
using Common.Dto.Peloton;
using Common.Stateful;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Common.Service
{
	public interface ISettingsService
	{
		Task<Settings> GetSettingsAsync();
		Task UpdateSettingsAsync(Settings settings);

		/// <summary>
		/// Settings set by P2G_FORMAT__* or P2G_NOTIFICATIONS__* environment variables, as "Section.Property".
		/// These override the saved values and can't be changed from the WebUI.
		/// </summary>
		IReadOnlyCollection<string> GetEnvironmentOverrides();

		Task<AppConfiguration> GetAppConfigurationAsync();

		Task<GarminDeviceInfo> GetCustomDeviceInfoAsync(Workout workout);

		PelotonApiAuthentication GetPelotonApiAuthentication(string pelotonEmail);
		void SetPelotonApiAuthentication(PelotonApiAuthentication authentication);
		void ClearPelotonApiAuthentication(string pelotonEmail);
	}
}
