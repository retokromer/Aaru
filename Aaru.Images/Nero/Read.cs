﻿// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : Read.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Disk image plugins.
//
// --[ Description ] ----------------------------------------------------------
//
//     Reads Nero Burning ROM disc images.
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
// Copyright © 2011-2020 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Aaru.CommonTypes;
using Aaru.CommonTypes.Enums;
using Aaru.CommonTypes.Exceptions;
using Aaru.CommonTypes.Interfaces;
using Aaru.CommonTypes.Structs;
using Aaru.Console;
using Aaru.Decoders.CD;
using Aaru.Helpers;
using Session = Aaru.CommonTypes.Structs.Session;

namespace Aaru.DiscImages
{
    public sealed partial class Nero
    {
        public bool Open(IFilter imageFilter)
        {
            try
            {
                _imageStream = imageFilter.GetDataForkStream();
                var footerV1 = new NeroV1Footer();
                var footerV2 = new NeroV2Footer();

                _imageStream.Seek(-8, SeekOrigin.End);
                byte[] buffer = new byte[8];
                _imageStream.Read(buffer, 0, 8);
                footerV1.ChunkId          = BigEndianBitConverter.ToUInt32(buffer, 0);
                footerV1.FirstChunkOffset = BigEndianBitConverter.ToUInt32(buffer, 4);

                _imageStream.Seek(-12, SeekOrigin.End);
                buffer = new byte[12];
                _imageStream.Read(buffer, 0, 12);
                footerV2.ChunkId          = BigEndianBitConverter.ToUInt32(buffer, 0);
                footerV2.FirstChunkOffset = BigEndianBitConverter.ToUInt64(buffer, 4);

                AaruConsole.DebugWriteLine("Nero plugin", "imageStream.Length = {0}", _imageStream.Length);

                AaruConsole.DebugWriteLine("Nero plugin", "footerV1.ChunkID = 0x{0:X8} (\"{1}\")", footerV1.ChunkId,
                                           Encoding.ASCII.GetString(BigEndianBitConverter.GetBytes(footerV1.ChunkId)));

                AaruConsole.DebugWriteLine("Nero plugin", "footerV1.FirstChunkOffset = {0}", footerV1.FirstChunkOffset);

                AaruConsole.DebugWriteLine("Nero plugin", "footerV2.ChunkID = 0x{0:X8} (\"{1}\")", footerV2.ChunkId,
                                           Encoding.ASCII.GetString(BigEndianBitConverter.GetBytes(footerV2.ChunkId)));

                AaruConsole.DebugWriteLine("Nero plugin", "footerV2.FirstChunkOffset = {0}", footerV2.FirstChunkOffset);

                if(footerV1.ChunkId          == NERO_FOOTER_V1 &&
                   footerV1.FirstChunkOffset < (ulong)_imageStream.Length)
                    _imageNewFormat = false;
                else if(footerV2.ChunkId          == NERO_FOOTER_V2 &&
                        footerV2.FirstChunkOffset < (ulong)_imageStream.Length)
                    _imageNewFormat = true;
                else
                {
                    AaruConsole.DebugWrite("Nero plugin", "Nero version not recognized.");

                    return false;
                }

                if(_imageNewFormat)
                    _imageStream.Seek((long)footerV2.FirstChunkOffset, SeekOrigin.Begin);
                else
                    _imageStream.Seek(footerV1.FirstChunkOffset, SeekOrigin.Begin);

                bool   parsing        = true;
                ushort currentsession = 1;
                uint   currenttrack   = 1;

                Tracks      = new List<Track>();
                _trackIsrCs = new Dictionary<uint, byte[]>();

                _imageInfo.MediaType  = MediaType.CD;
                _imageInfo.Sectors    = 0;
                _imageInfo.SectorSize = 0;
                ulong currentSector = 0;

                while(parsing)
                {
                    byte[] chunkHeaderBuffer = new byte[8];

                    _imageStream.Read(chunkHeaderBuffer, 0, 8);
                    uint chunkId     = BigEndianBitConverter.ToUInt32(chunkHeaderBuffer, 0);
                    uint chunkLength = BigEndianBitConverter.ToUInt32(chunkHeaderBuffer, 4);

                    AaruConsole.DebugWriteLine("Nero plugin", "ChunkID = 0x{0:X8} (\"{1}\")", chunkId,
                                               Encoding.ASCII.GetString(BigEndianBitConverter.GetBytes(chunkId)));

                    AaruConsole.DebugWriteLine("Nero plugin", "ChunkLength = {0}", chunkLength);

                    switch(chunkId)
                    {
                        case NERO_CUE_V1:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"CUES\" chunk, parsing {0} bytes",
                                                       chunkLength);

                            var newCuesheetV1 = new NeroV1Cuesheet
                            {
                                ChunkId   = chunkId,
                                ChunkSize = chunkLength,
                                Entries   = new List<NeroV1CueEntry>()
                            };

                            byte[] tmpbuffer = new byte[8];

                            for(int i = 0; i < newCuesheetV1.ChunkSize; i += 8)
                            {
                                var entry = new NeroV1CueEntry();
                                _imageStream.Read(tmpbuffer, 0, 8);
                                entry.Mode        = tmpbuffer[0];
                                entry.TrackNumber = tmpbuffer[1];
                                entry.IndexNumber = tmpbuffer[2];
                                entry.Dummy       = BigEndianBitConverter.ToUInt16(tmpbuffer, 3);
                                entry.Minute      = tmpbuffer[5];
                                entry.Second      = tmpbuffer[6];
                                entry.Frame       = tmpbuffer[7];

                                AaruConsole.DebugWriteLine("Nero plugin", "Cuesheet entry {0}", (i / 8) + 1);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Mode = {1:X2}", (i / 8) + 1,
                                                           entry.Mode);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].TrackNumber = {1:X2}",
                                                           (i / 8) + 1, entry.TrackNumber);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].IndexNumber = {1:X2}",
                                                           (i / 8) + 1, entry.IndexNumber);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Dummy = {1:X4}", (i / 8) + 1,
                                                           entry.Dummy);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Minute = {1:X2}", (i / 8) + 1,
                                                           entry.Minute);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Second = {1:X2}", (i / 8) + 1,
                                                           entry.Second);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Frame = {1:X2}", (i / 8) + 1,
                                                           entry.Frame);

                                newCuesheetV1.Entries.Add(entry);
                            }

                            if(_neroCuesheetV1 is null)
                                _neroCuesheetV1 = newCuesheetV1;
                            else
                                _neroCuesheetV1.Entries.AddRange(newCuesheetV1.Entries);

                            break;
                        }

                        case NERO_CUE_V2:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"CUEX\" chunk, parsing {0} bytes",
                                                       chunkLength);

                            var newCuesheetV2 = new NeroV2Cuesheet
                            {
                                ChunkId   = chunkId,
                                ChunkSize = chunkLength,
                                Entries   = new List<NeroV2CueEntry>()
                            };

                            byte[] tmpbuffer = new byte[8];

                            for(int i = 0; i < newCuesheetV2.ChunkSize; i += 8)
                            {
                                var entry = new NeroV2CueEntry();
                                _imageStream.Read(tmpbuffer, 0, 8);
                                entry.Mode        = tmpbuffer[0];
                                entry.TrackNumber = tmpbuffer[1];
                                entry.IndexNumber = tmpbuffer[2];
                                entry.Dummy       = tmpbuffer[3];
                                entry.LbaStart    = BigEndianBitConverter.ToInt32(tmpbuffer, 4);

                                AaruConsole.DebugWriteLine("Nero plugin", "Cuesheet entry {0}", (i / 8) + 1);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Mode = 0x{1:X2}", (i / 8) + 1,
                                                           entry.Mode);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].TrackNumber = {1:X2}",
                                                           (i / 8) + 1, entry.TrackNumber);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].IndexNumber = {1:X2}",
                                                           (i / 8) + 1, entry.IndexNumber);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Dummy = {1:X2}", (i / 8) + 1,
                                                           entry.Dummy);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].LBAStart = {1}", (i / 8) + 1,
                                                           entry.LbaStart);

                                newCuesheetV2.Entries.Add(entry);
                            }

                            if(_neroCuesheetV2 is null)
                                _neroCuesheetV2 = newCuesheetV2;
                            else
                                _neroCuesheetV2.Entries.AddRange(newCuesheetV2.Entries);

                            break;
                        }

                        case NERO_DAO_V1:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"DAOI\" chunk, parsing {0} bytes",
                                                       chunkLength);

                            _neroDaov1 = new NeroV1Dao
                            {
                                ChunkId     = chunkId,
                                ChunkSizeBe = chunkLength
                            };

                            byte[] tmpbuffer = new byte[22];
                            _imageStream.Read(tmpbuffer, 0, 22);
                            _neroDaov1.ChunkSizeLe = BigEndianBitConverter.ToUInt32(tmpbuffer, 0);
                            _neroDaov1.Upc         = new byte[14];
                            Array.Copy(tmpbuffer, 4, _neroDaov1.Upc, 0, 14);
                            _neroDaov1.TocType    = BigEndianBitConverter.ToUInt16(tmpbuffer, 18);
                            _neroDaov1.FirstTrack = tmpbuffer[20];
                            _neroDaov1.LastTrack  = tmpbuffer[21];
                            _neroDaov1.Tracks     = new List<NeroV1DaoEntry>();

                            if(!_imageInfo.ReadableMediaTags.Contains(MediaTagType.CD_MCN))
                                _imageInfo.ReadableMediaTags.Add(MediaTagType.CD_MCN);

                            if(!_imageInfo.ReadableSectorTags.Contains(SectorTagType.CdTrackIsrc))
                                _imageInfo.ReadableSectorTags.Add(SectorTagType.CdTrackIsrc);

                            AaruConsole.DebugWriteLine("Nero plugin", "neroDAOV1.ChunkSizeLe = {0} bytes",
                                                       _neroDaov1.ChunkSizeLe);

                            AaruConsole.DebugWriteLine("Nero plugin", "neroDAOV1.UPC = \"{0}\"",
                                                       StringHandlers.CToString(_neroDaov1.Upc));

                            AaruConsole.DebugWriteLine("Nero plugin", "neroDAOV1.TocType = 0x{0:X4}",
                                                       _neroDaov1.TocType);

                            AaruConsole.DebugWriteLine("Nero plugin", "neroDAOV1.FirstTrack = {0}",
                                                       _neroDaov1.FirstTrack);

                            AaruConsole.DebugWriteLine("Nero plugin", "neroDAOV1.LastTrack = {0}",
                                                       _neroDaov1.LastTrack);

                            _upc = _neroDaov1.Upc;

                            tmpbuffer = new byte[30];

                            for(int i = 0; i < _neroDaov1.ChunkSizeBe - 22; i += 30)
                            {
                                var entry = new NeroV1DaoEntry();
                                _imageStream.Read(tmpbuffer, 0, 30);
                                entry.Isrc = new byte[12];
                                Array.Copy(tmpbuffer, 4, entry.Isrc, 0, 12);
                                entry.SectorSize = BigEndianBitConverter.ToUInt16(tmpbuffer, 12);
                                entry.Mode       = BitConverter.ToUInt16(tmpbuffer, 14);
                                entry.Unknown    = BigEndianBitConverter.ToUInt16(tmpbuffer, 16);
                                entry.Index0     = BigEndianBitConverter.ToUInt32(tmpbuffer, 18);
                                entry.Index1     = BigEndianBitConverter.ToUInt32(tmpbuffer, 22);
                                entry.EndOfTrack = BigEndianBitConverter.ToUInt32(tmpbuffer, 26);

                                AaruConsole.DebugWriteLine("Nero plugin", "Disc-At-Once entry {0}", (i / 32) + 1);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].ISRC = \"{1}\"", (i / 32) + 1,
                                                           StringHandlers.CToString(entry.Isrc));

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].SectorSize = {1}",
                                                           (i / 32) + 1, entry.SectorSize);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Mode = {1} (0x{2:X4})",
                                                           (i / 32) + 1, (DaoMode)entry.Mode, entry.Mode);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Unknown = 0x{1:X4}",
                                                           (i / 32) + 1, entry.Unknown);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Index0 = {1}", (i / 32) + 1,
                                                           entry.Index0);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Index1 = {1}", (i / 32) + 1,
                                                           entry.Index1);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].EndOfTrack = {1}",
                                                           (i / 32) + 1, entry.EndOfTrack);

                                _neroDaov1.Tracks.Add(entry);

                                if(entry.SectorSize > _imageInfo.SectorSize)
                                    _imageInfo.SectorSize = entry.SectorSize;

                                _trackIsrCs.Add(currenttrack, entry.Isrc);

                                if(currenttrack == 1)
                                    entry.Index0 = entry.Index1;

                                var neroTrack = new NeroTrack
                                {
                                    EndOfTrack = entry.EndOfTrack,
                                    Isrc       = entry.Isrc,
                                    Length     = entry.EndOfTrack - entry.Index0,
                                    Mode       = entry.Mode,
                                    Offset     = entry.Index0,
                                    SectorSize = entry.SectorSize,
                                    StartLba   = currentSector,
                                    Index0     = entry.Index0,
                                    Index1     = entry.Index1,
                                    Sequence   = currenttrack
                                };

                                neroTrack.Sectors = neroTrack.Length / entry.SectorSize;
                                _neroTracks.Add(currenttrack, neroTrack);

                                _imageInfo.Sectors += neroTrack.Sectors;
                                currentSector      += neroTrack.Sectors;

                                currenttrack++;
                            }

                            break;
                        }

                        case NERO_DAO_V2:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"DAOX\" chunk, parsing {0} bytes",
                                                       chunkLength);

                            _neroDaov2 = new NeroV2Dao
                            {
                                ChunkId     = chunkId,
                                ChunkSizeBe = chunkLength
                            };

                            byte[] tmpbuffer = new byte[22];
                            _imageStream.Read(tmpbuffer, 0, 22);
                            _neroDaov2.ChunkSizeLe = BigEndianBitConverter.ToUInt32(tmpbuffer, 0);
                            _neroDaov2.Upc         = new byte[14];
                            Array.Copy(tmpbuffer, 4, _neroDaov2.Upc, 0, 14);
                            _neroDaov2.TocType    = BigEndianBitConverter.ToUInt16(tmpbuffer, 18);
                            _neroDaov2.FirstTrack = tmpbuffer[20];
                            _neroDaov2.LastTrack  = tmpbuffer[21];
                            _neroDaov2.Tracks     = new List<NeroV2DaoEntry>();

                            if(!_imageInfo.ReadableMediaTags.Contains(MediaTagType.CD_MCN))
                                _imageInfo.ReadableMediaTags.Add(MediaTagType.CD_MCN);

                            if(!_imageInfo.ReadableSectorTags.Contains(SectorTagType.CdTrackIsrc))
                                _imageInfo.ReadableSectorTags.Add(SectorTagType.CdTrackIsrc);

                            _upc = _neroDaov2.Upc;

                            AaruConsole.DebugWriteLine("Nero plugin", "neroDAOV2.ChunkSizeLe = {0} bytes",
                                                       _neroDaov2.ChunkSizeLe);

                            AaruConsole.DebugWriteLine("Nero plugin", "neroDAOV2.UPC = \"{0}\"",
                                                       StringHandlers.CToString(_neroDaov2.Upc));

                            AaruConsole.DebugWriteLine("Nero plugin", "neroDAOV2.TocType = 0x{0:X4}",
                                                       _neroDaov2.TocType);

                            AaruConsole.DebugWriteLine("Nero plugin", "neroDAOV2.FirstTrack = {0}",
                                                       _neroDaov2.FirstTrack);

                            AaruConsole.DebugWriteLine("Nero plugin", "neroDAOV2.LastTrack = {0}",
                                                       _neroDaov2.LastTrack);

                            tmpbuffer = new byte[42];

                            for(int i = 0; i < _neroDaov2.ChunkSizeBe - 22; i += 42)
                            {
                                var entry = new NeroV2DaoEntry();
                                _imageStream.Read(tmpbuffer, 0, 42);
                                entry.Isrc = new byte[12];
                                Array.Copy(tmpbuffer, 4, entry.Isrc, 0, 12);
                                entry.SectorSize = BigEndianBitConverter.ToUInt16(tmpbuffer, 12);
                                entry.Mode       = BitConverter.ToUInt16(tmpbuffer, 14);
                                entry.Unknown    = BigEndianBitConverter.ToUInt16(tmpbuffer, 16);
                                entry.Index0     = BigEndianBitConverter.ToUInt64(tmpbuffer, 18);
                                entry.Index1     = BigEndianBitConverter.ToUInt64(tmpbuffer, 26);
                                entry.EndOfTrack = BigEndianBitConverter.ToUInt64(tmpbuffer, 34);

                                AaruConsole.DebugWriteLine("Nero plugin", "Disc-At-Once entry {0}", (i / 32) + 1);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].ISRC = \"{1}\"", (i / 32) + 1,
                                                           StringHandlers.CToString(entry.Isrc));

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].SectorSize = {1}",
                                                           (i / 32) + 1, entry.SectorSize);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Mode = {1} (0x{2:X4})",
                                                           (i / 32) + 1, (DaoMode)entry.Mode, entry.Mode);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Unknown = {1:X2}",
                                                           (i / 32) + 1, entry.Unknown);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Index0 = {1}", (i / 32) + 1,
                                                           entry.Index0);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Index1 = {1}", (i / 32) + 1,
                                                           entry.Index1);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].EndOfTrack = {1}",
                                                           (i / 32) + 1, entry.EndOfTrack);

                                _neroDaov2.Tracks.Add(entry);

                                if(entry.SectorSize > _imageInfo.SectorSize)
                                    _imageInfo.SectorSize = entry.SectorSize;

                                _trackIsrCs.Add(currenttrack, entry.Isrc);

                                if(currenttrack == 1)
                                    entry.Index0 = entry.Index1;

                                var neroTrack = new NeroTrack
                                {
                                    EndOfTrack = entry.EndOfTrack,
                                    Isrc       = entry.Isrc,
                                    Length     = entry.EndOfTrack - entry.Index0,
                                    Mode       = entry.Mode,
                                    Offset     = entry.Index0,
                                    SectorSize = entry.SectorSize,
                                    StartLba   = currentSector,
                                    Index0     = entry.Index0,
                                    Index1     = entry.Index1,
                                    Sequence   = currenttrack
                                };

                                neroTrack.Sectors = neroTrack.Length / entry.SectorSize;
                                _neroTracks.Add(currenttrack, neroTrack);

                                _imageInfo.Sectors += neroTrack.Sectors;
                                currentSector      += neroTrack.Sectors;

                                currenttrack++;
                            }

                            break;
                        }

                        case NERO_CDTEXT:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"CDTX\" chunk, parsing {0} bytes",
                                                       chunkLength);

                            _neroCdtxt = new NeroCdText
                            {
                                ChunkId   = chunkId,
                                ChunkSize = chunkLength,
                                Packs     = new List<NeroCdTextPack>()
                            };

                            byte[] tmpbuffer = new byte[18];

                            for(int i = 0; i < _neroCdtxt.ChunkSize; i += 18)
                            {
                                var entry = new NeroCdTextPack();
                                _imageStream.Read(tmpbuffer, 0, 18);

                                entry.PackType    = tmpbuffer[0];
                                entry.TrackNumber = tmpbuffer[1];
                                entry.PackNumber  = tmpbuffer[2];
                                entry.BlockNumber = tmpbuffer[3];
                                entry.Text        = new byte[12];
                                Array.Copy(tmpbuffer, 4, entry.Text, 0, 12);
                                entry.Crc = BigEndianBitConverter.ToUInt16(tmpbuffer, 16);

                                AaruConsole.DebugWriteLine("Nero plugin", "CD-TEXT entry {0}", (i / 18) + 1);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].PackType = 0x{1:X2}",
                                                           (i / 18) + 1, entry.PackType);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].TrackNumber = 0x{1:X2}",
                                                           (i / 18) + 1, entry.TrackNumber);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].PackNumber = 0x{1:X2}",
                                                           (i / 18) + 1, entry.PackNumber);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].BlockNumber = 0x{1:X2}",
                                                           (i / 18) + 1, entry.BlockNumber);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Text = \"{1}\"", (i / 18) + 1,
                                                           StringHandlers.CToString(entry.Text));

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].CRC = 0x{1:X4}", (i / 18) + 1,
                                                           entry.Crc);

                                _neroCdtxt.Packs.Add(entry);
                            }

                            break;
                        }

                        case NERO_TAO_V1:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"ETNF\" chunk, parsing {0} bytes",
                                                       chunkLength);

                            _neroTaov1 = new NeroV1Tao
                            {
                                ChunkId   = chunkId,
                                ChunkSize = chunkLength,
                                Tracks    = new List<NeroV1TaoEntry>()
                            };

                            byte[] tmpbuffer = new byte[20];

                            for(int i = 0; i < _neroTaov1.ChunkSize; i += 20)
                            {
                                var entry = new NeroV1TaoEntry();
                                _imageStream.Read(tmpbuffer, 0, 20);

                                entry.Offset   = BigEndianBitConverter.ToUInt32(tmpbuffer, 0);
                                entry.Length   = BigEndianBitConverter.ToUInt32(tmpbuffer, 4);
                                entry.Mode     = BigEndianBitConverter.ToUInt32(tmpbuffer, 8);
                                entry.StartLba = BigEndianBitConverter.ToUInt32(tmpbuffer, 12);
                                entry.Unknown  = BigEndianBitConverter.ToUInt32(tmpbuffer, 16);

                                AaruConsole.DebugWriteLine("Nero plugin", "Track-at-Once entry {0}", (i / 20) + 1);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Offset = {1}", (i / 20) + 1,
                                                           entry.Offset);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Length = {1} bytes",
                                                           (i / 20) + 1, entry.Length);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Mode = {1} (0x{2:X4})",
                                                           (i / 20) + 1, (DaoMode)entry.Mode, entry.Mode);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].StartLBA = {1}", (i / 20) + 1,
                                                           entry.StartLba);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Unknown = 0x{1:X4}",
                                                           (i / 20) + 1, entry.Unknown);

                                _neroTaov1.Tracks.Add(entry);

                                if(NeroTrackModeToBytesPerSector((DaoMode)entry.Mode) > _imageInfo.SectorSize)
                                    _imageInfo.SectorSize = NeroTrackModeToBytesPerSector((DaoMode)entry.Mode);

                                var neroTrack = new NeroTrack
                                {
                                    EndOfTrack     = entry.Offset + entry.Length,
                                    Isrc           = new byte[12],
                                    Length         = entry.Length,
                                    Mode           = entry.Mode,
                                    Offset         = entry.Offset,
                                    SectorSize     = NeroTrackModeToBytesPerSector((DaoMode)entry.Mode),
                                    StartLba       = entry.StartLba,
                                    UseLbaForIndex = true,
                                    Sequence       = currenttrack
                                };

                                neroTrack.Sectors =
                                    neroTrack.Length / NeroTrackModeToBytesPerSector((DaoMode)entry.Mode);

                                _neroTracks.Add(currenttrack, neroTrack);

                                _imageInfo.Sectors += neroTrack.Sectors;

                                currenttrack++;
                            }

                            break;
                        }

                        case NERO_TAO_V2:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"ETN2\" chunk, parsing {0} bytes",
                                                       chunkLength);

                            _neroTaov2 = new NeroV2Tao
                            {
                                ChunkId   = chunkId,
                                ChunkSize = chunkLength,
                                Tracks    = new List<NeroV2TaoEntry>()
                            };

                            byte[] tmpbuffer = new byte[32];

                            for(int i = 0; i < _neroTaov2.ChunkSize; i += 32)
                            {
                                var entry = new NeroV2TaoEntry();
                                _imageStream.Read(tmpbuffer, 0, 32);

                                entry.Offset   = BigEndianBitConverter.ToUInt64(tmpbuffer, 0);
                                entry.Length   = BigEndianBitConverter.ToUInt64(tmpbuffer, 8);
                                entry.Mode     = BigEndianBitConverter.ToUInt32(tmpbuffer, 16);
                                entry.StartLba = BigEndianBitConverter.ToUInt32(tmpbuffer, 20);
                                entry.Unknown  = BigEndianBitConverter.ToUInt32(tmpbuffer, 24);
                                entry.Sectors  = BigEndianBitConverter.ToUInt32(tmpbuffer, 28);

                                AaruConsole.DebugWriteLine("Nero plugin", "Track-at-Once entry {0}", (i / 32) + 1);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Offset = {1}", (i / 32) + 1,
                                                           entry.Offset);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Length = {1} bytes",
                                                           (i / 32) + 1, entry.Length);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Mode = {1} (0x{2:X4})",
                                                           (i / 32) + 1, (DaoMode)entry.Mode, entry.Mode);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].StartLBA = {1}", (i / 32) + 1,
                                                           entry.StartLba);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Unknown = 0x{1:X4}",
                                                           (i / 32) + 1, entry.Unknown);

                                AaruConsole.DebugWriteLine("Nero plugin", "\t _entry[{0}].Sectors = {1}", (i / 32) + 1,
                                                           entry.Sectors);

                                _neroTaov2.Tracks.Add(entry);

                                if(NeroTrackModeToBytesPerSector((DaoMode)entry.Mode) > _imageInfo.SectorSize)
                                    _imageInfo.SectorSize = NeroTrackModeToBytesPerSector((DaoMode)entry.Mode);

                                var neroTrack = new NeroTrack
                                {
                                    EndOfTrack     = entry.Offset + entry.Length,
                                    Isrc           = new byte[12],
                                    Length         = entry.Length,
                                    Mode           = entry.Mode,
                                    Offset         = entry.Offset,
                                    UseLbaForIndex = true
                                };

                                neroTrack.Sectors =
                                    neroTrack.Length / NeroTrackModeToBytesPerSector((DaoMode)entry.Mode);

                                neroTrack.SectorSize = NeroTrackModeToBytesPerSector((DaoMode)entry.Mode);
                                neroTrack.StartLba   = entry.StartLba;
                                neroTrack.Sequence   = currenttrack;
                                _neroTracks.Add(currenttrack, neroTrack);

                                _imageInfo.Sectors += neroTrack.Sectors;

                                currenttrack++;
                            }

                            break;
                        }

                        case NERO_SESSION:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"SINF\" chunk, parsing {0} bytes",
                                                       chunkLength);

                            byte[] tmpbuffer = new byte[4];
                            _imageStream.Read(tmpbuffer, 0, 4);
                            uint sessionTracks = BigEndianBitConverter.ToUInt32(tmpbuffer, 0);
                            _neroSessions.Add(currentsession, sessionTracks);

                            AaruConsole.DebugWriteLine("Nero plugin", "\tSession {0} has {1} tracks", currentsession,
                                                       sessionTracks);

                            currentsession++;

                            break;
                        }

                        case NERO_DISC_TYPE:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"MTYP\" chunk, parsing {0} bytes",
                                                       chunkLength);

                            _neroMediaTyp = new NeroMediaType
                            {
                                ChunkId   = chunkId,
                                ChunkSize = chunkLength
                            };

                            byte[] tmpbuffer = new byte[4];
                            _imageStream.Read(tmpbuffer, 0, 4);
                            _neroMediaTyp.Type = BigEndianBitConverter.ToUInt32(tmpbuffer, 0);

                            AaruConsole.DebugWriteLine("Nero plugin", "\tMedia type is {0} ({1})",
                                                       (NeroMediaTypes)_neroMediaTyp.Type, _neroMediaTyp.Type);

                            _imageInfo.MediaType = NeroMediaTypeToMediaType((NeroMediaTypes)_neroMediaTyp.Type);

                            break;
                        }

                        case NERO_DISC_INFO:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"DINF\" chunk, parsing {0} bytes",
                                                       chunkLength);

                            _neroDiscInfo = new NeroDiscInformation
                            {
                                ChunkId   = chunkId,
                                ChunkSize = chunkLength
                            };

                            byte[] tmpbuffer = new byte[4];
                            _imageStream.Read(tmpbuffer, 0, 4);
                            _neroDiscInfo.Unknown = BigEndianBitConverter.ToUInt32(tmpbuffer, 0);

                            AaruConsole.DebugWriteLine("Nero plugin", "\tneroDiscInfo.Unknown = 0x{0:X4} ({0})",
                                                       _neroDiscInfo.Unknown);

                            break;
                        }

                        case NERO_RELOCATION:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"RELO\" chunk, parsing {0} bytes",
                                                       chunkLength);

                            _neroRelo = new NeroReloChunk
                            {
                                ChunkId   = chunkId,
                                ChunkSize = chunkLength
                            };

                            byte[] tmpbuffer = new byte[4];
                            _imageStream.Read(tmpbuffer, 0, 4);
                            _neroRelo.Unknown = BigEndianBitConverter.ToUInt32(tmpbuffer, 0);

                            AaruConsole.DebugWriteLine("Nero plugin", "\tneroRELO.Unknown = 0x{0:X4} ({0})",
                                                       _neroRelo.Unknown);

                            break;
                        }

                        case NERO_TOC:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"TOCT\" chunk, parsing {0} bytes",
                                                       chunkLength);

                            _neroToc = new NeroTocChunk
                            {
                                ChunkId   = chunkId,
                                ChunkSize = chunkLength
                            };

                            byte[] tmpbuffer = new byte[2];
                            _imageStream.Read(tmpbuffer, 0, 2);
                            _neroToc.Unknown = BigEndianBitConverter.ToUInt16(tmpbuffer, 0);

                            AaruConsole.DebugWriteLine("Nero plugin", "\tneroTOC.Unknown = 0x{0:X4} ({0})",
                                                       _neroToc.Unknown);

                            break;
                        }

                        case NERO_END:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Found \"END!\" chunk, finishing parse");
                            parsing = false;

                            break;
                        }

                        default:
                        {
                            AaruConsole.DebugWriteLine("Nero plugin", "Unknown chunk ID \"{0}\", skipping...",
                                                       Encoding.ASCII.GetString(BigEndianBitConverter.
                                                                                    GetBytes(chunkId)));

                            _imageStream.Seek(chunkLength, SeekOrigin.Current);

                            break;
                        }
                    }
                }

                _imageInfo.HasPartitions         = true;
                _imageInfo.HasSessions           = true;
                _imageInfo.Creator               = null;
                _imageInfo.CreationTime          = imageFilter.GetCreationTime();
                _imageInfo.LastModificationTime  = imageFilter.GetLastWriteTime();
                _imageInfo.MediaTitle            = Path.GetFileNameWithoutExtension(imageFilter.GetFilename());
                _imageInfo.Comments              = null;
                _imageInfo.MediaManufacturer     = null;
                _imageInfo.MediaModel            = null;
                _imageInfo.MediaSerialNumber     = null;
                _imageInfo.MediaBarcode          = null;
                _imageInfo.MediaPartNumber       = null;
                _imageInfo.DriveManufacturer     = null;
                _imageInfo.DriveModel            = null;
                _imageInfo.DriveSerialNumber     = null;
                _imageInfo.DriveFirmwareRevision = null;
                _imageInfo.MediaSequence         = 0;
                _imageInfo.LastMediaSequence     = 0;

                if(_imageNewFormat)
                {
                    _imageInfo.ImageSize          = footerV2.FirstChunkOffset;
                    _imageInfo.Version            = "Nero Burning ROM >= 5.5";
                    _imageInfo.Application        = "Nero Burning ROM";
                    _imageInfo.ApplicationVersion = ">= 5.5";
                }
                else
                {
                    _imageInfo.ImageSize          = footerV1.FirstChunkOffset;
                    _imageInfo.Version            = "Nero Burning ROM <= 5.0";
                    _imageInfo.Application        = "Nero Burning ROM";
                    _imageInfo.ApplicationVersion = "<= 5.0";
                }

                if(_neroSessions.Count == 0)
                    _neroSessions.Add(1, currenttrack);

                AaruConsole.DebugWriteLine("Nero plugin", "Building offset, track and session maps");

                currentsession = 1;
                _neroSessions.TryGetValue(1, out uint currentsessionmaxtrack);
                uint  currentsessioncurrenttrack = 1;
                var   currentsessionstruct       = new Session();
                ulong partitionSequence          = 0;
                ulong partitionStartByte         = 0;

                for(uint i = 1; i <= _neroTracks.Count; i++)
                {
                    if(!_neroTracks.TryGetValue(i, out NeroTrack neroTrack))
                        continue;

                    AaruConsole.DebugWriteLine("Nero plugin", "\tcurrentsession = {0}", currentsession);
                    AaruConsole.DebugWriteLine("Nero plugin", "\tcurrentsessionmaxtrack = {0}", currentsessionmaxtrack);

                    AaruConsole.DebugWriteLine("Nero plugin", "\tcurrentsessioncurrenttrack = {0}",
                                               currentsessioncurrenttrack);

                    var track = new Track();

                    if(_neroCuesheetV1?.Entries?.Count > 0)
                    {
                        foreach(NeroV1CueEntry entry in _neroCuesheetV1.
                                                        Entries.Where(e => e.TrackNumber == neroTrack.Sequence).
                                                        OrderBy(e => e.IndexNumber))
                        {
                            int minute = ((((entry.Minute & 0xF0) >> 4) * 10) + entry.Minute) & 0xF;
                            int second = ((((entry.Second & 0xF0) >> 4) * 10) + entry.Second) & 0xF;
                            int frame  = ((((entry.Frame  & 0xF0) >> 4) * 10) + entry.Frame)  & 0xF;

                            int indexSector = ((minute * 60 * 75) + (second * 75) + frame) - 150;

                            track.Indexes[entry.IndexNumber] = indexSector;
                        }
                    }
                    else if(_neroCuesheetV2?.Entries?.Count > 0)
                        foreach(NeroV2CueEntry entry in _neroCuesheetV2.
                                                        Entries.Where(e => e.TrackNumber == neroTrack.Sequence).
                                                        OrderBy(e => e.IndexNumber))
                            track.Indexes[entry.IndexNumber] = entry.LbaStart;

                    if(!track.Indexes.ContainsKey(1))
                    {
                        if(neroTrack.UseLbaForIndex)
                            track.Indexes[1] = (int)neroTrack.StartLba;
                        else
                        {
                            if(neroTrack.Sequence == 1)
                            {
                                track.Indexes.Add(0, -150);
                                neroTrack.Index0 = neroTrack.Index1;
                            }

                            if(neroTrack.Index0 < neroTrack.Index1)
                                track.Indexes.Add(0, (int)(neroTrack.Index0 / neroTrack.SectorSize));

                            track.Indexes.Add(1, (int)(neroTrack.Index1 / neroTrack.SectorSize));
                        }
                    }

                    track.TrackDescription = StringHandlers.CToString(neroTrack.Isrc);
                    track.TrackEndSector   = ((neroTrack.Length / neroTrack.SectorSize) + (ulong)track.Indexes[1]) - 1;

                    if(track.Indexes.ContainsKey(0))
                        track.TrackPregap = (ulong)(track.Indexes[1] - track.Indexes[0]);

                    if(track.Indexes.ContainsKey(0) &&
                       track.Indexes[0] == track.Indexes[1])
                        track.Indexes.Remove(0);

                    track.TrackSequence = neroTrack.Sequence;
                    track.TrackSession  = currentsession;

                    track.TrackStartSector = (ulong)(track.Indexes.ContainsKey(0) && track.Indexes[0] > 0
                                                         ? track.Indexes[0] : track.Indexes[1]);

                    track.TrackType           = NeroTrackModeToTrackType((DaoMode)neroTrack.Mode);
                    track.TrackFile           = imageFilter.GetFilename();
                    track.TrackFilter         = imageFilter;
                    track.TrackFileOffset     = neroTrack.Offset;
                    track.TrackFileType       = "BINARY";
                    track.TrackSubchannelType = TrackSubchannelType.None;

                    switch((DaoMode)neroTrack.Mode)
                    {
                        case DaoMode.Audio:
                            track.TrackBytesPerSector    = 2352;
                            track.TrackRawBytesPerSector = 2352;

                            break;
                        case DaoMode.AudioSub:
                            track.TrackBytesPerSector    = 2352;
                            track.TrackRawBytesPerSector = 2448;
                            track.TrackSubchannelType    = TrackSubchannelType.RawInterleaved;

                            break;
                        case DaoMode.Data:
                        case DaoMode.DataM2F1:
                            track.TrackBytesPerSector    = 2048;
                            track.TrackRawBytesPerSector = 2048;

                            break;
                        case DaoMode.DataM2F2:
                            track.TrackBytesPerSector    = 2336;
                            track.TrackRawBytesPerSector = 2336;

                            break;
                        case DaoMode.DataM2Raw:
                            track.TrackBytesPerSector    = 2352;
                            track.TrackRawBytesPerSector = 2352;

                            break;
                        case DaoMode.DataM2RawSub:
                            track.TrackBytesPerSector    = 2352;
                            track.TrackRawBytesPerSector = 2448;
                            track.TrackSubchannelType    = TrackSubchannelType.RawInterleaved;

                            break;
                        case DaoMode.DataRaw:
                            track.TrackBytesPerSector    = 2048;
                            track.TrackRawBytesPerSector = 2352;

                            break;
                        case DaoMode.DataRawSub:
                            track.TrackBytesPerSector    = 2048;
                            track.TrackRawBytesPerSector = 2448;
                            track.TrackSubchannelType    = TrackSubchannelType.RawInterleaved;

                            break;
                    }

                    if(track.TrackSubchannelType == TrackSubchannelType.RawInterleaved)
                    {
                        track.TrackSubchannelFilter = imageFilter;
                        track.TrackSubchannelFile   = imageFilter.GetFilename();
                        track.TrackSubchannelOffset = neroTrack.Offset;
                    }

                    Tracks.Add(track);

                    AaruConsole.DebugWriteLine("Nero plugin", "\t\t _track.TrackDescription = {0}",
                                               track.TrackDescription);

                    AaruConsole.DebugWriteLine("Nero plugin", "\t\t _track.TrackEndSector = {0}", track.TrackEndSector);
                    AaruConsole.DebugWriteLine("Nero plugin", "\t\t _track.TrackPregap = {0}", track.TrackPregap);
                    AaruConsole.DebugWriteLine("Nero plugin", "\t\t _track.TrackSequence = {0}", track.TrackSequence);
                    AaruConsole.DebugWriteLine("Nero plugin", "\t\t _track.TrackSession = {0}", track.TrackSession);

                    AaruConsole.DebugWriteLine("Nero plugin", "\t\t _track.TrackStartSector = {0}",
                                               track.TrackStartSector);

                    AaruConsole.DebugWriteLine("Nero plugin", "\t\t _track.TrackType = {0}", track.TrackType);

                    if(currentsessioncurrenttrack == 1)
                        currentsessionstruct = new Session
                        {
                            SessionSequence = currentsession,
                            StartSector     = track.TrackStartSector,
                            StartTrack      = track.TrackSequence
                        };

                    currentsessioncurrenttrack++;

                    if(currentsessioncurrenttrack > currentsessionmaxtrack)
                    {
                        currentsession++;
                        _neroSessions.TryGetValue(currentsession, out currentsessionmaxtrack);
                        currentsessioncurrenttrack     = 1;
                        currentsessionstruct.EndTrack  = track.TrackSequence;
                        currentsessionstruct.EndSector = track.TrackEndSector;
                        Sessions.Add(currentsessionstruct);
                    }

                    else if(i == _neroTracks.Count)
                    {
                        _neroSessions.TryGetValue(currentsession, out currentsessionmaxtrack);
                        currentsessioncurrenttrack     = 1;
                        currentsessionstruct.EndTrack  = track.TrackSequence;
                        currentsessionstruct.EndSector = track.TrackEndSector;
                        Sessions.Add(currentsessionstruct);
                    }

                    _offsetmap.Add(track.TrackSequence, track.TrackStartSector);

                    AaruConsole.DebugWriteLine("Nero plugin", "\t\t Offset[{0}]: {1}", track.TrackSequence,
                                               track.TrackStartSector);

                    var partition = new Partition
                    {
                        Description = $"Track {track.TrackSequence}",
                        Size        = neroTrack.EndOfTrack - neroTrack.Index1,
                        Name        = StringHandlers.CToString(neroTrack.Isrc),
                        Sequence    = partitionSequence,
                        Offset      = partitionStartByte,
                        Start       = (ulong)track.Indexes[1],
                        Type        = NeroTrackModeToTrackType((DaoMode)neroTrack.Mode).ToString()
                    };

                    partition.Length = partition.Size / neroTrack.SectorSize;
                    Partitions.Add(partition);
                    partitionSequence++;
                    partitionStartByte += partition.Size;
                }

                foreach(Track track in from track in Tracks where !track.Indexes.ContainsKey(0)
                                       let firstTrackInSession =
                                           Tracks.Where(t => t.TrackSession == track.TrackSession).
                                                  OrderBy(t => t.TrackSequence).FirstOrDefault()
                                       where !(firstTrackInSession is null) &&
                                             firstTrackInSession.TrackSequence == track.TrackSequence select track)
                {
                    track.TrackPregap = 150;

                    Dictionary<ushort, int> indexes = new Dictionary<ushort, int>
                    {
                        [0] = track.Indexes[1] - 150
                    };

                    foreach(KeyValuePair<ushort, int> idx in track.Indexes)
                        indexes[idx.Key] = idx.Value;

                    track.Indexes.Clear();

                    foreach(KeyValuePair<ushort, int> idx in indexes.OrderBy(i => i.Key))
                        track.Indexes[idx.Key] = idx.Value;

                    track.TrackFileOffset  -= (ulong)(track.TrackRawBytesPerSector * 150);
                    track.TrackStartSector -= 150;
                }

                _neroFilter = imageFilter;

                if(_imageInfo.MediaType == MediaType.Unknown ||
                   _imageInfo.MediaType == MediaType.CD)
                {
                    bool data       = false;
                    bool mode2      = false;
                    bool firstaudio = false;
                    bool firstdata  = false;
                    bool audio      = false;

                    for(int i = 0; i < _neroTracks.Count; i++)
                    {
                        // First track is audio
                        firstaudio |= i == 0 && ((DaoMode)_neroTracks.ElementAt(i).Value.Mode == DaoMode.Audio ||
                                                 (DaoMode)_neroTracks.ElementAt(i).Value.Mode == DaoMode.AudioSub);

                        // First track is data
                        firstdata |= i == 0 && (DaoMode)_neroTracks.ElementAt(i).Value.Mode != DaoMode.Audio &&
                                     (DaoMode)_neroTracks.ElementAt(i).Value.Mode != DaoMode.AudioSub;

                        // Any non first track is data
                        data |= i != 0 && (DaoMode)_neroTracks.ElementAt(i).Value.Mode != DaoMode.Audio &&
                                (DaoMode)_neroTracks.ElementAt(i).Value.Mode != DaoMode.AudioSub;

                        // Any non first track is audio
                        audio |= i != 0 && ((DaoMode)_neroTracks.ElementAt(i).Value.Mode == DaoMode.Audio ||
                                            (DaoMode)_neroTracks.ElementAt(i).Value.Mode == DaoMode.AudioSub);

                        switch((DaoMode)_neroTracks.ElementAt(i).Value.Mode)
                        {
                            case DaoMode.DataM2F1:
                            case DaoMode.DataM2F2:
                            case DaoMode.DataM2Raw:
                            case DaoMode.DataM2RawSub:
                                mode2 = true;

                                break;
                        }
                    }

                    if(!data &&
                       !firstdata)
                        _imageInfo.MediaType = MediaType.CDDA;
                    else if(firstaudio         &&
                            data               &&
                            Sessions.Count > 1 &&
                            mode2)
                        _imageInfo.MediaType = MediaType.CDPLUS;
                    else if((firstdata && audio) || mode2)
                        _imageInfo.MediaType = MediaType.CDROMXA;
                    else if(!audio)
                        _imageInfo.MediaType = MediaType.CDROM;
                    else
                        _imageInfo.MediaType = MediaType.CD;
                }

                _imageInfo.XmlMediaType = XmlMediaType.OpticalDisc;
                AaruConsole.VerboseWriteLine("Nero image contains a disc of type {0}", _imageInfo.MediaType);

                _sectorBuilder = new SectorBuilder();

                if(_imageInfo.MediaType != MediaType.CD            &&
                   _imageInfo.MediaType != MediaType.CDDA          &&
                   _imageInfo.MediaType != MediaType.CDG           &&
                   _imageInfo.MediaType != MediaType.CDEG          &&
                   _imageInfo.MediaType != MediaType.CDI           &&
                   _imageInfo.MediaType != MediaType.CDROM         &&
                   _imageInfo.MediaType != MediaType.CDROMXA       &&
                   _imageInfo.MediaType != MediaType.CDPLUS        &&
                   _imageInfo.MediaType != MediaType.CDMO          &&
                   _imageInfo.MediaType != MediaType.CDR           &&
                   _imageInfo.MediaType != MediaType.CDRW          &&
                   _imageInfo.MediaType != MediaType.CDMRW         &&
                   _imageInfo.MediaType != MediaType.VCD           &&
                   _imageInfo.MediaType != MediaType.SVCD          &&
                   _imageInfo.MediaType != MediaType.PCD           &&
                   _imageInfo.MediaType != MediaType.DTSCD         &&
                   _imageInfo.MediaType != MediaType.CDMIDI        &&
                   _imageInfo.MediaType != MediaType.CDV           &&
                   _imageInfo.MediaType != MediaType.CDIREADY      &&
                   _imageInfo.MediaType != MediaType.FMTOWNS       &&
                   _imageInfo.MediaType != MediaType.PS1CD         &&
                   _imageInfo.MediaType != MediaType.PS2CD         &&
                   _imageInfo.MediaType != MediaType.MEGACD        &&
                   _imageInfo.MediaType != MediaType.SATURNCD      &&
                   _imageInfo.MediaType != MediaType.GDROM         &&
                   _imageInfo.MediaType != MediaType.GDR           &&
                   _imageInfo.MediaType != MediaType.MilCD         &&
                   _imageInfo.MediaType != MediaType.SuperCDROM2   &&
                   _imageInfo.MediaType != MediaType.JaguarCD      &&
                   _imageInfo.MediaType != MediaType.ThreeDO       &&
                   _imageInfo.MediaType != MediaType.PCFX          &&
                   _imageInfo.MediaType != MediaType.NeoGeoCD      &&
                   _imageInfo.MediaType != MediaType.CDTV          &&
                   _imageInfo.MediaType != MediaType.CD32          &&
                   _imageInfo.MediaType != MediaType.Playdia       &&
                   _imageInfo.MediaType != MediaType.Pippin        &&
                   _imageInfo.MediaType != MediaType.VideoNow      &&
                   _imageInfo.MediaType != MediaType.VideoNowColor &&
                   _imageInfo.MediaType != MediaType.VideoNowXp    &&
                   _imageInfo.MediaType != MediaType.CVD)
                    foreach(Track track in Tracks)
                    {
                        track.TrackPregap = 0;
                        track.Indexes?.Clear();
                    }

                return true;
            }
            catch
            {
                AaruConsole.DebugWrite("Nero plugin", "Exception ocurred opening file.");

                return false;
            }
        }

        public byte[] ReadDiskTag(MediaTagType tag)
        {
            switch(tag)
            {
                case MediaTagType.CD_MCN:  return _upc;
                case MediaTagType.CD_TEXT: throw new NotImplementedException("Not yet implemented");
                default:
                    throw new FeaturedNotSupportedByDiscImageException("Requested disk tag not supported by image");
            }
        }

        public byte[] ReadSector(ulong sectorAddress) => ReadSectors(sectorAddress, 1);

        public byte[] ReadSectorTag(ulong sectorAddress, SectorTagType tag) => ReadSectorsTag(sectorAddress, 1, tag);

        public byte[] ReadSector(ulong sectorAddress, uint track) => ReadSectors(sectorAddress, 1, track);

        public byte[] ReadSectorTag(ulong sectorAddress, uint track, SectorTagType tag) =>
            ReadSectorsTag(sectorAddress, 1, track, tag);

        public byte[] ReadSectors(ulong sectorAddress, uint length)
        {
            foreach(KeyValuePair<uint, ulong> kvp in from kvp in _offsetmap where sectorAddress     >= kvp.Value
                                                     from track in Tracks where track.TrackSequence == kvp.Key
                                                     where sectorAddress        - kvp.Value <=
                                                           track.TrackEndSector - track.TrackStartSector select kvp)
                return ReadSectors(sectorAddress - kvp.Value, length, kvp.Key);

            throw new ArgumentOutOfRangeException(nameof(sectorAddress), $"Sector address {sectorAddress} not found");
        }

        public byte[] ReadSectorsTag(ulong sectorAddress, uint length, SectorTagType tag)
        {
            foreach(KeyValuePair<uint, ulong> kvp in from kvp in _offsetmap where sectorAddress     >= kvp.Value
                                                     from track in Tracks where track.TrackSequence == kvp.Key
                                                     where sectorAddress        - kvp.Value <=
                                                           track.TrackEndSector - track.TrackStartSector select kvp)
                return ReadSectorsTag(sectorAddress - kvp.Value, length, kvp.Key, tag);

            throw new ArgumentOutOfRangeException(nameof(sectorAddress), $"Sector address {sectorAddress} not found");
        }

        public byte[] ReadSectors(ulong sectorAddress, uint length, uint track)
        {
            if(!_neroTracks.TryGetValue(track, out NeroTrack aaruTrack))
                throw new ArgumentOutOfRangeException(nameof(track), "Track not found");

            if(length > aaruTrack.Sectors)
                throw new ArgumentOutOfRangeException(nameof(length),
                                                      $"Requested more sectors ({length}) than present in track ({aaruTrack.Sectors}), won't cross tracks");

            uint sectorOffset;
            uint sectorSize;
            uint sectorSkip;
            bool mode2 = false;

            switch((DaoMode)aaruTrack.Mode)
            {
                case DaoMode.Data:
                case DaoMode.DataM2F1:
                {
                    sectorOffset = 0;
                    sectorSize   = 2048;
                    sectorSkip   = 0;

                    break;
                }

                case DaoMode.DataM2F2:
                {
                    mode2        = true;
                    sectorOffset = 0;
                    sectorSize   = 2336;
                    sectorSkip   = 0;

                    break;
                }

                case DaoMode.Audio:
                {
                    sectorOffset = 0;
                    sectorSize   = 2352;
                    sectorSkip   = 0;

                    break;
                }

                case DaoMode.DataRaw:
                {
                    sectorOffset = 16;
                    sectorSize   = 2048;
                    sectorSkip   = 288;

                    break;
                }

                case DaoMode.DataM2Raw:
                {
                    mode2        = true;
                    sectorOffset = 0;
                    sectorSize   = 2352;
                    sectorSkip   = 0;

                    break;
                }

                // TODO: Supposing Nero suffixes the subchannel to the channel
                case DaoMode.DataRawSub:
                {
                    sectorOffset = 16;
                    sectorSize   = 2048;
                    sectorSkip   = 288 + 96;

                    break;
                }

                case DaoMode.DataM2RawSub:
                {
                    mode2        = true;
                    sectorOffset = 0;
                    sectorSize   = 2352;
                    sectorSkip   = 96;

                    break;
                }

                case DaoMode.AudioSub:
                {
                    sectorOffset = 0;
                    sectorSize   = 2352;
                    sectorSkip   = 96;

                    break;
                }

                default: throw new FeatureSupportedButNotImplementedImageException("Unsupported track type");
            }

            byte[] buffer = new byte[sectorSize * length];

            _imageStream = _neroFilter.GetDataForkStream();
            var br = new BinaryReader(_imageStream);

            br.BaseStream.
               Seek((long)aaruTrack.Offset + (long)(sectorAddress * (sectorOffset + sectorSize + sectorSkip)),
                    SeekOrigin.Begin);

            if(mode2)
            {
                var mode2Ms = new MemoryStream((int)(sectorSize * length));

                buffer = br.ReadBytes((int)((sectorSize + sectorSkip) * length));

                for(int i = 0; i < length; i++)
                {
                    byte[] sector = new byte[sectorSize];
                    Array.Copy(buffer, (sectorSize + sectorSkip) * i, sector, 0, sectorSize);
                    sector = Sector.GetUserDataFromMode2(sector);
                    mode2Ms.Write(sector, 0, sector.Length);
                }

                buffer = mode2Ms.ToArray();
            }
            else if(sectorOffset == 0 &&
                    sectorSkip   == 0)
                buffer = br.ReadBytes((int)(sectorSize * length));
            else
                for(int i = 0; i < length; i++)
                {
                    br.BaseStream.Seek(sectorOffset, SeekOrigin.Current);
                    byte[] sector = br.ReadBytes((int)sectorSize);
                    br.BaseStream.Seek(sectorSkip, SeekOrigin.Current);
                    Array.Copy(sector, 0, buffer, i * sectorSize, sectorSize);
                }

            return buffer;
        }

        public byte[] ReadSectorsTag(ulong sectorAddress, uint length, uint track, SectorTagType tag)
        {
            if(tag == SectorTagType.CdTrackFlags ||
               tag == SectorTagType.CdTrackIsrc)
                track = (uint)sectorAddress;

            if(!_neroTracks.TryGetValue(track, out NeroTrack aaruTrack))
                throw new ArgumentOutOfRangeException(nameof(track), "Track not found");

            if(length > aaruTrack.Sectors)
                throw new ArgumentOutOfRangeException(nameof(length),
                                                      $"Requested more sectors ({length}) than present in track ({aaruTrack.Sectors}), won't cross tracks");

            uint sectorOffset;
            uint sectorSize;
            uint sectorSkip;

            switch(tag)
            {
                case SectorTagType.CdSectorEcc:
                case SectorTagType.CdSectorEccP:
                case SectorTagType.CdSectorEccQ:
                case SectorTagType.CdSectorEdc:
                case SectorTagType.CdSectorHeader:
                case SectorTagType.CdSectorSubchannel:
                case SectorTagType.CdSectorSubHeader:
                case SectorTagType.CdSectorSync: break;
                case SectorTagType.CdTrackFlags:
                {
                    byte[] flags = new byte[1];
                    flags[0] = 0x00;

                    if((DaoMode)aaruTrack.Mode != DaoMode.Audio &&
                       (DaoMode)aaruTrack.Mode != DaoMode.AudioSub)
                        flags[0] += 0x4;

                    return flags;
                }

                case SectorTagType.CdTrackIsrc: return aaruTrack.Isrc;
                case SectorTagType.CdTrackText:
                    throw new FeatureSupportedButNotImplementedImageException("Feature not yet implemented");
                default: throw new ArgumentException("Unsupported tag requested", nameof(tag));
            }

            switch((DaoMode)aaruTrack.Mode)
            {
                case DaoMode.Data:
                case DaoMode.DataM2F1: throw new ArgumentException("No tags in image for requested track", nameof(tag));
                case DaoMode.DataM2F2:
                {
                    switch(tag)
                    {
                        case SectorTagType.CdSectorSync:
                        case SectorTagType.CdSectorHeader:
                        case SectorTagType.CdSectorSubchannel:
                        case SectorTagType.CdSectorEcc:
                        case SectorTagType.CdSectorEccP:
                        case SectorTagType.CdSectorEccQ:
                            throw new ArgumentException("Unsupported tag requested for this track", nameof(tag));
                        case SectorTagType.CdSectorSubHeader:
                        {
                            sectorOffset = 0;
                            sectorSize   = 8;
                            sectorSkip   = 2328;

                            break;
                        }

                        case SectorTagType.CdSectorEdc:
                        {
                            sectorOffset = 2332;
                            sectorSize   = 4;
                            sectorSkip   = 0;

                            break;
                        }

                        default: throw new ArgumentException("Unsupported tag requested", nameof(tag));
                    }

                    break;
                }

                case DaoMode.Audio: throw new ArgumentException("There are no tags on audio tracks", nameof(tag));
                case DaoMode.DataRaw:
                {
                    switch(tag)
                    {
                        case SectorTagType.CdSectorSync:
                        {
                            sectorOffset = 0;
                            sectorSize   = 12;
                            sectorSkip   = 2340;

                            break;
                        }

                        case SectorTagType.CdSectorHeader:
                        {
                            sectorOffset = 12;
                            sectorSize   = 4;
                            sectorSkip   = 2336;

                            break;
                        }

                        case SectorTagType.CdSectorSubchannel:
                        case SectorTagType.CdSectorSubHeader:
                            throw new ArgumentException("Unsupported tag requested for this track", nameof(tag));
                        case SectorTagType.CdSectorEcc:
                        {
                            sectorOffset = 2076;
                            sectorSize   = 276;
                            sectorSkip   = 0;

                            break;
                        }

                        case SectorTagType.CdSectorEccP:
                        {
                            sectorOffset = 2076;
                            sectorSize   = 172;
                            sectorSkip   = 104;

                            break;
                        }

                        case SectorTagType.CdSectorEccQ:
                        {
                            sectorOffset = 2248;
                            sectorSize   = 104;
                            sectorSkip   = 0;

                            break;
                        }

                        case SectorTagType.CdSectorEdc:
                        {
                            sectorOffset = 2064;
                            sectorSize   = 4;
                            sectorSkip   = 284;

                            break;
                        }

                        default: throw new ArgumentException("Unsupported tag requested", nameof(tag));
                    }

                    break;
                }

                // TODO
                case DaoMode.DataM2RawSub:
                    throw new FeatureSupportedButNotImplementedImageException("Feature not yet implemented");
                case DaoMode.DataRawSub:
                {
                    switch(tag)
                    {
                        case SectorTagType.CdSectorSync:
                        {
                            sectorOffset = 0;
                            sectorSize   = 12;
                            sectorSkip   = 2340 + 96;

                            break;
                        }

                        case SectorTagType.CdSectorHeader:
                        {
                            sectorOffset = 12;
                            sectorSize   = 4;
                            sectorSkip   = 2336 + 96;

                            break;
                        }

                        case SectorTagType.CdSectorSubchannel:
                        {
                            sectorOffset = 2352;
                            sectorSize   = 96;
                            sectorSkip   = 0;

                            break;
                        }

                        case SectorTagType.CdSectorSubHeader:
                            throw new ArgumentException("Unsupported tag requested for this track", nameof(tag));
                        case SectorTagType.CdSectorEcc:
                        {
                            sectorOffset = 2076;
                            sectorSize   = 276;
                            sectorSkip   = 0 + 96;

                            break;
                        }

                        case SectorTagType.CdSectorEccP:
                        {
                            sectorOffset = 2076;
                            sectorSize   = 172;
                            sectorSkip   = 104 + 96;

                            break;
                        }

                        case SectorTagType.CdSectorEccQ:
                        {
                            sectorOffset = 2248;
                            sectorSize   = 104;
                            sectorSkip   = 0 + 96;

                            break;
                        }

                        case SectorTagType.CdSectorEdc:
                        {
                            sectorOffset = 2064;
                            sectorSize   = 4;
                            sectorSkip   = 284 + 96;

                            break;
                        }

                        default: throw new ArgumentException("Unsupported tag requested", nameof(tag));
                    }

                    break;
                }

                case DaoMode.AudioSub:
                {
                    if(tag != SectorTagType.CdSectorSubchannel)
                        throw new ArgumentException("Unsupported tag requested for this track", nameof(tag));

                    sectorOffset = 2352;
                    sectorSize   = 96;
                    sectorSkip   = 0;

                    break;
                }

                default: throw new FeatureSupportedButNotImplementedImageException("Unsupported track type");
            }

            byte[] buffer = new byte[sectorSize * length];

            _imageStream = _neroFilter.GetDataForkStream();
            var br = new BinaryReader(_imageStream);

            br.BaseStream.
               Seek((long)aaruTrack.Offset + (long)(sectorAddress * (sectorOffset + sectorSize + sectorSkip)),
                    SeekOrigin.Begin);

            if(sectorOffset == 0 &&
               sectorSkip   == 0)
                buffer = br.ReadBytes((int)(sectorSize * length));
            else
                for(int i = 0; i < length; i++)
                {
                    br.BaseStream.Seek(sectorOffset, SeekOrigin.Current);
                    byte[] sector = br.ReadBytes((int)sectorSize);
                    br.BaseStream.Seek(sectorSkip, SeekOrigin.Current);
                    Array.Copy(sector, 0, buffer, i * sectorSize, sectorSize);
                }

            return buffer;
        }

        public byte[] ReadSectorLong(ulong sectorAddress) => ReadSectorsLong(sectorAddress, 1);

        public byte[] ReadSectorLong(ulong sectorAddress, uint track) => ReadSectorsLong(sectorAddress, 1, track);

        public byte[] ReadSectorsLong(ulong sectorAddress, uint length)
        {
            foreach(KeyValuePair<uint, ulong> kvp in from kvp in _offsetmap where sectorAddress     >= kvp.Value
                                                     from track in Tracks where track.TrackSequence == kvp.Key
                                                     where sectorAddress        - kvp.Value <=
                                                           track.TrackEndSector - track.TrackStartSector select kvp)
                return ReadSectorsLong(sectorAddress - kvp.Value, length, kvp.Key);

            throw new ArgumentOutOfRangeException(nameof(sectorAddress), $"Sector address {sectorAddress} not found");
        }

        public byte[] ReadSectorsLong(ulong sectorAddress, uint length, uint track)
        {
            if(!_neroTracks.TryGetValue(track, out NeroTrack aaruTrack))
                throw new ArgumentOutOfRangeException(nameof(track), "Track not found");

            if(length > aaruTrack.Sectors)
                throw new ArgumentOutOfRangeException(nameof(length),
                                                      $"Requested more sectors ({length}) than present in track ({aaruTrack.Sectors}), won't cross tracks");

            uint sectorOffset;
            uint sectorSize;
            uint sectorSkip;

            switch((DaoMode)aaruTrack.Mode)
            {
                case DaoMode.Data:
                case DaoMode.DataM2F1:
                {
                    sectorOffset = 0;
                    sectorSize   = 2048;
                    sectorSkip   = 0;

                    break;
                }

                case DaoMode.DataM2F2:
                {
                    sectorOffset = 0;
                    sectorSize   = 2336;
                    sectorSkip   = 0;

                    break;
                }

                case DaoMode.DataRaw:
                case DaoMode.DataM2Raw:
                case DaoMode.Audio:
                {
                    sectorOffset = 0;
                    sectorSize   = 2352;
                    sectorSkip   = 0;

                    break;
                }

                case DaoMode.DataRawSub:
                case DaoMode.DataM2RawSub:
                case DaoMode.AudioSub:
                {
                    sectorOffset = 0;
                    sectorSize   = 2448;
                    sectorSkip   = 0;

                    break;
                }

                default: throw new FeatureSupportedButNotImplementedImageException("Unsupported track type");
            }

            byte[] buffer = new byte[sectorSize * length];

            _imageStream = _neroFilter.GetDataForkStream();
            var br = new BinaryReader(_imageStream);

            br.BaseStream.
               Seek((long)aaruTrack.Offset + (long)(sectorAddress * (sectorOffset + sectorSize + sectorSkip)),
                    SeekOrigin.Begin);

            if(sectorOffset == 0 &&
               sectorSkip   == 0)
                buffer = br.ReadBytes((int)(sectorSize * length));
            else
                for(int i = 0; i < length; i++)
                {
                    br.BaseStream.Seek(sectorOffset, SeekOrigin.Current);
                    byte[] sector = br.ReadBytes((int)sectorSize);
                    br.BaseStream.Seek(sectorSkip, SeekOrigin.Current);

                    Array.Copy(sector, 0, buffer, i * sectorSize, sectorSize);
                }

            switch((DaoMode)aaruTrack.Mode)
            {
                case DaoMode.Data:
                {
                    byte[] fullSector = new byte[2352];
                    byte[] fullBuffer = new byte[2352 * length];

                    for(uint i = 0; i < length; i++)
                    {
                        Array.Copy(buffer, i * 2048, fullSector, 16, 2048);
                        _sectorBuilder.ReconstructPrefix(ref fullSector, TrackType.CdMode1, (long)(sectorAddress + i));
                        _sectorBuilder.ReconstructEcc(ref fullSector, TrackType.CdMode1);
                        Array.Copy(fullSector, 0, fullBuffer, i * 2352, 2352);
                    }

                    buffer = fullBuffer;

                    break;
                }
                case DaoMode.DataM2F1:
                {
                    byte[] fullSector = new byte[2352];
                    byte[] fullBuffer = new byte[2352 * length];

                    for(uint i = 0; i < length; i++)
                    {
                        Array.Copy(buffer, i * 2048, fullSector, 24, 2048);

                        _sectorBuilder.ReconstructPrefix(ref fullSector, TrackType.CdMode2Form1,
                                                         (long)(sectorAddress + i));

                        _sectorBuilder.ReconstructEcc(ref fullSector, TrackType.CdMode2Form1);
                        Array.Copy(fullSector, 0, fullBuffer, i * 2352, 2352);
                    }

                    buffer = fullBuffer;

                    break;
                }
                case DaoMode.DataM2F2:
                {
                    byte[] fullSector = new byte[2352];
                    byte[] fullBuffer = new byte[2352 * length];

                    for(uint i = 0; i < length; i++)
                    {
                        _sectorBuilder.ReconstructPrefix(ref fullSector, TrackType.CdMode2Formless,
                                                         (long)(sectorAddress + i));

                        Array.Copy(buffer, i                    * 2336, fullSector, 16, 2336);
                        Array.Copy(fullSector, 0, fullBuffer, i * 2352, 2352);
                    }

                    buffer = fullBuffer;

                    break;
                }
            }

            return buffer;
        }

        public List<Track> GetSessionTracks(Session session) => GetSessionTracks(session.SessionSequence);

        public List<Track> GetSessionTracks(ushort session) =>
            Tracks.Where(track => track.TrackSession == session).ToList();
    }
}