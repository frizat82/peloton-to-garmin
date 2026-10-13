using System.Collections.Generic;

namespace Common.Dto;

/// <summary>Peloton's cycling power zones. Garmin's default % FTP zones use the same boundaries.</summary>
public static class PowerZoneModel
{
	/// <summary>Lowest point of zones 1-7 as a share of FTP. Output exactly on a floor belongs to the zone below.</summary>
	public static readonly IReadOnlyList<double> Floors = new[] { 0, .55, .75, .90, 1.05, 1.20, 1.50 };

	/// <summary>The zone (1-7) that <paramref name="watts"/> falls in for the given FTP.</summary>
	public static int ZoneOf(double watts, double ftp)
	{
		var zone = 1;
		while (zone < Floors.Count && watts > Floors[zone] * ftp)
			zone++;
		return zone;
	}
}
