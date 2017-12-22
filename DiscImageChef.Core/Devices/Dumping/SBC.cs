﻿// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : SBC.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Core algorithms.
//
// --[ Description ] ----------------------------------------------------------
//
//     Dumps SCSI Block devices.
//
// --[ License ] --------------------------------------------------------------
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as
//     published by the Free Software Foundation, either version 3 of the
//     License, or (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//
//     You should have received a copy of the GNU General Public License
//     along with this program.  If not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2018 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using DiscImageChef.CommonTypes;
using DiscImageChef.Console;
using DiscImageChef.Core.Logging;
using DiscImageChef.Decoders.ATA;
using DiscImageChef.Decoders.SCSI;
using DiscImageChef.Devices;
using DiscImageChef.DiscImages;
using DiscImageChef.Filesystems;
using DiscImageChef.Filters;
using DiscImageChef.Metadata;
using Extents;
using Schemas;
using MediaType = DiscImageChef.CommonTypes.MediaType;
using TrackType = DiscImageChef.DiscImages.TrackType;

namespace DiscImageChef.Core.Devices.Dumping
{
    static class Sbc
    {
        internal static void Dump(Device dev, string devicePath, string outputPrefix, ushort retryPasses, bool force,
                                  bool dumpRaw, bool persistent, bool stopOnError, ref CICMMetadataType sidecar,
                                  ref MediaType dskType, bool opticalDisc, ref Resume resume,
                                  ref DumpLog dumpLog, Encoding encoding, Alcohol120 alcohol = null)
        {
            bool sense;
            ulong blocks;
            uint blockSize;
            uint logicalBlockSize;
            uint physicalBlockSize;
            byte scsiMediumType = 0;
            byte scsiDensityCode = 0;
            bool containsFloppyPage = false;
            const ushort SBC_PROFILE = 0x0001;
            DateTime start;
            DateTime end;
            double totalDuration = 0;
            double totalChkDuration = 0;
            double currentSpeed = 0;
            double maxSpeed = double.MinValue;
            double minSpeed = double.MaxValue;
            byte[] readBuffer;
            uint blocksToRead;
            bool aborted = false;
            System.Console.CancelKeyPress += (sender, e) => e.Cancel = aborted = true;

            dumpLog.WriteLine("Initializing reader.");
            Reader scsiReader = new Reader(dev, dev.Timeout, null, dumpRaw);
            blocks = scsiReader.GetDeviceBlocks();
            blockSize = scsiReader.LogicalBlockSize;
            if(scsiReader.FindReadCommand())
            {
                dumpLog.WriteLine("ERROR: Cannot find correct read command: {0}.", scsiReader.ErrorMessage);
                DicConsole.ErrorWriteLine("Unable to read medium.");
                return;
            }

            if(blocks != 0 && blockSize != 0)
            {
                blocks++;
                DicConsole.WriteLine("Media has {0} blocks of {1} bytes/each. (for a total of {2} bytes)", blocks,
                                     blockSize, blocks * (ulong)blockSize);
            }
            // Check how many blocks to read, if error show and return
            if(scsiReader.GetBlocksToRead())
            {
                dumpLog.WriteLine("ERROR: Cannot get blocks to read: {0}.", scsiReader.ErrorMessage);
                DicConsole.ErrorWriteLine(scsiReader.ErrorMessage);
                return;
            }

            blocksToRead = scsiReader.BlocksToRead;
            logicalBlockSize = blockSize;
            physicalBlockSize = scsiReader.PhysicalBlockSize;

            if(blocks == 0)
            {
                dumpLog.WriteLine("ERROR: Unable to read medium or empty medium present...");
                DicConsole.ErrorWriteLine("Unable to read medium or empty medium present...");
                return;
            }

            if(!opticalDisc)
            {
                sidecar.BlockMedia = new BlockMediaType[1];
                sidecar.BlockMedia[0] = new BlockMediaType();

                // All USB flash drives report as removable, even if the media is not removable
                if(!dev.IsRemovable || dev.IsUsb)
                {
                    if(dev.IsUsb)
                    {
                        dumpLog.WriteLine("Reading USB descriptors.");
                        sidecar.BlockMedia[0].USB = new USBType
                        {
                            ProductID = dev.UsbProductId,
                            VendorID = dev.UsbVendorId,
                            Descriptors = new DumpType
                            {
                                Image = outputPrefix + ".usbdescriptors.bin",
                                Size = dev.UsbDescriptors.Length,
                                Checksums = Checksum.GetChecksums(dev.UsbDescriptors).ToArray()
                            }
                        };
                        DataFile.WriteTo("SCSI Dump", sidecar.BlockMedia[0].USB.Descriptors.Image, dev.UsbDescriptors);
                    }

                    byte[] cmdBuf;
                    if(dev.Type == DeviceType.ATAPI)
                    {
                        dumpLog.WriteLine("Requesting ATAPI IDENTIFY PACKET DEVICE.");
                        sense = dev.AtapiIdentify(out cmdBuf, out _);
                        if(!sense)
                        {
                            sidecar.BlockMedia[0].ATA = new ATAType
                            {
                                Identify = new DumpType
                                {
                                    Image = outputPrefix + ".identify.bin",
                                    Size = cmdBuf.Length,
                                    Checksums = Checksum.GetChecksums(cmdBuf).ToArray()
                                }
                            };
                            DataFile.WriteTo("SCSI Dump", sidecar.BlockMedia[0].ATA.Identify.Image, cmdBuf);
                        }
                    }

                    sense = dev.ScsiInquiry(out cmdBuf, out _);
                    if(!sense)
                    {
                        dumpLog.WriteLine("Requesting SCSI INQUIRY.");
                        sidecar.BlockMedia[0].SCSI = new SCSIType
                        {
                            Inquiry = new DumpType
                            {
                                Image = outputPrefix + ".inquiry.bin",
                                Size = cmdBuf.Length,
                                Checksums = Checksum.GetChecksums(cmdBuf).ToArray()
                            }
                        };
                        DataFile.WriteTo("SCSI Dump", sidecar.BlockMedia[0].SCSI.Inquiry.Image, cmdBuf);

                        dumpLog.WriteLine("Reading SCSI Extended Vendor Page Descriptors.");
                        sense = dev.ScsiInquiry(out cmdBuf, out _, 0x00);
                        if(!sense)
                        {
                            byte[] pages = EVPD.DecodePage00(cmdBuf);

                            if(pages != null)
                            {
                                List<EVPDType> evpds = new List<EVPDType>();
                                foreach(byte page in pages)
                                {
                                    dumpLog.WriteLine("Requesting page {0:X2}h.", page);
                                    sense = dev.ScsiInquiry(out cmdBuf, out _, page);
                                    if(sense) continue;

                                    EVPDType evpd = new EVPDType
                                    {
                                        Image = $"{outputPrefix}.evpd_{page:X2}h.bin",
                                        Checksums = Checksum.GetChecksums(cmdBuf).ToArray(),
                                        Size = cmdBuf.Length
                                    };
                                    evpd.Checksums = Checksum.GetChecksums(cmdBuf).ToArray();
                                    DataFile.WriteTo("SCSI Dump", evpd.Image, cmdBuf);
                                    evpds.Add(evpd);
                                }

                                if(evpds.Count > 0) sidecar.BlockMedia[0].SCSI.EVPD = evpds.ToArray();
                            }
                        }

                        dumpLog.WriteLine("Requesting MODE SENSE (10).");
                        sense = dev.ModeSense10(out cmdBuf, out _, false, true, ScsiModeSensePageControl.Current,
                                                0x3F, 0xFF, 5, out _);
                        if(!sense || dev.Error)
                            sense = dev.ModeSense10(out cmdBuf, out _, false, true,
                                                    ScsiModeSensePageControl.Current, 0x3F, 0x00, 5, out _);

                        Modes.DecodedMode? decMode = null;

                        if(!sense && !dev.Error)
                            if(Modes.DecodeMode10(cmdBuf, dev.ScsiType).HasValue)
                            {
                                decMode = Modes.DecodeMode10(cmdBuf, dev.ScsiType);
                                sidecar.BlockMedia[0].SCSI.ModeSense10 = new DumpType
                                {
                                    Image = outputPrefix + ".modesense10.bin",
                                    Size = cmdBuf.Length,
                                    Checksums = Checksum.GetChecksums(cmdBuf).ToArray()
                                };
                                DataFile.WriteTo("SCSI Dump", sidecar.BlockMedia[0].SCSI.ModeSense10.Image, cmdBuf);
                            }

                        dumpLog.WriteLine("Requesting MODE SENSE (6).");
                        sense = dev.ModeSense6(out cmdBuf, out _, false, ScsiModeSensePageControl.Current, 0x3F,
                                               0x00, 5, out _);
                        if(sense || dev.Error)
                            sense = dev.ModeSense6(out cmdBuf, out _, false, ScsiModeSensePageControl.Current,
                                                   0x3F, 0x00, 5, out _);
                        if(sense || dev.Error) sense = dev.ModeSense(out cmdBuf, out _, 5, out _);

                        if(!sense && !dev.Error)
                            if(Modes.DecodeMode6(cmdBuf, dev.ScsiType).HasValue)
                            {
                                decMode = Modes.DecodeMode6(cmdBuf, dev.ScsiType);
                                sidecar.BlockMedia[0].SCSI.ModeSense = new DumpType
                                {
                                    Image = outputPrefix + ".modesense.bin",
                                    Size = cmdBuf.Length,
                                    Checksums = Checksum.GetChecksums(cmdBuf).ToArray()
                                };
                                DataFile.WriteTo("SCSI Dump", sidecar.BlockMedia[0].SCSI.ModeSense.Image, cmdBuf);
                            }

                        if(decMode.HasValue)
                        {
                            scsiMediumType = (byte)decMode.Value.Header.MediumType;
                            if(decMode.Value.Header.BlockDescriptors != null &&
                               decMode.Value.Header.BlockDescriptors.Length >= 1)
                                scsiDensityCode = (byte)decMode.Value.Header.BlockDescriptors[0].Density;

                            containsFloppyPage = decMode.Value.Pages.Aggregate(containsFloppyPage, (current, modePage) => current | (modePage.Page == 0x05));
                        }
                    }
                }
            }

            if(dskType == MediaType.Unknown)
                dskType = MediaTypeFromScsi.Get((byte)dev.ScsiType, dev.Manufacturer, dev.Model, scsiMediumType,
                                                scsiDensityCode, blocks, blockSize);


            dumpLog.WriteLine("Device reports {0} blocks ({1} bytes).", blocks, blocks * blockSize);
            dumpLog.WriteLine("Device can read {0} blocks at a time.", blocksToRead);
            dumpLog.WriteLine("Device reports {0} bytes per logical block.", blockSize);
            dumpLog.WriteLine("Device reports {0} bytes per physical block.", scsiReader.LongBlockSize);
            dumpLog.WriteLine("SCSI device type: {0}.", dev.ScsiType);
            dumpLog.WriteLine("SCSI medium type: {0}.", scsiMediumType);
            dumpLog.WriteLine("SCSI density type: {0}.", scsiDensityCode);

            if(dskType == MediaType.Unknown && dev.IsUsb && containsFloppyPage) dskType = MediaType.FlashDrive;

            DicConsole.WriteLine("Media identified as {0}", dskType);
            dumpLog.WriteLine("SCSI floppy mode page present: {0}.", containsFloppyPage);
            dumpLog.WriteLine("Media identified as {0}.", dskType);

            uint longBlockSize = scsiReader.LongBlockSize;

            if(dumpRaw)
                if(blockSize == longBlockSize)
                {
                    DicConsole.ErrorWriteLine(!scsiReader.CanReadRaw
                                                  ? "Device doesn't seem capable of reading raw data from media."
                                                  : "Device is capable of reading raw data but I've been unable to guess correct sector size.");

                    if(!force)
                    {
                        DicConsole
                            .ErrorWriteLine("Not continuing. If you want to continue reading cooked data when raw is not available use the force option.");
                        // TODO: Exit more gracefully
                        return;
                    }

                    DicConsole.ErrorWriteLine("Continuing dumping cooked data.");
                    dumpRaw = false;
                }
                else
                {
                    // Only a block will be read, but it contains 16 sectors and command expect sector number not block number
                    blocksToRead = (uint)(longBlockSize == 37856 ? 16 : 1);
                    DicConsole.WriteLine("Reading {0} raw bytes ({1} cooked bytes) per sector.", longBlockSize,
                                         blockSize * blocksToRead);
                    physicalBlockSize = longBlockSize;
                    blockSize = longBlockSize;
                }

            DicConsole.WriteLine("Reading {0} sectors at a time.", blocksToRead);

            string outputExtension = ".bin";
            if(opticalDisc && blockSize == 2048) outputExtension = ".iso";
            MhddLog mhddLog = new MhddLog(outputPrefix + ".mhddlog.bin", dev, blocks, blockSize, blocksToRead);
            IbgLog ibgLog = new IbgLog(outputPrefix + ".ibg", SBC_PROFILE);
            DataFile dumpFile = new DataFile(outputPrefix + outputExtension);

            start = DateTime.UtcNow;

            if(alcohol != null && !dumpRaw)
            {
                alcohol.AddSessions(new[] {new Session {StartTrack = 1, EndTrack = 1, SessionSequence = 1}});
                alcohol.AddTrack(20, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1);
                alcohol.SetExtension(outputExtension);
                alcohol.SetTrackSizes(1, (int)blockSize, 0, 0, (long)blocks);
                alcohol.SetTrackTypes(1, TrackType.Data, TrackSubchannelType.None);
            }

            DumpHardwareType currentTry = null;
            ExtentsULong extents = null;
            ResumeSupport.Process(true, dev.IsRemovable, blocks, dev.Manufacturer, dev.Model, dev.Serial,
                                  dev.PlatformId, ref resume, ref currentTry, ref extents);
            if(currentTry == null || extents == null)
                throw new Exception("Could not process resume file, not continuing...");

            dumpFile.Seek(resume.NextBlock, blockSize);
            if(resume.NextBlock > 0) dumpLog.WriteLine("Resuming from block {0}.", resume.NextBlock);

            for(ulong i = resume.NextBlock; i < blocks; i += blocksToRead)
            {
                if(aborted)
                {
                    currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                    dumpLog.WriteLine("Aborted!");
                    break;
                }

                if(blocks - i < blocksToRead) blocksToRead = (uint)(blocks - i);

#pragma warning disable RECS0018 // Comparison of floating point numbers with equality operator
                if(currentSpeed > maxSpeed && currentSpeed != 0) maxSpeed = currentSpeed;
                if(currentSpeed < minSpeed && currentSpeed != 0) minSpeed = currentSpeed;
#pragma warning restore RECS0018 // Comparison of floating point numbers with equality operator

                DicConsole.Write("\rReading sector {0} of {1} ({2:F3} MiB/sec.)", i, blocks, currentSpeed);

                sense = scsiReader.ReadBlocks(out readBuffer, i, blocksToRead, out double cmdDuration);
                totalDuration += cmdDuration;

                if(!sense && !dev.Error)
                {
                    mhddLog.Write(i, cmdDuration);
                    ibgLog.Write(i, currentSpeed * 1024);
                    dumpFile.Write(readBuffer);
                    extents.Add(i, blocksToRead, true);
                }
                else
                {
                    // TODO: Reset device after X errors
                    if(stopOnError) return; // TODO: Return more cleanly

                    // Write empty data
                    dumpFile.Write(new byte[blockSize * blocksToRead]);

                    for(ulong b = i; b < i + blocksToRead; b++) resume.BadBlocks.Add(b);

                    mhddLog.Write(i, cmdDuration < 500 ? 65535 : cmdDuration);

                    ibgLog.Write(i, 0);
                    dumpLog.WriteLine("Error reading {0} blocks from block {1}.", blocksToRead, i);
                }

                double newSpeed= (double)blockSize * blocksToRead / 1048576 / (cmdDuration / 1000);
                if(!double.IsInfinity(newSpeed)) currentSpeed = newSpeed;
                resume.NextBlock = i + blocksToRead;
            }

            end = DateTime.UtcNow;
            DicConsole.WriteLine();
            mhddLog.Close();
            ibgLog.Close(dev, blocks, blockSize, (end - start).TotalSeconds, currentSpeed * 1024,
                         blockSize * (double)(blocks + 1) / 1024 / (totalDuration / 1000), devicePath);
            dumpLog.WriteLine("Dump finished in {0} seconds.", (end - start).TotalSeconds);
            dumpLog.WriteLine("Average dump speed {0:F3} KiB/sec.",
                              (double)blockSize * (double)(blocks + 1) / 1024 / (totalDuration / 1000));

            #region Error handling
            if(resume.BadBlocks.Count > 0 && !aborted)
            {
                int pass = 0;
                bool forward = true;
                bool runningPersistent = false;

                repeatRetry:
                ulong[] tmpArray = resume.BadBlocks.ToArray();
                foreach(ulong badSector in tmpArray)
                {
                    if(aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        dumpLog.WriteLine("Aborted!");
                        break;
                    }

                    DicConsole.Write("\rRetrying sector {0}, pass {1}, {3}{2}", badSector, pass + 1,
                                     forward ? "forward" : "reverse",
                                     runningPersistent ? "recovering partial data, " : "");

                    sense = scsiReader.ReadBlock(out readBuffer, badSector, out double cmdDuration);
                    totalDuration += cmdDuration;

                    if(!sense && !dev.Error)
                    {
                        resume.BadBlocks.Remove(badSector);
                        extents.Add(badSector);
                        dumpFile.WriteAt(readBuffer, badSector, blockSize);
                        dumpLog.WriteLine("Correctly retried block {0} in pass {1}.", badSector, pass);
                    }
                    else if(runningPersistent) dumpFile.WriteAt(readBuffer, badSector, blockSize);
                }

                if(pass < retryPasses && !aborted && resume.BadBlocks.Count > 0)
                {
                    pass++;
                    forward = !forward;
                    resume.BadBlocks.Sort();
                    resume.BadBlocks.Reverse();
                    goto repeatRetry;
                }

                Modes.ModePage? currentModePage = null;
                byte[] md6;
                byte[] md10;

                if(!runningPersistent && persistent)
                {
                    if(dev.ScsiType == PeripheralDeviceTypes.MultiMediaDevice)
                    {
                        Modes.ModePage_01_MMC pgMmc =
                            new Modes.ModePage_01_MMC
                            {
                                PS = false,
                                ReadRetryCount = 255,
                                Parameter = 0x20
                            };
                        Modes.DecodedMode md = new Modes.DecodedMode
                        {
                            Header = new Modes.ModeHeader(),
                            Pages = new[]
                            {
                                new Modes.ModePage
                                {
                                    Page = 0x01,
                                    Subpage = 0x00,
                                    PageResponse = Modes.EncodeModePage_01_MMC(pgMmc)
                                }
                            }
                        };
                        md6 = Modes.EncodeMode6(md, dev.ScsiType);
                        md10 = Modes.EncodeMode10(md, dev.ScsiType);
                    }
                    else
                    {
                        Modes.ModePage_01 pg = new Modes.ModePage_01
                        {
                            PS = false,
                            AWRE = false,
                            ARRE = false,
                            TB = true,
                            RC = false,
                            EER = true,
                            PER = false,
                            DTE = false,
                            DCR = false,
                            ReadRetryCount = 255
                        };
                        Modes.DecodedMode md = new Modes.DecodedMode
                        {
                            Header = new Modes.ModeHeader(),
                            Pages = new[]
                            {
                                new Modes.ModePage
                                {
                                    Page = 0x01,
                                    Subpage = 0x00,
                                    PageResponse = Modes.EncodeModePage_01(pg)
                                }
                            }
                        };
                        md6 = Modes.EncodeMode6(md, dev.ScsiType);
                        md10 = Modes.EncodeMode10(md, dev.ScsiType);
                    }

                    dumpLog.WriteLine("Sending MODE SELECT to drive.");
                    sense = dev.ModeSelect(md6, out _, true, false, dev.Timeout, out _);
                    if(sense) sense = dev.ModeSelect10(md10, out _, true, false, dev.Timeout, out _);

                    runningPersistent = true;
                    if(!sense && !dev.Error)
                    {
                        pass--;
                        goto repeatRetry;
                    }
                }
                else if(runningPersistent && persistent && currentModePage.HasValue)
                {
                    Modes.DecodedMode md = new Modes.DecodedMode
                    {
                        Header = new Modes.ModeHeader(),
                        Pages = new[] {currentModePage.Value}
                    };
                    md6 = Modes.EncodeMode6(md, dev.ScsiType);
                    md10 = Modes.EncodeMode10(md, dev.ScsiType);

                    dumpLog.WriteLine("Sending MODE SELECT to drive.");
                    sense = dev.ModeSelect(md6, out _, true, false, dev.Timeout, out _);
                    if(sense) dev.ModeSelect10(md10, out _, true, false, dev.Timeout, out _);
                }

                DicConsole.WriteLine();
            }
            #endregion Error handling

            resume.BadBlocks.Sort();
            currentTry.Extents = ExtentsConverter.ToMetadata(extents);

            Checksum dataChk = new Checksum();
            dumpFile.Seek(0, SeekOrigin.Begin);
            blocksToRead = 500;

            dumpLog.WriteLine("Checksum starts.");
            for(ulong i = 0; i < blocks; i += blocksToRead)
            {
                if(aborted)
                {
                    dumpLog.WriteLine("Aborted!");
                    break;
                }

                if(blocks - i < blocksToRead) blocksToRead = (uint)(blocks - i);

                DicConsole.Write("\rChecksumming sector {0} of {1} ({2:F3} MiB/sec.)", i, blocks, currentSpeed);

                DateTime chkStart = DateTime.UtcNow;
                byte[] dataToCheck = new byte[blockSize * blocksToRead];
                dumpFile.Read(dataToCheck, 0, (int)(blockSize * blocksToRead));
                dataChk.Update(dataToCheck);
                DateTime chkEnd = DateTime.UtcNow;

                double chkDuration = (chkEnd - chkStart).TotalMilliseconds;
                totalChkDuration += chkDuration;

                double newSpeed = (double)blockSize * blocksToRead / 1048576 / (chkDuration / 1000);
                if(!double.IsInfinity(newSpeed)) currentSpeed = newSpeed;
            }

            DicConsole.WriteLine();
            dumpFile.Close();
            end = DateTime.UtcNow;
            dumpLog.WriteLine("Checksum finished in {0} seconds.", (end - start).TotalSeconds);
            dumpLog.WriteLine("Average checksum speed {0:F3} KiB/sec.",
                              (double)blockSize * (double)(blocks + 1) / 1024 / (totalChkDuration / 1000));

            PluginBase plugins = new PluginBase();
            plugins.RegisterAllPlugins(encoding);
            FiltersList filtersList = new FiltersList();
            Filter inputFilter = filtersList.GetFilter(outputPrefix + outputExtension);

            if(inputFilter == null)
            {
                DicConsole.ErrorWriteLine("Cannot open file just created, this should not happen.");
                return;
            }

            ImagePlugin imageFormat = ImageFormat.Detect(inputFilter);
            PartitionType[] xmlFileSysInfo = null;

            try { if(!imageFormat.OpenImage(inputFilter)) imageFormat = null; }
            catch { imageFormat = null; }

            if(imageFormat != null)
            {
                dumpLog.WriteLine("Getting partitions.");
                List<Partition> partitions = Partitions.GetAll(imageFormat);
                Partitions.AddSchemesToStats(partitions);
                dumpLog.WriteLine("Found {0} partitions.", partitions.Count);

                if(partitions.Count > 0)
                {
                    xmlFileSysInfo = new PartitionType[partitions.Count];
                    for(int i = 0; i < partitions.Count; i++)
                    {
                        xmlFileSysInfo[i] = new PartitionType
                        {
                            Description = partitions[i].Description,
                            EndSector = (int)(partitions[i].Start + partitions[i].Length - 1),
                            Name = partitions[i].Name,
                            Sequence = (int)partitions[i].Sequence,
                            StartSector = (int)partitions[i].Start,
                            Type = partitions[i].Type
                        };
                        List<FileSystemType> lstFs = new List<FileSystemType>();
                        dumpLog.WriteLine("Getting filesystems on partition {0}, starting at {1}, ending at {2}, with type {3}, under scheme {4}.",
                                          i, partitions[i].Start, partitions[i].End, partitions[i].Type,
                                          partitions[i].Scheme);

                        foreach(Filesystem plugin in plugins.PluginsList.Values)
                            try
                            {
                                if(!plugin.Identify(imageFormat, partitions[i])) continue;

                                plugin.GetInformation(imageFormat, partitions[i], out _);
                                lstFs.Add(plugin.XmlFSType);
                                Statistics.AddFilesystem(plugin.XmlFSType.Type);
                                dumpLog.WriteLine("Filesystem {0} found.", plugin.XmlFSType.Type);

                                switch(plugin.XmlFSType.Type) {
                                    case "Opera": dskType = MediaType.ThreeDO;
                                        break;
                                    case "PC Engine filesystem": dskType = MediaType.SuperCDROM2;
                                        break;
                                    case "Nintendo Wii filesystem": dskType = MediaType.WOD;
                                        break;
                                    case "Nintendo Gamecube filesystem": dskType = MediaType.GOD;
                                        break;
                                }
                            }
#pragma warning disable RECS0022 // A catch clause that catches System.Exception and has an empty body
                            catch
#pragma warning restore RECS0022 // A catch clause that catches System.Exception and has an empty body
                            {
                                //DicConsole.DebugWriteLine("Dump-media command", "Plugin {0} crashed", _plugin.Name);
                            }

                        if(lstFs.Count > 0) xmlFileSysInfo[i].FileSystems = lstFs.ToArray();
                    }
                }
                else
                {
                    dumpLog.WriteLine("Getting filesystem for whole device.");
                    xmlFileSysInfo = new PartitionType[1];
                    xmlFileSysInfo[0] = new PartitionType {EndSector = (int)(blocks - 1), StartSector = 0};
                    List<FileSystemType> lstFs = new List<FileSystemType>();

                    Partition wholePart =
                        new Partition {Name = "Whole device", Length = blocks, Size = blocks * blockSize};

                    foreach(Filesystem plugin in plugins.PluginsList.Values)
                        try
                        {
                            if(!plugin.Identify(imageFormat, wholePart)) continue;

                            plugin.GetInformation(imageFormat, wholePart, out _);
                            lstFs.Add(plugin.XmlFSType);
                            Statistics.AddFilesystem(plugin.XmlFSType.Type);
                            dumpLog.WriteLine("Filesystem {0} found.", plugin.XmlFSType.Type);

                            switch(plugin.XmlFSType.Type) {
                                case "Opera": dskType = MediaType.ThreeDO;
                                    break;
                                case "PC Engine filesystem": dskType = MediaType.SuperCDROM2;
                                    break;
                                case "Nintendo Wii filesystem": dskType = MediaType.WOD;
                                    break;
                                case "Nintendo Gamecube filesystem": dskType = MediaType.GOD;
                                    break;
                            }
                        }
#pragma warning disable RECS0022 // A catch clause that catches System.Exception and has an empty body
                        catch
#pragma warning restore RECS0022 // A catch clause that catches System.Exception and has an empty body
                        {
                            //DicConsole.DebugWriteLine("Create-sidecar command", "Plugin {0} crashed", _plugin.Name);
                        }

                    if(lstFs.Count > 0) xmlFileSysInfo[0].FileSystems = lstFs.ToArray();
                }
            }

            if(alcohol != null && !dumpRaw) alcohol.SetMediaType(dskType);

            if(opticalDisc)
            {
                sidecar.OpticalDisc[0].Checksums = dataChk.End().ToArray();
                sidecar.OpticalDisc[0].DumpHardwareArray = resume.Tries.ToArray();
                sidecar.OpticalDisc[0].Image = new ImageType
                {
                    format = "Raw disk image (sector by sector copy)",
                    Value = outputPrefix + outputExtension
                };
                // TODO: Implement layers
                //sidecar.OpticalDisc[0].Layers = new LayersType();
                sidecar.OpticalDisc[0].Sessions = 1;
                sidecar.OpticalDisc[0].Tracks = new[] {1};
                sidecar.OpticalDisc[0].Track = new Schemas.TrackType[1];
                sidecar.OpticalDisc[0].Track[0] = new Schemas.TrackType
                {
                    BytesPerSector = (int)blockSize,
                    Checksums = sidecar.OpticalDisc[0].Checksums,
                    EndSector = (long)(blocks - 1),
                    Image =
                        new ImageType
                        {
                            format = "BINARY",
                            offset = 0,
                            offsetSpecified = true,
                            Value = sidecar.OpticalDisc[0].Image.Value
                        },
                    Sequence = new TrackSequenceType {Session = 1, TrackNumber = 1},
                    Size = (long)(blocks * blockSize),
                    StartSector = 0
                };
                if(xmlFileSysInfo != null) sidecar.OpticalDisc[0].Track[0].FileSystemInformation = xmlFileSysInfo;
                switch(dskType)
                {
                    case MediaType.DDCD:
                    case MediaType.DDCDR:
                    case MediaType.DDCDRW:
                        sidecar.OpticalDisc[0].Track[0].TrackType1 = TrackTypeTrackType.ddcd;
                        break;
                    case MediaType.DVDROM:
                    case MediaType.DVDR:
                    case MediaType.DVDRAM:
                    case MediaType.DVDRW:
                    case MediaType.DVDRDL:
                    case MediaType.DVDRWDL:
                    case MediaType.DVDDownload:
                    case MediaType.DVDPRW:
                    case MediaType.DVDPR:
                    case MediaType.DVDPRWDL:
                    case MediaType.DVDPRDL:
                        sidecar.OpticalDisc[0].Track[0].TrackType1 = TrackTypeTrackType.dvd;
                        break;
                    case MediaType.HDDVDROM:
                    case MediaType.HDDVDR:
                    case MediaType.HDDVDRAM:
                    case MediaType.HDDVDRW:
                    case MediaType.HDDVDRDL:
                    case MediaType.HDDVDRWDL:
                        sidecar.OpticalDisc[0].Track[0].TrackType1 = TrackTypeTrackType.hddvd;
                        break;
                    case MediaType.BDROM:
                    case MediaType.BDR:
                    case MediaType.BDRE:
                    case MediaType.BDREXL:
                    case MediaType.BDRXL:
                        sidecar.OpticalDisc[0].Track[0].TrackType1 = TrackTypeTrackType.bluray;
                        break;
                }

                sidecar.OpticalDisc[0].Dimensions = Dimensions.DimensionsFromMediaType(dskType);
                Metadata.MediaType.MediaTypeToString(dskType, out string xmlDskTyp, out string xmlDskSubTyp);
                sidecar.OpticalDisc[0].DiscType = xmlDskTyp;
                sidecar.OpticalDisc[0].DiscSubType = xmlDskSubTyp;
            }
            else
            {
                sidecar.BlockMedia[0].Checksums = dataChk.End().ToArray();
                sidecar.BlockMedia[0].Dimensions = Dimensions.DimensionsFromMediaType(dskType);
                Metadata.MediaType.MediaTypeToString(dskType, out string xmlDskTyp, out string xmlDskSubTyp);
                sidecar.BlockMedia[0].DiskType = xmlDskTyp;
                sidecar.BlockMedia[0].DiskSubType = xmlDskSubTyp;
                // TODO: Implement device firmware revision
                sidecar.BlockMedia[0].Image = new ImageType
                {
                    format = "Raw disk image (sector by sector copy)",
                    Value = outputPrefix + ".bin"
                };
                if(!dev.IsRemovable || dev.IsUsb)
                    if(dev.Type == DeviceType.ATAPI) sidecar.BlockMedia[0].Interface = "ATAPI";
                    else if(dev.IsUsb) sidecar.BlockMedia[0].Interface = "USB";
                    else if(dev.IsFireWire) sidecar.BlockMedia[0].Interface = "FireWire";
                    else sidecar.BlockMedia[0].Interface = "SCSI";
                sidecar.BlockMedia[0].LogicalBlocks = (long)blocks;
                sidecar.BlockMedia[0].PhysicalBlockSize = (int)physicalBlockSize;
                sidecar.BlockMedia[0].LogicalBlockSize = (int)logicalBlockSize;
                sidecar.BlockMedia[0].Manufacturer = dev.Manufacturer;
                sidecar.BlockMedia[0].Model = dev.Model;
                sidecar.BlockMedia[0].Serial = dev.Serial;
                sidecar.BlockMedia[0].Size = (long)(blocks * blockSize);
                if(xmlFileSysInfo != null) sidecar.BlockMedia[0].FileSystemInformation = xmlFileSysInfo;

                if(dev.IsRemovable) sidecar.BlockMedia[0].DumpHardwareArray = resume.Tries.ToArray();
            }

            DicConsole.WriteLine();

            DicConsole.WriteLine("Took a total of {0:F3} seconds ({1:F3} processing commands, {2:F3} checksumming).",
                                 (end - start).TotalSeconds, totalDuration / 1000, totalChkDuration / 1000);
            DicConsole.WriteLine("Avegare speed: {0:F3} MiB/sec.",
                                 (double)blockSize * (double)(blocks + 1) / 1048576 / (totalDuration / 1000));
            DicConsole.WriteLine("Fastest speed burst: {0:F3} MiB/sec.", maxSpeed);
            DicConsole.WriteLine("Slowest speed burst: {0:F3} MiB/sec.", minSpeed);
            DicConsole.WriteLine("{0} sectors could not be read.", resume.BadBlocks.Count);
            DicConsole.WriteLine();

            if(!aborted)
            {
                DicConsole.WriteLine("Writing metadata sidecar");

                FileStream xmlFs = new FileStream(outputPrefix + ".cicm.xml", FileMode.Create);

                XmlSerializer xmlSer =
                    new XmlSerializer(typeof(CICMMetadataType));
                xmlSer.Serialize(xmlFs, sidecar);
                xmlFs.Close();
                if(alcohol != null && !dumpRaw) alcohol.Close();
            }

            Statistics.AddMedia(dskType, true);
        }
    }
}