// Ported from code written before nullable reference types; not annotated.
#nullable disable
using System;
using System.Collections.Generic;
using System.Text;

namespace AirPad.Core.Models.Configs
{
    public class AirPlayReceiverConfig
    {
        public string Instance { get; set; }
        public ushort AirTunesPort { get; set; }
        public ushort AirPlayPort { get; set; }
        public string DeviceMacAddress { get; set; }
    }
}
