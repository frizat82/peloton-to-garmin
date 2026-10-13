using System.Text.Json.Serialization;

namespace Garmin.Dto;

/// <summary>One sport's FTP and power zones in Garmin Connect. Each floor is the zone's lowest wattage.</summary>
public class GarminPowerZones
{
	public const string CyclingSport = "CYCLING";

	[JsonPropertyName("sport")]
	public string Sport { get; set; }
	[JsonPropertyName("functionalThresholdPower")]
	public double? FunctionalThresholdPower { get; set; }
	[JsonPropertyName("zone1Floor")]
	public double? Zone1Floor { get; set; }
	[JsonPropertyName("zone2Floor")]
	public double? Zone2Floor { get; set; }
	[JsonPropertyName("zone3Floor")]
	public double? Zone3Floor { get; set; }
	[JsonPropertyName("zone4Floor")]
	public double? Zone4Floor { get; set; }
	[JsonPropertyName("zone5Floor")]
	public double? Zone5Floor { get; set; }
	[JsonPropertyName("zone6Floor")]
	public double? Zone6Floor { get; set; }
	[JsonPropertyName("zone7Floor")]
	public double? Zone7Floor { get; set; }
	[JsonPropertyName("userLocalTime")]
	public string UserLocalTime { get; set; }
}
