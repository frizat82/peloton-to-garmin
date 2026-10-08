using Conversion;
using Dynastream.Fit;
using FluentAssertions;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnitTests.Conversion;

public class FitWriterTests
{
	[Test]
	public void Write_RecordsWithAndWithoutCadence_LeavesSdkProfileCleanAndRoundTrips()
	{
		var messages = new List<Mesg>();
		var ts = new DateTime(System.DateTime.UtcNow);
		for (int i = 0; i < 6; i++)
		{
			var record = new RecordMesg();
			record.SetTimestamp(new DateTime(ts.GetTimeStamp() + (uint)i));
			record.SetHeartRate(120);
			if (i < 3)
			{
				record.SetCadence(85);
				record.SetPower(200);
			}
			messages.Add(record);
		}

		using var stream = new MemoryStream();
		FitWriter.Write(stream, messages);

		FitWriter.IsSdkProfileCorrupted().Should().BeFalse();

		var decoded = new List<RecordMesg>();
		var decoder = new Decode();
		var broadcaster = new MesgBroadcaster();
		decoder.MesgEvent += broadcaster.OnMesg;
		decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
		broadcaster.RecordMesgEvent += (_, e) => decoded.Add((RecordMesg)e.mesg);
		decoder.Read(new MemoryStream(stream.ToArray()));

		decoded.Select(r => r.GetCadence()).Should().Equal(85, 85, 85, null, null, null);
		decoded.Select(r => r.GetHeartRate()).Should().AllBeEquivalentTo(120);
	}
}
