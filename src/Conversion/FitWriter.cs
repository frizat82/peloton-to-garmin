using Dynastream.Fit;
using System.Collections.Generic;
using System.IO;

namespace Conversion;

/// <summary>
/// Writes FIT messages without corrupting the FIT SDK's shared state.
/// </summary>
/// <remarks>
/// The SDK reuses the last definition for a message whenever it covers the message's fields. When a message has
/// fewer fields than that definition (e.g. records without cadence after records with it), the SDK fills the
/// missing fields from its static <see cref="Profile"/> and leaves invalid values stored there. Every FIT file
/// read or merged later in the same process then sees those values, which made merges silently skip
/// cadence, power and resistance. Writing a new definition whenever the set of fields changes avoids that path.
/// </remarks>
public static class FitWriter
{
	/// <summary>
	/// True when the SDK's shared record definition holds values, i.e. something wrote FIT data without
	/// <see cref="Write"/> and later merges in this process will lose cadence, power and resistance.
	/// </summary>
	public static bool IsSdkProfileCorrupted()
	{
		var record = Profile.GetMesg(MesgNum.Record);
		return record.GetField(RecordMesg.FieldDefNum.Cadence).GetNumValues() > 0
			|| record.GetField(RecordMesg.FieldDefNum.Power).GetNumValues() > 0;
	}

	public static void Write(Stream stream, IEnumerable<Mesg> messages)
	{
		var encoder = new Encode(ProtocolVersion.V20);
		try
		{
			encoder.Open(stream);
			var written = new Dictionary<byte, MesgDefinition>();
			foreach (var mesg in messages)
			{
				var definition = new MesgDefinition(mesg);
				// Supports() means the written definition covers this message; equal field counts mean it has no extras.
				if (!written.TryGetValue(mesg.LocalNum, out var current)
					|| !current.Supports(definition)
					|| current.NumFields != definition.NumFields
					|| current.NumDevFields != definition.NumDevFields)
				{
					encoder.Write(definition);
					written[mesg.LocalNum] = definition;
				}
				encoder.Write(mesg);
			}
		}
		finally
		{
			encoder.Close();
		}
	}
}
