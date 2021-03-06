﻿// Copyright(C) 2017 Shane Macaulay smacaulay@gmail.com
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or(at your option) any later version.
//
//This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.If not, see<http://www.gnu.org/licenses/>.

using inVtero.net.Support;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static inVtero.net.UnsafeHelp;
using static System.Console;
using ProtoBuf;
using static inVtero.net.Misc;
using System.Diagnostics;

namespace inVtero.net
{
    /// <summary>
    /// Scanner is the initial entry point into inVtero, the most basic and primary functionality
    /// 
    /// Scanner is a file based scanning class
    /// </summary>
    [ProtoContract(AsReferenceDefault = true, ImplicitFields = ImplicitFields.AllPublic)]
    public class Scanner 
    {
        // for diagnostic printf's
        int MAX_LINE_WIDTH = Console.WindowWidth;

        Vtero vtero;

        // using bag since it has the same collection interface as List
        [ProtoIgnore]
        public ConcurrentDictionary<long, DetectedProc> DetectedProcesses;
        [ProtoIgnore]
        public DetectedProc[] ScanForVMCSset;

        [ProtoIgnore]
        public uint HexScanDword;
        [ProtoIgnore]
        public ulong HexScanUlong;
        [ProtoIgnore]
        public bool Scan64;


        #region class instance variables
        public string Filename;
        public long FileSize;
        [ProtoIgnore]
        public ConcurrentBag<VMCS> HVLayer;
        public bool DumpVMCSPage;

        List<MemoryRun> Gaps;
        [ProtoIgnore]
        List<Func<int, long, bool>> CheckMethods;
        PTType scanMode;
        public PTType ScanMode
        {
            get { return scanMode; }
            set
            {
                scanMode = value;

                CheckMethods.Clear();

                if ((value & PTType.GENERIC) == PTType.GENERIC)
                    CheckMethods.Add(Generic);

                if ((value & PTType.Windows) == PTType.Windows)
                    CheckMethods.Add(Windows);

                if ((value & PTType.HyperV) == PTType.HyperV)
                    CheckMethods.Add(HV);

                if ((value & PTType.FreeBSD) == PTType.FreeBSD)
                    CheckMethods.Add(FreeBSD);

                if ((value & PTType.OpenBSD) == PTType.OpenBSD)
                    CheckMethods.Add(OpenBSD);

                if ((value & PTType.NetBSD) == PTType.NetBSD)
                    CheckMethods.Add(NetBSD);

#if TESTING
                if ((value & PTType.VALUE) == PTType.VALUE)
                    CheckMethods = null;
#endif

                if ((value & PTType.LinuxS) == PTType.LinuxS)
                    CheckMethods.Add(LinuxS);

                if ((value & PTType.VMCS) == PTType.VMCS)
                    CheckMethods.Add(VMCS);
            }
        }

#endregion

        Scanner()
        {
            DetectedProcesses = new ConcurrentDictionary<long, DetectedProc>();
            HVLayer = new ConcurrentBag<VMCS>();
            FileSize = 0;
            Gaps = new List<MemoryRun>();
            CheckMethods = new List<Func<int, long, bool>>();
        }

        public Scanner(string InputFile, Vtero vTero) : this()
        {
            Filename = InputFile;
            vtero = vTero;
        }

#if TESTONLY
        public static bool HexScan(List<long> FoundValueOffsets, long offset, long[] ValueBlock, int ValueReadCount)
        {
            if (Scan64)
                for (int i = 0; i < ValueReadCount; i++)
                {
                    if ((ulong)ValueBlock[i] == HexScanUlong)
                    {
                        long xoff = offset + (i * 8);
                        WriteColor($"Found Hex data @{offset} + {i * 8}");
                        FoundValueOffsets.Add(xoff);
                        return true;
                    }
                }
            else
                for (int i = 0; i < ValueReadCount; i++)
                {
                    if ((uint)(ValueBlock[i] & 0xffffffff) == HexScanDword)
                    {
                        long xoff = offset + (i * 8);

                        WriteColor($"Found Hex ({HexScanDword:x8}) data OFFSET {offset:X16} + {(i * 8):X} @{(offset + (i * 8)):X} i={i}");
                        WriteColor($"{ValueBlock[i]:X16} : {ValueBlock[i + 1]:X16} : {ValueBlock[i + 2]:X16} : {ValueBlock[i + 3]:X16}");
                        WriteColor($"{ValueBlock[i + 4]:X16} : {ValueBlock[i + 5]:X16} : {ValueBlock[i + 6]:X16} : {ValueBlock[i + 7]:X16}");
                        FoundValueOffsets.Add(xoff);
                        return true;
                    }
                    else if ((uint)(ValueBlock[i] >> 32) == HexScanDword)
                    {
                        long xoff = offset + (i * 8) + 4;
                        WriteColor($"Found Hex ({HexScanDword:x8}) data OFFSET {offset:X16} + {(i * 8):X} @{(offset + (i * 8)):X}");
                        WriteColor($"{ValueBlock[i]:X16} : {ValueBlock[i + 1]:X16} : {ValueBlock[i + 2]:X16} : {ValueBlock[i + 3]:X16}");
                        WriteColor($"{ValueBlock[i + 4]:X16} : {ValueBlock[i + 5]:X16} : {ValueBlock[i + 6]:X16} : {ValueBlock[i + 7]:X16}");
                        FoundValueOffsets.Add(xoff);
                        return true;
                    }
                }
            return false;
        }
#endif

        /// <summary>
        /// The VMCS scan is based on the LINK pointer, abort code and CR3 register
        /// We  later isolate the EPTP based on constraints for that pointer
        /// </summary>
        /// <param name="xoffset"></param>
        /// <returns>true if the page being scanned is a candidate</returns>
        public bool VMCS(int bo, long xoffset)
        {
            var RevID = (REVISION_ID)(block[bo + 0] & 0xffffffff);
            var Acode = (VMCS_ABORT)((block[bo + 0] >> 32) & 0x7fffffff);

            var KnownAbortCode = false;
            var KnownRevision = false;
            var Candidate = false;
            var LinkCount = 0;
            var Neg1 = -1;

            if (ScanForVMCSset == null)
                throw new NullReferenceException("Entered VMCS callback w/o having found any VMCS, this is a second pass Func");

            // this might be a bit micro-opt-pointless ;)
            KnownRevision = typeof(REVISION_ID).GetEnumValues().Cast<REVISION_ID>().Any(x => x == RevID);
            KnownAbortCode = typeof(VMCS_ABORT).GetEnumValues().Cast<VMCS_ABORT>().Any(x => x == Acode);

            // TODO: Relax link pointer check. Possible when VMCS is shadow, then the link pointer is configured, retest this detection/nesting etc..
            // Find a 64bit value for link ptr
            for (int l = 0; l < block.Length; l++)
            {
                if (block[bo + l] == Neg1)
                    LinkCount++;

                // too many
                if (LinkCount > 32)
                    return false;
            }
            // Currently, we expect to have 1 Link pointer at least
            if (LinkCount == 0 || !KnownAbortCode)
                return false;

            // curr width of line to screen
            Candidate = false;
            Parallel.For(0, ScanForVMCSset.Length, (v) =>
            {
                var ScanFor = ScanForVMCSset[v];

                for (int check = 1; check < block.Length; check++)
                {
                    if (block[bo + check] == ScanFor.CR3Value && Candidate == false)
                    {
                        var OutputList = new List<long>();
                        StringBuilder sb = null, sbRED = null;
                        byte[] shorted = null;
                        var curr_width = 0;

                        if (Vtero.VerboseOutput)
                        {
                            sb = new StringBuilder();
                            // reverse endianness for easy reading in hex dumps/editors
                            shorted = BitConverter.GetBytes(block[bo + check]);
                            Array.Reverse(shorted, 0, 8);
                            var Converted = BitConverter.ToUInt64(shorted, 0);

                            sbRED = new StringBuilder();
                            sbRED.Append($"Hypervisor: VMCS revision field: {RevID} [{((uint)RevID):X8}] abort indicator: {Acode} [{((int)Acode):X8}]{Environment.NewLine}");
                            sbRED.Append($"Hypervisor: {ScanFor.PageTableType} CR3 found [{ScanFor.CR3Value:X16})] byte-swapped: [{Converted:X16}] @ PAGE/File Offset = [{xoffset:X16}]");
                        }

                        for (int i = 0; i < block.Length; i++)
                        {
                            var value = block[bo + i];

                            var eptp = new EPTP(value);

                            // any good minimum size? 64kb?
                            if (block[bo + i] > 0
                            && block[bo + i] < FileSize
                            && eptp.IsFullyValidated()
                   //         && EPTP.IsValid(eptp.aEPTP) && EPTP.IsValid2(eptp.aEPTP) && EPTP.IsValidEntry(eptp.aEPTP)
                            && !OutputList.Contains(block[bo + i]))
                            {
                                Candidate = true;
                                OutputList.Add(block[bo + i]);

                                if (Vtero.VerboseOutput)
                                {
                                    var linefrag = $"[{i}][{block[bo + i]:X16}] ";

                                    if (curr_width + linefrag.Length > MAX_LINE_WIDTH)
                                    {
                                        sb.Append(Environment.NewLine);
                                        curr_width = 0;
                                    }
                                    sb.Append(linefrag);
                                    curr_width += linefrag.Length;
                                }

                            }
                        }
                        if (Candidate && Vtero.VerboseOutput)
                        {
                            WColor(ConsoleColor.Red, ConsoleColor.Black, sbRED.ToString().PadRight(WindowWidth));
                            WColor(ConsoleColor.DarkGreen, ConsoleColor.Black, sb.ToString().PadRight(WindowWidth));
                        }

                        // most VMWare I've scanned comes are using this layout
                        // we know VMWare well so ignore any other potential candidates // TODO: Constantly Verify assumption's 
                        if (RevID == REVISION_ID.VMWARE_NESTED && OutputList.Contains(block[bo + 14]))
                        {
                            var vmcsFound = new VMCS { dp = ScanFor, EPTP = block[bo + 14], gCR3 = ScanFor.CR3Value, Offset = xoffset };
                            HVLayer.Add(vmcsFound);
                        }
                        else
                            foreach (var entry in OutputList)
                                HVLayer.Add(new VMCS { dp = ScanFor, EPTP = entry, gCR3 = ScanFor.CR3Value, Offset = xoffset });
                    }
                }
            });
            return Candidate;
        }

        long[] LinuxSFirstPage;
        List<long[]> LinuxSFirstPages = new List<long[]>();

        /// <summary>
        /// The LinuxS check is a single pass state preserving scanner
        /// 
        /// This was created using kernel 3.19 as a baseline.  More to follow.
        /// 
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool LinuxS(int bo, long offset)
        {
            var Candidate = false;
            var group = -1;

            // The main observation on kern319 is the given set below of must-have offsets that are identical and 0x7f8 which is unique per process
            // Next is the behavior that uses entries in 2 directions from top down and bottom up 
            // i.e. 0x7f0 0x0 are the next expected values.
            // All others would be unset in the top level / base page
            //
            // Kernel should have only the magnificent entries
            // memcmp 0 ranges 8-7f0, 800-880, 888-c88, c98-e88, e90-ea0, ea8-ff0
            // after first (likely kernel) page table found, use it's lower 1/2 to validate other detected page tables
            // Linux was found (so far) to have a consistent kernel view.
            var kern319 = new Dictionary<int, bool> { [0x7f8] = false, [0x880] = true, [0xc90] = true, [0xe88] = true, [0xea0] = true, [0xff0] = true, [0xff8] = true };

            var Profiles = new List<Dictionary<int, bool>>();

            if (((block[bo + 0xFF] & 0xfff) == 0x067) &&
               ((block[bo + 0x110] & 0xfff) == 0x067) &&
               ((block[bo + 0x192] & 0xfff) == 0x067) &&
               ((block[bo + 0x1d1] & 0xfff) == 0x067) &&
               ((block[bo + 0x1d4] & 0xfff) == 0x067) &&
               ((block[bo + 0x1fe] & 0xfff) == 0x067) &&
               ((block[bo + 0x1ff] & 0xfff) == 0x067) 

               // this is the largest block of 0's 
               // just do this one to qualify
               //IsZero(block, 8, 0xe0)
               )

            if (
                    /*IsZero(block, 8,     0xE0) &&
                IsZero(block, 0x100, 0x10) &&*/
                IsZero(block, 0x111, 0x80) &&
                IsZero(block, 0x193, 0x3e) &&
                IsZero(block, 0x1D2, 0x02) &&
                IsZero(block, 0x1D5, 0x29))
            {
                // before we catalog this entry, check to see if we can put it in a group
                for (int i = 0; i < LinuxSFirstPages.Count(); i++)
                    if (EqualBytesLongUnrolled(block, LinuxSFirstPages[i], 0x100))
                        group = i;

                // if we haven't found anything yet, setup first page
                if (LinuxSFirstPage == null)
                {
                    LinuxSFirstPage = block;
                    LinuxSFirstPages.Add(block);
                    group = 0;
                }

                // load DP 
                var dp = new DetectedProc { CR3Value = offset, FileOffset = offset, Diff = 0, Mode = 2, Group = group, PageTableType = PTType.LinuxS, TrueOffset = TrueOffset };
                    for (int p = 0; p < 0x200; p++)
                    if (block[bo + p] != 0)
                        dp.TopPageTablePage.Add(p, block[bo + p]);

                if (Vtero.VerboseOutput)
                    WriteColor(ConsoleColor.Cyan, dp.ToString());

                DetectedProcesses.TryAdd(offset, dp);
                Candidate = true;
            }
            return Candidate;
        }

        /// <summary>
        /// TODO: NetBSD needs some analysis
        /// Will add more later, this check is a bit noisy, consider it alpha
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool NetBSD(int bo, long offset)
        {
            var Candidate = false;

            //var offset = CurrWindowBase + CurrMapBase;
            var shifted = (block[bo + 255] & 0xFFFFFFFFF000);
            var diff = offset - shifted;


            if (((block[bo + 511] & 0xf3) == 0x63) && ((0x63 == (block[bo + 320] & 0xf3)) || (0x63 == (block[bo + 256] & 0xf3))))
            {
                if (((block[bo + 255] & 0xf3) == 0x63) && (0 == (block[bo + 255] & 0x7FFF000000000000)))
                {
                    if (!DetectedProcesses.ContainsKey(offset))
                    {
                        var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 2, PageTableType = PTType.NetBSD, TrueOffset = TrueOffset };
                        for (int p = 0; p < 0x200; p++)
                        {
                            if (block[bo + p] != 0)
                                dp.TopPageTablePage.Add(p, block[bo + p]);
                        }

                        DetectedProcesses.TryAdd(offset, dp);
                        if (Vtero.VerboseOutput)
                            WriteColor(ConsoleColor.Cyan, ConsoleColor.Black, dp.ToString());
                        Candidate = true;
                    }
                }
            }
            return Candidate;
        }

        /*   OpenBSD /src/sys/arch/amd64/include/pmap.h
#define L4_SLOT_PTE		255
#define L4_SLOT_KERN		256
#define L4_SLOT_KERNBASE	511
#define L4_SLOT_DIRECT		510
        */
        /// <summary>
        /// Slightly better check then NetBSD so I guess consider it beta!
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool OpenBSD(int bo, long offset)
        {
            var Candidate = false;

            //var offset = CurrWindowBase + CurrMapBase;
            var shifted = (block[bo + 255] & 0xFFFFFFFFF000);
            var diff = offset - shifted;

            if (((block[bo + 510] & 0xf3) == 0x63) && (0x63 == (block[bo + 256] & 0xf3)) && (0x63 == (block[bo + 254] & 0xf3)))
            {
                if (((block[bo + 255] & 0xf3) == 0x63) && (0 == (block[bo + 255] & 0x7FFF000000000000)))
                {
                    if (!DetectedProcesses.ContainsKey(offset))
                    {
                        var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 2, PageTableType = PTType.OpenBSD, TrueOffset = TrueOffset };
                        for (int p = 0; p < 0x200; p++)
                        {
                            if (block[bo + p] != 0)
                                dp.TopPageTablePage.Add(p, block[bo + p]);
                        }

                        DetectedProcesses.TryAdd(offset, dp);
                        if (Vtero.VerboseOutput)
                            WriteColor(ConsoleColor.Cyan, ConsoleColor.Black, dp.ToString());
                        Candidate = true;
                    }
                }
            }
            return Candidate;
        }

        /// <summary>
        /// The FreeBSD check for process detection is good
        /// Consider it release quality ;) 
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool FreeBSD(int bo, long offset)
        {
            var Candidate = false;

            //var offset = CurrWindowBase + CurrMapBase;
            var shifted = (block[bo + 0x100] & 0xFFFFFFFFF000);
            var diff = offset - shifted;

            if (((block[bo] & 0xff) == 0x67) && (0x67 == (block[bo + 0xff] & 0xff)))
            {
                if (((block[bo + 0x100] & 0xff) == 0x63) && (0 == (block[bo + 0x100] & 0x7FFF000000000000)))
                {
                    if (!DetectedProcesses.ContainsKey(offset))
                    {
                        var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 2, PageTableType = PTType.FreeBSD, TrueOffset = TrueOffset };
                        for (int p = 0; p < 0x200; p++)
                        {
                            if (block[bo + p] != 0)
                                dp.TopPageTablePage.Add(p, block[bo + p]);
                        }

                        DetectedProcesses.TryAdd(offset, dp);
                        if (Vtero.VerboseOutput)
                            WriteColor(ConsoleColor.Cyan, ConsoleColor.Black, dp.ToString());
                        Candidate = true;
                    }
                }
            }
            return Candidate;
        }
        /// <summary>
        /// Naturally the Generic checker is fairly chatty but at least you can use it
        /// to find unknowns, we could use some more tunable values here to help select the
        /// best match, I currently use the value with the lowest diff, which can be correct
        /// 
        /// This will find a self pointer in the first memory run for a non-sparse memory dump.
        ///
        /// The calling code is expected to adjust offset around RUN gaps.
        ///
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool Generic(int x, long offset)
        {
            var Candidate = false;

            //var offset = CurrWindowBase + CurrMapBase;
            //long bestShift = long.MaxValue, bestDiff = long.MaxValue;
            //var bestOffset = long.MaxValue;
            var i = 0x1ff;

            if (((block[x] & 0xff) == 0x63) || (block[x] & 0xfdf) == 0x847)
            {
                do
                {
                    if (((block[x+i] & 0xff) == 0x63 || (block[x+i] & 0xff) == 0x67))
                    {
                        // we disqualify entries that have these bits configured
                        // 111 0101 1111 1111 0000 0000 0000 0000 0000 0000 0000 0000 0000 0100 1000 0000
                        if ((block[x+i] & 0x75FF000000000480) == 0)
                        {
                            var shifted = (block[x+i] & 0xFFFFFFFFF000);
                            if (shifted == offset)
                            {
                                var diff = offset - shifted;
                                // BUGBUG: Need to K-Means this or something cluster values to help detection of processes in sparse format
                                // this could be better 
                                var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 2, PageTableType = PTType.GENERIC, TrueOffset = TrueOffset };
                                for (int p = 0; p < 0x200; p++)
                                {
                                    if (block[x+p] != 0)
                                        dp.TopPageTablePage.Add(p, block[x+p]);
                                }
                                DetectedProcesses.TryAdd(offset, dp);
                                if (Vtero.DiagOutput)
                                    WriteColor(ConsoleColor.Cyan, ConsoleColor.Black, dp.ToString());
                                Candidate = true;
                            }
                        }
                    }
                    i--;
                } while (i > 0xFF);
            }
            // maybe some kernels keep more than 1/2 system memory 
            // wouldn't that be a bit greedy though!?
            return Candidate;
        }

        /// <summary>
        /// In some deployments Hyper-V was found to use a configuration as such
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool HV(int bo, long offset)
        {
            var Candidate = false;

            //var offset = CurrWindowBase + CurrMapBase;
            var shifted = (block[bo + 0x1fe] & 0xFFFFFFFFF000);
            var diff = offset - shifted;

            // detect mode 2, 2 seems good for most purposes and is more portable
            // maybe 0x3 is sufficient
            if (shifted != 0 && ((block[bo] & 0xfff) == 0x063) && ((block[bo + 0x1fe] & 0xff) == 0x63 || (block[bo + 0x1fe] & 0xff) == 0x67) && block[bo + 0x1ff] == 0)
            {
                // we disqualify entries that have these bits configured
                // 111 1111 1111 1111 0000 0000 0000 0000 0000 0000 0000 0000 0000 0100 1000 0000
                // 
                if (((ulong)block[bo + 0x1fe] & 0xFFFF000000000480) == 0)
                {
                    if (!DetectedProcesses.ContainsKey(offset))
                    {
                        var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 2, PageTableType = PTType.HyperV, TrueOffset = TrueOffset };
                        for (int p = 0; p < 0x200; p++)
                        {
                            if (block[bo + p] != 0)
                                dp.TopPageTablePage.Add(p, block[bo + p]);
                        }

                        DetectedProcesses.TryAdd(offset, dp);
                        if (Vtero.VerboseOutput)
                            WriteColor(ConsoleColor.Cyan, ConsoleColor.Black, dp.ToString());
                        Candidate = true;
                    }
                }
            }
            return Candidate;
        }

        /// <summary>
        /// This is the same check as the earlier process detection code from CSW and DefCon
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        public bool Windows(int bo, long offset)
        {
            var Candidate = false;
            // pre randomized kernel 10.16 anniversario edition
            const int SELF_PTR = 0x1ed;

            //var offset = CurrWindowBase + CurrMapBase;
            var shifted = (block[bo + SELF_PTR] & 0xFFFFFFFFF000);
            var diff = offset - shifted;

            // detect mode 2, 2 seems good for most purposes and is more portable
            // maybe 0x3 is sufficient
            if (((block[bo] & 0xfdf) == 0x847) && ((block[bo + SELF_PTR] & 0xff) == 0x63 || (block[bo + SELF_PTR] & 0xff) == 0x67))
            {
                // we disqualify entries that have these bits configured
                //111 1111 1111 1111 0000 0000 0000 0000 0000 0000 0000 0000 0000 0100 1000 0000
                if ((block[0x1ed] & 0x7FFF000000000480) == 0)
                {
#if MODE_1
                    if (!SetDiff)
                    {
                        FirstDiff = diff;
                        SetDiff = true;
                    }
#endif
                    if (!DetectedProcesses.ContainsKey(offset))
                    {
                        var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 2, PageTableType = PTType.Windows, TrueOffset = TrueOffset };
                        for (int p = 0; p < 0x200; p++)
                        {
                            if (block[bo + p] != 0)
                                dp.TopPageTablePage.Add(p, block[bo + p]);
                        }

                        DetectedProcesses.TryAdd(offset, dp);
                        if (Vtero.VerboseOutput)
                            WriteColor(ConsoleColor.Cyan, ConsoleColor.Black, dp.ToString());
                        Candidate = true;
                    }
                }
            }
            // mode 1 is implemented to hit on very few supported bits
            // developing a version close to this that will work for Linux
#region MODE 1 IS PRETTY LOOSE
#if MODE_1
            else
                /// detect MODE 1, we can probably get away with even just testing & 1, the valid bit
                //if (((block[0] & 3) == 3) && (block[0x1ed] & 3) == 3)		
                if ((block[0] & 1) == 1 && (block[0xf68 / 8] & 1) == 1)
            {
                // a possible kernel first PFN? should look somewhat valid... 
                if (!SetDiff)
                {
                    // I guess we could be attacked here too, the system kernel could be modified/hooked/bootkit enough 
                    // we'll see if we need to analyze this in the long run
                    // the idea of mode 1 is a very low bit-scan, but we also do not want to mess up FirstDiff
                    // these root entries are valid for all win64's for PTE/hyper/session space etc.
                    if ((block[0xf78 / 8] & 1) == 1 && (block[0xf80 / 8] & 1) == 1 && (block[0xff8 / 8] & 1) == 1 && (block[0xff0 / 8] == 0))
                    {
                        // W/O this we may see some false positives 
                        // however can remove if you feel aggressive
                        if (diff < FileSize && (offset > shifted ? (diff + shifted == offset) : (diff + offset == shifted)))
                        {
                            FirstDiff = diff;
                            SetDiff = true;
                        }
                    }
                }

                if (SetDiff &&
                    !(FirstDiff != diff) &&
                     (shifted < (FileSize + diff)
                     //|| shifted != 0
                     ))
                {
                    if (!DetectedProcesses.ContainsKey(offset))
                    {
                        var dp = new DetectedProc { CR3Value = shifted, FileOffset = offset, Diff = diff, Mode = 1, PageTableType = PTType.Windows };

                        DetectedProcesses.TryAdd(offset, dp);
                        WriteColor(dp);

                        Candidate = true;
                    }
                }
            }
#endif
#endregion
            return Candidate;
        }

        const int init_map_size = 64 * 1024 * 1024; // 64MB window's
        const int block_size = (1024 * 4)*512; // 2MB chunks
        const int block_count = block_size / 8; // long arrays

        // TODO: Stop using static's ? perf?
        static long TrueOffset;
        static long CurrMapBase;
        static long CurrWindowBase;
        static long mapSize = init_map_size;
        static long[] block = new long[block_count];
        static long[][] buffers = { new long[block_count], new long[block_count] };
        static int filled = 0;

        /// <summary>
        /// A simple memory mapped scan over the input provided in the constructor
        /// </summary>
        /// <param name="ExitAfter">Optionally stop checking or exit early after this many candidates.  0 does not exit early.</param>
        /// <returns></returns>
        public int Analyze(int ExitAfter = 0)
        {
            CurrWindowBase = 0;
            mapSize = init_map_size;
            long RunShift = 0;

            if (File.Exists(Filename))
            {
                using (var fs = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var mapName = Path.GetFileNameWithoutExtension(Filename) + DateTime.Now.ToBinary().ToString("X16");
                    using (var mmap =
                        MemoryMappedFile.CreateFromFile(fs,
                        mapName,
                        0,
                        MemoryMappedFileAccess.Read,
                        null,
                        HandleInheritability.Inheritable,
                        false))
                    {
                        if (FileSize == 0)
                            FileSize = new FileInfo(Filename).Length;

                        // TODO: Clean up all the shifts
                        while (CurrWindowBase < FileSize)
                        {

                            // TODO: Realisitically we should be min/fill mapSize to accomidate our scan time 
                            // one paralallel task marshaling time should be roughly equiv to the scan of that block

                            using (var reader = mmap.CreateViewAccessor(CurrWindowBase, mapSize, MemoryMappedFileAccess.Read))
                            {
                                CurrMapBase = 0;
                                //reader.ReadArray(CurrMapBase, buffers[filled], 0, block_count);
                                UnsafeHelp.ReadBytes(reader, CurrMapBase, ref buffers[filled], block_count);

                                while (CurrMapBase < mapSize)
                                {
                                    // setup buffers for parallel load/read
                                    block = buffers[filled];
                                    filled ^= 1;
                                    var CURR_BASES = CurrWindowBase + CurrMapBase;
                                    CurrMapBase += block_size;

#pragma warning disable HeapAnalyzerImplicitParamsRule // Array allocation for params parameter
                                    Parallel.Invoke(() =>
                                    Parallel.ForEach<Func<int, long, bool>>(CheckMethods, (check) =>
                                    {
                                        for (int bo = 0; bo < block_count; bo += 512)
                                        {
                                            // Adjust for known memory run / extents mappings.
                                            // Adjust TrueOffset is actually possibly used by check fn (TODO: CLEAN UP THE GLOBALS)
                                            var offset = TrueOffset = CURR_BASES + (bo * 8);

                                            var offset_pfn = offset >> MagicNumbers.PAGE_SHIFT;
                                            // next page, may be faster with larger chunks but it's simple to view 1 page at a time
                                            long IndexedOffset_pfn = 0;
                                            do
                                            {
                                                IndexedOffset_pfn = vtero.MemAccess.OffsetToMemIndex(offset_pfn + RunShift);
                                                if (IndexedOffset_pfn == -1)
                                                {
                                                    RunShift++;
                                                    continue;
                                                }
                                                if (IndexedOffset_pfn == -2)
                                                    break;

                                            } while (IndexedOffset_pfn < 0);

                                            // found shift, accumulate indexes
                                            offset_pfn += RunShift;
                                            IndexedOffset_pfn = IndexedOffset_pfn >> MagicNumbers.PAGE_SHIFT;

                                            // Calculate DIFF
                                            var diff_off_pfn = offset < IndexedOffset_pfn ? IndexedOffset_pfn - offset_pfn : offset_pfn - IndexedOffset_pfn;

                                            // Skew Offset
                                            offset += (diff_off_pfn << MagicNumbers.PAGE_SHIFT);


                                            ///// !!! DO CHECK !!!
                                            check(bo, offset);
                                        }

                                    }), () =>
                                        {
                                            if (CurrMapBase < mapSize)
                                            {
                                                var total_count_remain = ((mapSize - CurrMapBase) / 8);
                                                var read_in_count = total_count_remain > block_count ? block_count : total_count_remain;
                                                UnsafeHelp.ReadBytes(reader, CurrMapBase, ref buffers[filled], (int) read_in_count);
                                            }
                                        }
                                    );
                                    if (ExitAfter > 0 && (ExitAfter == DetectedProcesses.Count())) // || FoundValueOffsets.Count() >= ExitAfter))
                                        return DetectedProcesses.Count();
                                }
                            } // close current window

                            CurrWindowBase += CurrMapBase;

                            if (CurrWindowBase + mapSize > FileSize)
                                mapSize = FileSize - CurrWindowBase;

                            var progress = Convert.ToInt32(Convert.ToDouble(CurrWindowBase) / Convert.ToDouble(FileSize) * 100.0);
                            if (progress != ProgressBarz.Progress)
                                ProgressBarz.RenderConsoleProgress(progress);

                        }
                    }
                } // close map
            } // close stream
            return DetectedProcesses.Count();
        }

        static IEnumerable<long> MapScanFile(String File, long From, int ScanData, int Count)
        {
            List<long> rv = new List<long>();

            // TODO: These streams should be persistent across these calls right?
            // TODO: This path is only 1 time and pretty infrequent so far though 
            using (var fs = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var mapName = Path.GetFileNameWithoutExtension(File) + From.ToString("X16");
                using (var mmap =
                    MemoryMappedFile.CreateFromFile(fs, mapName, 0, MemoryMappedFileAccess.Read,
                    null, HandleInheritability.Inheritable, false))
                {
                    using (var reader = mmap.CreateViewAccessor(From, Count * 4, MemoryMappedFileAccess.Read))
                    {
                        var LocatedScanTarget = UnsafeHelp.ScanBytes(reader, ScanData, Count);
                        if (LocatedScanTarget.Count() > 0)
                        {
                            foreach (var ioff in LocatedScanTarget)
                            {
                                var target = From + ioff;

                                //WriteColor($"Found input @ {(target):X}");
                                rv.Add(target);
                                yield return target;
                            }
                        }
                    }

                }
            }
            yield break;
        }


        /// <summary>
        /// Scan for a class configured variable "HexScanDword"
        /// This is a specialized thing we are trying to avoid over scanning
        /// Turns out the physical memory run data maintained by the OS is typically very deep physically
        /// So in start-up we may use this depending on input file
        /// </summary>
        /// <param name="ExitAfter"></param>
        /// <returns></returns>
        public static IEnumerable<long> BackwardsValueScan(String Filename, int ScanFor, int ExitAfter = 0)
        {
            List<long> FoundValueOffsets = new List<long>();
            var FileSize = new FileInfo(Filename).Length;

            long ReadSize = 1024 * 1024 * 8;
            var ValueReadCount = (int)ReadSize / 4;
            var RevMapSize = ReadSize;

            var ShortFirstChunkSize = (int)(FileSize & (ReadSize - 1));
            var ShortFirstChunkBase = FileSize - ShortFirstChunkSize;

            if (ShortFirstChunkSize != 0)
            {
                var found = MapScanFile(Filename, ShortFirstChunkBase, ScanFor, ShortFirstChunkSize / 4);
                foreach (var offset in found)
                    yield return offset;

                if(ShortFirstChunkBase == 0)
                    yield break;
            }

            var RevCurrWindowBase = FileSize - ShortFirstChunkSize;

            RevCurrWindowBase -= RevMapSize;
            var ChunkCount = (FileSize / RevMapSize) + 1;

            bool StopRunning = false;

            long localOffset = ShortFirstChunkBase - ReadSize;

            for (long i = ChunkCount; i > 0; i--)
            {
                if (!StopRunning)
                {

                    if(Vtero.VerboseLevel > 1)
                        WriteColor($"Scanning From {localOffset:X} To {(localOffset + ReadSize):X} bytes");

                    var results = MapScanFile(Filename, localOffset, ScanFor, ValueReadCount);

                    foreach (var offset in results)
                        yield return offset;

                    CurrWindowBase += (1 * ReadSize);
                    CurrWindowBase  = CurrWindowBase > FileSize ? FileSize : CurrWindowBase;
                    var progress = Convert.ToInt32(Convert.ToDouble(CurrWindowBase) / Convert.ToDouble(FileSize) * 100.0);
                    if (progress != ProgressBarz.Progress)
                        ProgressBarz.RenderConsoleProgress(progress);

                    localOffset -= RevMapSize;
                    if (localOffset < 0 && !StopRunning)
                    {
                        localOffset = 0;
                        StopRunning = true;

                    }
                }
            }
            yield break;
        }
    }
}
