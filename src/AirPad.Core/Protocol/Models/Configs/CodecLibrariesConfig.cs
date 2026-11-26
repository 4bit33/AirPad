// Ported from code written before nullable reference types; not annotated.
#nullable disable
using System;
using System.Collections.Generic;
using System.Text;

namespace AirPad.Core.Models.Configs
{
    public class CodecLibrariesConfig
    {
        public string AACLibPath { get; set; }
        public string ALACLibPath { get; set; }
    }
}
