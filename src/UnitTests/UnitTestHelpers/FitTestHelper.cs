using Dynastream.Fit;
using System.Collections.Generic;
using System.IO;

namespace UnitTests.UnitTestHelpers;

public static class FitTestHelper
{
	public static List<RecordMesg> DecodeRecords(byte[] fitBytes)
	{
		var records = new List<RecordMesg>();
		var decoder = new Decode();
		var broadcaster = new MesgBroadcaster();
		decoder.MesgEvent += broadcaster.OnMesg;
		decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
		broadcaster.RecordMesgEvent += (_, e) => records.Add((RecordMesg)e.mesg);
		decoder.Read(new MemoryStream(fitBytes));
		return records;
	}
}
