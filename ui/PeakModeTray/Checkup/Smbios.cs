using System;
using System.Collections.Generic;
using System.Text;

namespace PeakModeTray.Checkup
{
    // Parses the raw SMBIOS table from GetSystemFirmwareTable('RSMB').
    // Offsets from the DMTF SMBIOS reference specification (DSP0134):
    // type 3 System Enclosure, type 17 Memory Device.
    internal static class Smbios
    {
        private const int RawHeader = 8;   // RawSMBIOSData: 4 bytes of versions, DWORD Length

        public static SmbiosInfo Parse(byte[] raw)
        {
            if (raw.Length < RawHeader) return SmbiosInfo.Empty;
            int length = (int)Math.Min(BitConverter.ToUInt32(raw, 4), (uint)(raw.Length - RawHeader));
            var modules = new List<MemoryModule>();
            var chassis = new List<int>();

            int p = RawHeader, end = RawHeader + length;
            while (p + 4 <= end)
            {
                int type = raw[p], len = raw[p + 1];
                if (len < 4 || p + len > end) break;
                // String set after the formatted area: NUL-terminated strings,
                // ended by one more NUL (two NULs when there are no strings)
                var strings = new List<string>();
                int s = p + len, next;
                if (s + 1 < end && raw[s] == 0 && raw[s + 1] == 0) next = s + 2;
                else
                {
                    int start = s;
                    while (s < end)
                    {
                        if (raw[s] != 0) { s++; continue; }
                        strings.Add(Encoding.ASCII.GetString(raw, start, s - start).Trim());
                        s++;
                        if (s < end && raw[s] == 0) { s++; break; }
                        start = s;
                    }
                    next = s;
                }

                byte B(int off) => off < len ? raw[p + off] : (byte)0;
                int W(int off) => off + 1 < len ? BitConverter.ToUInt16(raw, p + off) : 0;
                long D(int off) => off + 3 < len ? BitConverter.ToUInt32(raw, p + off) : 0;
                string Str(int off) { int i = B(off); return i > 0 && i <= strings.Count ? strings[i - 1] : ""; }

                if (type == 3)
                    chassis.Add(B(0x05) & 0x7F);
                else if (type == 17)
                {
                    int size = W(0x0C);
                    if (size != 0)                     // 0: slot is empty
                    {
                        int rated = W(0x15), conf = W(0x20);
                        if (rated == 0xFFFF) rated = (int)D(0x54);
                        if (conf == 0xFFFF) conf = (int)D(0x58);
                        string bank = Str(0x11), device = Str(0x10);
                        string slot = bank.Length > 0 ? bank : device;
                        modules.Add(new MemoryModule(slot, rated, conf, B(0x12), B(0x0E)));
                    }
                }
                else if (type == 127) break;           // end of table
                p = next;
            }
            return new SmbiosInfo(modules, chassis);
        }

        public static string MemoryTypeName(int t) => t switch
        {
            0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1C => "LPDDR2", 0x1D => "LPDDR3",
            0x1E => "LPDDR4", 0x22 => "DDR5", 0x23 => "LPDDR5", _ => "",
        };

        // SMBIOS form factor (not the CIM values Win32_PhysicalMemory uses: there SODIMM is 12)
        public static string FormFactorName(int f) => f switch
        {
            0x09 => "DIMM", 0x0D => "SODIMM", 0x0B => "soldered", _ => "",
        };

        // Chassis types that are portable (DSP0134 7.4.1)
        public static bool IsPortable(int chassis) => chassis is 8 or 9 or 10 or 14 or 30 or 31 or 32;
    }
}
