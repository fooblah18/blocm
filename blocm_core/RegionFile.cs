﻿/*  Minecraft NBT reader
 * 
 *  Copyright 2010-2013 Michael Ong, all rights reserved.
 *  
 *  This program is free software; you can redistribute it and/or
 *  modify it under the terms of the GNU General Public License
 *  as published by the Free Software Foundation; either version 2
 *  of the License, or (at your option) any later version.
 *  
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *  
 *  You should have received a copy of the GNU General Public License
 *  along with this program; if not, write to the Free Software
 *  Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using NBT.Utils;

namespace NBT
{
    /// <summary>
    ///     A Minecraft region file.
    /// </summary>
    public class RegionFile : IDisposable
    {
        /// <summary>
        ///     The maximum threads the region reader will allocate (use 2^n values).
        /// </summary>
        public const int MaxThreads = 4;

        /// <summary>
        ///     Maximum number of chunks per thread.
        /// </summary>
        public const int chunkSlice = 1024 / MaxThreads;

        private Offset[] offsets;
        private TimeStamp[] tStamps;

        private RegionFile()
        {
            offsets = new Offset[1024];
            tStamps = new TimeStamp[1024];
        }

        /// <summary>
        /// Returns the chunk offsets and sector sizes in this region file.
        /// </summary>
        public ReadOnlyCollection<Offset> ChunkOffsets
        {
            get { return new ReadOnlyCollection<Offset>(offsets); }
        }

        /// <summary>
        /// Returns the time stamp of the chunks in this region file.
        /// </summary>
        public ReadOnlyCollection<TimeStamp> TimeStamps
        {
            get { return new ReadOnlyCollection<TimeStamp>(tStamps); }
        }

        /// <summary>
        ///     The content of the region file.
        /// </summary>
        public NbtFile[] Content { get; private set; }

        /// <summary>
        ///     Gets a chunk from this region.
        /// </summary>
        /// <param name="point">The location of the chunk.</param>
        /// <returns>An NBT file that has the </returns>
        public NbtFile this[Point point]
        {
            get { return Content[point.X + point.Y * 32]; }
            set { InsertChunk(point, value); }
        }

        /// <summary>
        /// Dispose the contents of the file.
        /// </summary>
        public void Dispose()
        {
            if (Content != null)
                foreach (NbtFile file in Content.Where(file => file != null))
                {
                    file.Dispose();
                }

            Content = null;
            offsets = null;
            tStamps = null;
        }

        /// <summary>
        ///     Inserts/replaces a new chunk on a specified location.
        /// </summary>
        /// <param name="location">The region location of the chunk.</param>
        /// <param name="chunk">The chunk to be added.</param>
        public void InsertChunk(Point location, NbtFile chunk)
        {
            Content[location.X + (location.Y * 32)] = chunk;
        }

        /// <summary>
        ///     Removes a chunk on a specified location.
        /// </summary>
        /// <param name="location">The region location of the chunk to be removed.</param>
        public void RemoveChunk(Point location)
        {
            Content[location.X + (location.Y * 32)] = null;
        }

        /// <summary>
        ///     Saves the region file to a stream.
        /// </summary>
        /// <param name="stream">The stream the region file will write to.</param>
        /// <param name="regionFile">The region file to save.</param>
        public static void SaveRegion(Stream stream, RegionFile regionFile)
        {
            // build offset and timestamp headers
            
            // write data to storage
            using (var writer = new BinaryWriter(stream))
            {
                //write header information
                foreach (var offset in regionFile.offsets)
                {
                    writer.Write(EndiannessConverter.ToInt16((short)offset.SectorOffset));
                    writer.Write(EndiannessConverter.ToInt16(offset.SectorSize));
                }

                foreach (var timeStamp in regionFile.tStamps)
                {
                    writer.Write(EndiannessConverter.ToInt32((int)timeStamp.Timestamp));
                }

                var index = 0;

                // write chunk information
                foreach (var content in regionFile.Content)
                {
                    stream.Seek(regionFile.offsets[index++].SectorOffset * 4096, SeekOrigin.Begin);
                    NbtFile.SaveTag(stream, 2, content);

                    // write blank space before writing next chunk
                    var size = content.Size();
                    var csiz = (int)Math.Ceiling(size / 1024.0);

                    for (var i = 0; i < (csiz * 1024) - size; i++)
                    {
                        writer.Write((byte)0);
                    }
                }
            }
        }

        /// <summary>
        ///     Opens the region file from a stream.
        /// </summary>
        /// <param name="stream">The stream the region file will read from.</param>
        /// <returns>The parsed region file.</returns>
        public static RegionFile OpenRegion(Stream stream)
        {
            var region = new RegionFile();

            using (var reader = new BinaryReader(stream))
            {
                // initialize values

                #region Init

                var sectors = new int[1024];
                var tstamps = new int[1024];

                #endregion

                // read header information

                #region Header IO read

                for (int i = 0; i < 1024; i++)
                    sectors[i] = reader.ReadInt32();

                for (int i = 0; i < 1024; i++)
                    tstamps[i] = reader.ReadInt32();

                #endregion

                // parse header information

                #region Offset parse

                for (int i = 0; i < 1024; i++)
                {
                    int sector = EndiannessConverter.ToInt32(sectors[i]);

                    region.offsets[i] = new Offset
                        {
                            // get the sector size of the chunk
                            SectorSize = (byte)(sector & 0xFF),
                            // get the sector offset of the chunk
                            SectorOffset = sector >> 8,
                        };
                }

                #endregion

                #region Timestamp parse

                for (int i = 0; i < 1024; i++)
                {
                    int tstamp = EndiannessConverter.ToInt32(tstamps[i]);

                    region.tStamps[i] = new TimeStamp
                        {
                            Timestamp = tstamp
                        };
                }

                #endregion

                // read content from disk

                #region Chunk IO read

                var chunkBuffer = new byte[sectors.Length][];
                {
                    for (int i = 0; i < 1024; i++)
                    {
                        Offset offset = region.offsets[i];

                        if (offset.SectorOffset <= 0)
                            continue;

                        // sector offset will always start at 2
                        stream.Seek(offset.SectorOffset * 4096, SeekOrigin.Begin);
                        reader.ReadByte();

                        chunkBuffer[i] = reader.ReadBytes(EndiannessConverter.ToInt32(reader.ReadInt32()) - 1);
                    }
                }

                #endregion

                // parse chunk information

                #region Parse content

                var workerThreads = new Thread[MaxThreads];
                var content = new List<NbtFile>();
                {
                    for (int i = 0; i < MaxThreads; i++)
                    {
                        int index = i;

                        workerThreads[i] = new Thread(() =>
                            {
                                var buffer = new List<NbtFile>();

                                int offset = index * (1024 / MaxThreads);
                                for (int n = offset; n < (chunkSlice + offset); n++)
                                {
                                    byte[] chunk = chunkBuffer[n];

                                    if (chunk == null)
                                        continue;

                                    using (var mmStream = new MemoryStream(chunk))
                                    {
                                        buffer.Add(NbtFile.OpenTag(mmStream, 2));
                                    }
                                }

                                lock (content)
                                    content.AddRange(buffer);
                            }) { Name = "Worker thread " + (i + 1) };

                        workerThreads[i].Start();
                    }

                    foreach (Thread t in workerThreads)
                        t.Join();

                    region.Content = content.ToArray();
                }

                #endregion
            }

            return region;
        }
    }
}
