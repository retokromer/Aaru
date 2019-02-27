﻿// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : Verify.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Disk image plugins.
//
// --[ Description ] ----------------------------------------------------------
//
//     Verifies DiscImageChef format disk images.
//
// --[ License ] --------------------------------------------------------------
//
//     This library is free software; you can redistribute it and/or modify
//     it under the terms of the GNU Lesser General Public License as
//     published by the Free Software Foundation; either version 2.1 of the
//     License, or (at your option) any later version.
//
//     This library is distributed in the hope that it will be useful, but
//     WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//     Lesser General Public License for more details.
//
//     You should have received a copy of the GNU Lesser General Public
//     License along with this library; if not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2019 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DiscImageChef.Checksums;
using DiscImageChef.CommonTypes.Enums;
using DiscImageChef.Console;

namespace DiscImageChef.DiscImages
{
    public partial class DiscImageChef
    {
        public bool? VerifyMediaImage()
        {
            // This will traverse all blocks and check their CRC64 without uncompressing them
            DicConsole.DebugWriteLine("DiscImageChef format plugin", "Checking index integrity at {0}",
                                      header.indexOffset);
            imageStream.Position = (long)header.indexOffset;

            IndexHeader idxHeader = new IndexHeader();
            structureBytes = new byte[Marshal.SizeOf(idxHeader)];
            imageStream.Read(structureBytes, 0, structureBytes.Length);
            idxHeader = Helpers.Marshal.ByteArrayToStructureLittleEndian<IndexHeader>(structureBytes);

            if(idxHeader.identifier != BlockType.Index)
            {
                DicConsole.DebugWriteLine("DiscImageChef format plugin", "Incorrect index identifier");
                return false;
            }

            DicConsole.DebugWriteLine("DiscImageChef format plugin", "Index at {0} contains {1} entries",
                                      header.indexOffset, idxHeader.entries);

            structureBytes = new byte[Marshal.SizeOf(typeof(IndexEntry)) * idxHeader.entries];
            imageStream.Read(structureBytes, 0, structureBytes.Length);
            Crc64Context.Data(structureBytes, out byte[] verifyCrc);

            if(BitConverter.ToUInt64(verifyCrc, 0) != idxHeader.crc64)
            {
                DicConsole.DebugWriteLine("DiscImageChef format plugin", "Expected index CRC {0:X16} but got {1:X16}",
                                          idxHeader.crc64, BitConverter.ToUInt64(verifyCrc, 0));
                return false;
            }

            imageStream.Position -= structureBytes.Length;

            List<IndexEntry> vrIndex = new List<IndexEntry>();
            for(ushort i = 0; i < idxHeader.entries; i++)
            {
                IndexEntry entry = new IndexEntry();
                structureBytes = new byte[Marshal.SizeOf(entry)];
                imageStream.Read(structureBytes, 0, structureBytes.Length);
                entry = Helpers.Marshal.ByteArrayToStructureLittleEndian<IndexEntry>(structureBytes);
                DicConsole.DebugWriteLine("DiscImageChef format plugin",
                                          "Block type {0} with data type {1} is indexed to be at {2}", entry.blockType,
                                          entry.dataType, entry.offset);
                vrIndex.Add(entry);
            }

            // Read up to 1MiB at a time for verification
            const int VERIFY_SIZE = 1024 * 1024;

            foreach(IndexEntry entry in vrIndex)
            {
                imageStream.Position = (long)entry.offset;
                Crc64Context crcVerify;
                ulong        readBytes;
                byte[]       verifyBytes;

                switch(entry.blockType)
                {
                    case BlockType.DataBlock:
                        BlockHeader blockHeader = new BlockHeader();
                        structureBytes = new byte[Marshal.SizeOf(blockHeader)];
                        imageStream.Read(structureBytes, 0, structureBytes.Length);
                        blockHeader = Helpers.Marshal.ByteArrayToStructureLittleEndian<BlockHeader>(structureBytes);

                        crcVerify = new Crc64Context();
                        readBytes = 0;

                        DicConsole.DebugWriteLine("DiscImageChef format plugin",
                                                  "Verifying data block type {0} at position {1}", entry.dataType,
                                                  entry.offset);

                        while(readBytes + VERIFY_SIZE < blockHeader.cmpLength)
                        {
                            verifyBytes = new byte[VERIFY_SIZE];
                            imageStream.Read(verifyBytes, 0, verifyBytes.Length);
                            crcVerify.Update(verifyBytes);
                            readBytes += (ulong)verifyBytes.LongLength;
                        }

                        verifyBytes = new byte[blockHeader.cmpLength - readBytes];
                        imageStream.Read(verifyBytes, 0, verifyBytes.Length);
                        crcVerify.Update(verifyBytes);

                        verifyCrc = crcVerify.Final();

                        if(BitConverter.ToUInt64(verifyCrc, 0) != blockHeader.cmpCrc64)
                        {
                            DicConsole.DebugWriteLine("DiscImageChef format plugin",
                                                      "Expected block CRC {0:X16} but got {1:X16}",
                                                      blockHeader.cmpCrc64, BitConverter.ToUInt64(verifyCrc, 0));
                            return false;
                        }

                        break;
                    case BlockType.DeDuplicationTable:
                        DdtHeader ddtHeader = new DdtHeader();
                        structureBytes = new byte[Marshal.SizeOf(ddtHeader)];
                        imageStream.Read(structureBytes, 0, structureBytes.Length);
                        ddtHeader = Helpers.Marshal.ByteArrayToStructureLittleEndian<DdtHeader>(structureBytes);

                        crcVerify = new Crc64Context();
                        readBytes = 0;

                        DicConsole.DebugWriteLine("DiscImageChef format plugin",
                                                  "Verifying deduplication table type {0} at position {1}",
                                                  entry.dataType, entry.offset);

                        while(readBytes + VERIFY_SIZE < ddtHeader.cmpLength)
                        {
                            verifyBytes = new byte[readBytes];
                            imageStream.Read(verifyBytes, 0, verifyBytes.Length);
                            crcVerify.Update(verifyBytes);
                            readBytes += (ulong)verifyBytes.LongLength;
                        }

                        verifyBytes = new byte[ddtHeader.cmpLength - readBytes];
                        imageStream.Read(verifyBytes, 0, verifyBytes.Length);
                        crcVerify.Update(verifyBytes);

                        verifyCrc = crcVerify.Final();

                        if(BitConverter.ToUInt64(verifyCrc, 0) != ddtHeader.cmpCrc64)
                        {
                            DicConsole.DebugWriteLine("DiscImageChef format plugin",
                                                      "Expected DDT CRC {0:X16} but got {1:X16}", ddtHeader.cmpCrc64,
                                                      BitConverter.ToUInt64(verifyCrc, 0));
                            return false;
                        }

                        break;
                    case BlockType.TracksBlock:
                        TracksHeader trkHeader = new TracksHeader();
                        structureBytes = new byte[Marshal.SizeOf(trkHeader)];
                        imageStream.Read(structureBytes, 0, structureBytes.Length);
                        trkHeader = Helpers.Marshal.ByteArrayToStructureLittleEndian<TracksHeader>(structureBytes);

                        DicConsole.DebugWriteLine("DiscImageChef format plugin",
                                                  "Track block at {0} contains {1} entries", header.indexOffset,
                                                  trkHeader.entries);

                        structureBytes = new byte[Marshal.SizeOf(typeof(TrackEntry)) * trkHeader.entries];
                        imageStream.Read(structureBytes, 0, structureBytes.Length);
                        Crc64Context.Data(structureBytes, out verifyCrc);

                        if(BitConverter.ToUInt64(verifyCrc, 0) != trkHeader.crc64)
                        {
                            DicConsole.DebugWriteLine("DiscImageChef format plugin",
                                                      "Expected index CRC {0:X16} but got {1:X16}", trkHeader.crc64,
                                                      BitConverter.ToUInt64(verifyCrc, 0));
                            return false;
                        }

                        break;
                    default:
                        DicConsole.DebugWriteLine("DiscImageChef format plugin", "Ignored field type {0}",
                                                  entry.blockType);
                        break;
                }
            }

            return true;
        }

        public bool? VerifySector(ulong sectorAddress)
        {
            if(imageInfo.XmlMediaType != XmlMediaType.OpticalDisc) return null;

            byte[] buffer = ReadSectorLong(sectorAddress);
            return CdChecksums.CheckCdSector(buffer);
        }

        public bool? VerifySectors(ulong           sectorAddress, uint length, out List<ulong> failingLbas,
                                   out List<ulong> unknownLbas)
        {
            failingLbas = new List<ulong>();
            unknownLbas = new List<ulong>();

            // Right now only CompactDisc sectors are verifyable
            if(imageInfo.XmlMediaType != XmlMediaType.OpticalDisc)
            {
                for(ulong i = sectorAddress; i < sectorAddress + length; i++) unknownLbas.Add(i);

                return null;
            }

            byte[] buffer = ReadSectorsLong(sectorAddress, length);
            int    bps    = (int)(buffer.Length / length);
            byte[] sector = new byte[bps];
            failingLbas = new List<ulong>();
            unknownLbas = new List<ulong>();

            for(int i = 0; i < length; i++)
            {
                Array.Copy(buffer, i * bps, sector, 0, bps);
                bool? sectorStatus = CdChecksums.CheckCdSector(sector);

                switch(sectorStatus)
                {
                    case null:
                        unknownLbas.Add((ulong)i + sectorAddress);
                        break;
                    case false:
                        failingLbas.Add((ulong)i + sectorAddress);
                        break;
                }
            }

            if(unknownLbas.Count > 0) return null;

            return failingLbas.Count <= 0;
        }

        public bool? VerifySectors(ulong           sectorAddress, uint length, uint track, out List<ulong> failingLbas,
                                   out List<ulong> unknownLbas)
        {
            // Right now only CompactDisc sectors are verifyable
            if(imageInfo.XmlMediaType != XmlMediaType.OpticalDisc)
            {
                failingLbas = new List<ulong>();
                unknownLbas = new List<ulong>();

                for(ulong i = sectorAddress; i < sectorAddress + length; i++) unknownLbas.Add(i);

                return null;
            }

            byte[] buffer = ReadSectorsLong(sectorAddress, length, track);
            int    bps    = (int)(buffer.Length / length);
            byte[] sector = new byte[bps];
            failingLbas = new List<ulong>();
            unknownLbas = new List<ulong>();

            for(int i = 0; i < length; i++)
            {
                Array.Copy(buffer, i * bps, sector, 0, bps);
                bool? sectorStatus = CdChecksums.CheckCdSector(sector);

                switch(sectorStatus)
                {
                    case null:
                        unknownLbas.Add((ulong)i + sectorAddress);
                        break;
                    case false:
                        failingLbas.Add((ulong)i + sectorAddress);
                        break;
                }
            }

            if(unknownLbas.Count > 0) return null;

            return failingLbas.Count <= 0;
        }

        public bool? VerifySector(ulong sectorAddress, uint track)
        {
            if(imageInfo.XmlMediaType != XmlMediaType.OpticalDisc) return null;

            byte[] buffer = ReadSectorLong(sectorAddress, track);
            return CdChecksums.CheckCdSector(buffer);
        }
    }
}