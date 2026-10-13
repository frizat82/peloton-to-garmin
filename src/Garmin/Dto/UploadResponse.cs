using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Garmin.Dto
{
	public class UploadResponse
	{
		public DetailedImportResult DetailedImportResult { get; set; }

		/// <summary>The new activity's ID, when Garmin processed the upload straight away.</summary>
		[JsonIgnore]
		public long? NewActivityId => DetailedImportResult?.Successes?.Select(s => s.InternalId).FirstOrDefault(id => id is not null);

		/// <summary>Garmin already has this file (code 202).</summary>
		[JsonIgnore]
		public bool IsDuplicate => DetailedImportResult?.Failures?.Any(f => f.Messages?.Any(m => m.Code == DuplicateCode) == true) == true;

		/// <summary>Why Garmin refused the upload, or null if it took it. A duplicate counts as refused.</summary>
		public string GetRejection()
		{
			var result = DetailedImportResult;
			if (result?.Successes?.Any() == true || result?.Failures?.Any() != true)
				return null;

			return string.Join("; ", result.Failures
				.SelectMany(f => f.Messages ?? new List<Messages>())
				.Select(m => m.Code == DuplicateCode ? "duplicate of an activity Garmin already has" : $"{m.Code} {m.Content}")
				.DefaultIfEmpty("upload failed"));
		}

		private const int DuplicateCode = 202;
	}

	public class DetailedImportResult
	{
		public string CreationDate { get; set; }
		public string FileName { get; set; }
		public int? FileSize { get; set; }
		public string IpAddress { get; set; }
		public int? ProcessingTime { get; set; }
		[JsonConverter(typeof(LenientLongConverter))]
		public long? UploadId { get; set; }
		public ICollection<Failure> Failures { get; set; }
		public ICollection<Success> Successes { get; set; }
	}

	public class Success
	{
		public string ExternalId { get; set; }
		[JsonConverter(typeof(LenientLongConverter))]
		public long? InternalId { get; set; }
		public ICollection<Messages> Messages { get; set; }
	}

	public class Failure
	{
		public string ExternalId { get; set; }
		//public int? InternalId { get; set; }
		public ICollection<Messages> Messages { get; set; }
	}

	public class Messages
	{
		public int Code { get; set; }
		public string Content { get; set; }
	}

	/// <summary>Reads an ID Garmin sends as a number, as text, or as something else entirely (read as null).</summary>
	public class LenientLongConverter : JsonConverter<long?>
	{
		public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
				return number;
			if (reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), out var parsed))
				return parsed;

			reader.Skip();
			return null;
		}

		public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
		{
			if (value is null) writer.WriteNullValue();
			else writer.WriteNumberValue(value.Value);
		}
	}
}
