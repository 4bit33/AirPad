// Ported from code written before nullable reference types; not annotated.
#nullable disable
using System;

namespace AirPad.Core.Models
{
	public class RaopBuffer
	{
		public const int Length = 1024;

		public bool IsEmpty { get; set; }
		public ushort FirstSeqNum { get; set; }
		public ushort LastSeqNum { get; set; }

		public RaopBufferEntry[] Entries = new RaopBufferEntry[Length];

		public int BufferSize { get; set; }
		public byte[] Buffer { get; set; }
	}
}
