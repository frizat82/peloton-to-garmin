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
/// cadence, power and resistance. Writing an exact definition whenever the fields change avoids that path.
/// </remarks>
public static class FitWriter
{
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
				if (!written.TryGetValue(mesg.LocalNum, out var current) || !current.Supports(definition) || !definition.Supports(current))
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
