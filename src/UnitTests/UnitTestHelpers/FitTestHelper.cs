using Dynastream.Fit;
using System;
using System.Collections.Generic;
using System.IO;

namespace UnitTests.UnitTestHelpers;

public static class FitTestHelper
{
	public static List<RecordMesg> DecodeRecords(byte[] fitBytes) => Decode<RecordMesg>(fitBytes, MesgNum.Record);

	public static List<SetMesg> DecodeSets(byte[] fitBytes) => Decode<SetMesg>(fitBytes, MesgNum.Set);

	private static List<T> Decode<T>(byte[] fitBytes, ushort mesgNum) where T : Mesg
	{
		var messages = new List<T>();
		var decoder = new Decode();
		var broadcaster = new MesgBroadcaster();
		decoder.MesgEvent += broadcaster.OnMesg;
		decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
		broadcaster.MesgEvent += (_, e) =>
		{
			if (e.mesg.Num == mesgNum)
				messages.Add((T)Activator.CreateInstance(typeof(T), e.mesg));
		};
		decoder.Read(new MemoryStream(fitBytes));
		return messages;
	}
}
