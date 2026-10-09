namespace Conversion
{
	public class ConvertStatus
	{
		public ConversionResult Result { get; set; }
		public string ErrorMessage { get; set; }

		/// <summary>
		/// True when this converter produces the file that gets uploaded to Garmin.
		/// </summary>
		public bool IsUploadFormat { get; set; }
	}

	public enum ConversionResult
	{
		Success = 0,
		Skipped = 10,
		Failed = 20,
	}
}
