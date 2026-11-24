using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DataRecovery.Core.Engine;
using DataRecovery.Core.Signatures;

namespace DataRecovery.Core.Storage
{
    public class NtfsMftRecord
    {
        public uint RecordNumber { get; set; }
        public bool IsInUse { get; set; }
        public bool IsDirectory { get; set; }
        public string FileName { get; set; } = string.Empty;
        public ulong ParentDirectoryRef { get; set; }
        public long RealFileSize { get; set; }
        public DateTime? CreationTime { get; set; }
        public DateTime? ModificationTime { get; set; }
        public bool HasData { get; set; }
        public bool IsResidentData { get; set; }
        public byte[]? ResidentData { get; set; }
        public List<DataRun> DataRuns { get; } = new();
    }

    public struct DataRun
    {
        public long StartingLcn;
        public long ClusterCount;
    }

    public static class NtfsMftParser
    {
        private static readonly byte[] FileMagic = new byte[] { (byte)'F', (byte)'I', (byte)'L', (byte)'E' };

        public static bool TryParseRecord(ReadOnlySpan<byte> recordBytes, out NtfsMftRecord? parsedRecord, long sectorOffset = 0, int bytesPerCluster = 4096)
        {
            parsedRecord = null;
            if (recordBytes.Length < 1024) return false;

            // Check signature "FILE"
            if (recordBytes[0] != (byte)'F' || recordBytes[1] != (byte)'I' ||
                recordBytes[2] != (byte)'L' || recordBytes[3] != (byte)'E')
            {
                return false;
            }

            // Copy to working buffer to apply fixups safely
            byte[] buffer = recordBytes.Slice(0, 1024).ToArray();

            // Fixup handling
            ushort updateSeqOffset = BitConverter.ToUInt16(buffer, 0x04);
            ushort updateSeqCount = BitConverter.ToUInt16(buffer, 0x06);

            if (updateSeqOffset + (updateSeqCount * 2) <= buffer.Length && updateSeqCount >= 2)
            {
                ushort usn = BitConverter.ToUInt16(buffer, updateSeqOffset);
                for (int s = 1; s < updateSeqCount && s <= 2; s++)
                {
                    int sectorEnd = s * 512 - 2;
                    ushort sectorVal = BitConverter.ToUInt16(buffer, sectorEnd);
                    if (sectorVal == usn)
                    {
                        // Replace fixup with original byte pair
                        buffer[sectorEnd] = buffer[updateSeqOffset + (s * 2)];
                        buffer[sectorEnd + 1] = buffer[updateSeqOffset + (s * 2) + 1];
                    }
                }
            }

            ushort flags = BitConverter.ToUInt16(buffer, 0x16);
            bool inUse = (flags & 0x0001) != 0;
            bool isDirectory = (flags & 0x0002) != 0;
            ushort firstAttrOffset = BitConverter.ToUInt16(buffer, 0x14);

            uint recordNumber = BitConverter.ToUInt32(buffer, 0x2C);

            var rec = new NtfsMftRecord
            {
                RecordNumber = recordNumber,
                IsInUse = inUse,
                IsDirectory = isDirectory
            };

            // Traverse attributes
            int offset = firstAttrOffset;
            string bestFileName = string.Empty;
            byte bestNamespace = 0; // Prefer Win32 / POSIX over DOS

            while (offset + 8 <= buffer.Length)
            {
                uint attrType = BitConverter.ToUInt32(buffer, offset);
                if (attrType == 0xFFFFFFFF || attrType == 0) break;

                uint attrLength = BitConverter.ToUInt32(buffer, offset + 4);
                if (attrLength < 8 || offset + attrLength > buffer.Length) break;

                byte nonResident = buffer[offset + 8];

                // 0x30 = $FILE_NAME
                if (attrType == 0x30 && nonResident == 0)
                {
                    ushort contentOffset = BitConverter.ToUInt16(buffer, offset + 0x14);
                    int fnBase = offset + contentOffset;

                    if (fnBase + 66 <= buffer.Length)
                    {
                        ulong parentRef = BitConverter.ToUInt64(buffer, fnBase);
                        long cTime = BitConverter.ToInt64(buffer, fnBase + 0x08);
                        long mTime = BitConverter.ToInt64(buffer, fnBase + 0x10);
                        long realSize = BitConverter.ToInt64(buffer, fnBase + 0x30);
                        byte nameLen = buffer[fnBase + 0x40];
                        byte nameNs = buffer[fnBase + 0x41];

                        if (fnBase + 0x42 + (nameLen * 2) <= buffer.Length && nameLen > 0)
                        {
                            string name = Encoding.Unicode.GetString(buffer, fnBase + 0x42, nameLen * 2);

                            // Select Win32 or Win32&DOS name if multiple names exist
                            if (string.IsNullOrEmpty(bestFileName) || nameNs == 1 || nameNs == 3)
                            {
                                bestFileName = name;
                                bestNamespace = nameNs;
                                rec.ParentDirectoryRef = parentRef;
                                rec.RealFileSize = realSize;
                                if (cTime > 0) rec.CreationTime = DateTime.FromFileTimeUtc(cTime).ToLocalTime();
                                if (mTime > 0) rec.ModificationTime = DateTime.FromFileTimeUtc(mTime).ToLocalTime();
                            }
                        }
                    }
                }
                // 0x80 = $DATA
                else if (attrType == 0x80)
                {
                    byte nameLength = buffer[offset + 9];
                    // CRITICAL: Only the unnamed primary $DATA stream (nameLength == 0) represents the actual file content!
                    // Named streams (nameLength > 0) are Alternate Data Streams (ADS) like Zone.Identifier (26-89 bytes).
                    if (nameLength != 0)
                    {
                        offset += (int)attrLength;
                        continue;
                    }

                    rec.HasData = true;
                    if (nonResident == 0)
                    {
                        // Resident data: file contents are inside this 1024-byte record!
                        uint valLen = BitConverter.ToUInt32(buffer, offset + 0x10);
                        ushort valOffset = BitConverter.ToUInt16(buffer, offset + 0x14);
                        int dataStart = offset + valOffset;

                        if (dataStart + valLen <= buffer.Length)
                        {
                            rec.IsResidentData = true;
                            rec.RealFileSize = valLen;
                            rec.ResidentData = new byte[valLen];
                            Array.Copy(buffer, dataStart, rec.ResidentData, 0, (int)valLen);
                        }
                    }
                    else
                    {
                        // Non-resident data: parse runlists
                        rec.IsResidentData = false;
                        long realSize = BitConverter.ToInt64(buffer, offset + 0x30);
                        if (realSize > 0) rec.RealFileSize = realSize;
                        else
                        {
                            long allocSize = BitConverter.ToInt64(buffer, offset + 0x28);
                            if (allocSize > 0) rec.RealFileSize = allocSize;
                        }

                        ushort runOffset = BitConverter.ToUInt16(buffer, offset + 0x20);
                        int runIdx = offset + runOffset;
                        long currentLcn = 0;

                        while (runIdx < offset + attrLength && buffer[runIdx] != 0)
                        {
                            byte header = buffer[runIdx++];
                            int lenFieldLen = header & 0x0F;
                            int offsetFieldLen = (header >> 4) & 0x0F;

                            if (lenFieldLen <= 0 || lenFieldLen > 8 || offsetFieldLen > 8 || runIdx + lenFieldLen + offsetFieldLen > buffer.Length)
                                break;

                            long runLen = 0;
                            for (int b = 0; b < lenFieldLen; b++)
                                runLen |= (long)buffer[runIdx++] << (b * 8);

                            long runOffsetDelta = 0;
                            for (int b = 0; b < offsetFieldLen; b++)
                                runOffsetDelta |= (long)buffer[runIdx++] << (b * 8);

                            // Sign extend if highest bit is set
                            if (offsetFieldLen > 0 && (buffer[runIdx - 1] & 0x80) != 0)
                            {
                                for (int b = offsetFieldLen; b < 8; b++)
                                    runOffsetDelta |= (long)0xFF << (b * 8);
                            }

                            currentLcn += runOffsetDelta;
                            rec.DataRuns.Add(new DataRun { StartingLcn = currentLcn, ClusterCount = runLen });
                        }

                        if (rec.RealFileSize <= 0 && rec.DataRuns.Count > 0)
                        {
                            long totalClusters = 0;
                            foreach (var dr in rec.DataRuns) totalClusters += dr.ClusterCount;
                            if (totalClusters > 0) rec.RealFileSize = totalClusters * bytesPerCluster;
                        }
                    }
                }

                offset += (int)attrLength;
            }

            rec.FileName = bestFileName;
            parsedRecord = rec;
            return !string.IsNullOrEmpty(rec.FileName);
        }

        public static CarvedFileResult? ConvertToCarvedResult(NtfsMftRecord record, long volumeSectorOffset = 0, int bytesPerCluster = 4096)
        {
            if (string.IsNullOrEmpty(record.FileName)) return null;

            // Skip internal NTFS system metadata records like $MFT, $LogFile, $Volume, $Bitmap
            if (record.FileName.StartsWith("$") && record.RecordNumber < 16) return null;

            string ext = Path.GetExtension(record.FileName).TrimStart('.').ToLowerInvariant();
            var category = CategorizeExtension(ext);

            long fileSectorOffset = -1;
            if (record.DataRuns.Count > 0 && record.DataRuns[0].StartingLcn > 0)
            {
                long clusterStart = record.DataRuns[0].StartingLcn;
                fileSectorOffset = volumeSectorOffset + (clusterStart * bytesPerCluster);
            }

            var result = new CarvedFileResult
            {
                FileName = record.FileName,
                OriginalFileName = record.FileName,
                OriginalPath = record.IsDirectory ? $"[Directory] {record.FileName}" : record.FileName,
                Extension = ext,
                Category = category,
                SectorOffset = fileSectorOffset,
                FileSizeBytes = record.RealFileSize,
                CreatedDate = record.CreationTime,
                DeletedDate = record.ModificationTime,
                Status = record.IsInUse ? "Active (MFT)" : "Deleted (MFT)",
                IsMetadataRecovered = true,
                IsResident = record.IsResidentData,
                ResidentData = record.ResidentData,
                DiscoveredTime = DateTime.Now,
                BytesPerCluster = bytesPerCluster
            };

            if (record.DataRuns.Count > 0)
            {
                result.DataRuns.AddRange(record.DataRuns);
            }

            return result;
        }

        public static FileCategory CategorizeExtension(string ext)
        {
            return ext switch
            {
                "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "svg" or "ico" or "tiff" => FileCategory.Images,
                "txt" or "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "csv" or "rtf" or "md" or "log" or "json" or "xml" or "html" => FileCategory.Documents,
                "mp4" or "mkv" or "avi" or "mov" or "wmv" or "flv" or "webm" => FileCategory.Video,
                "mp3" or "wav" or "flac" or "aac" or "ogg" or "m4a" or "wma" => FileCategory.Audio,
                "zip" or "rar" or "7z" or "tar" or "gz" or "iso" => FileCategory.Archives,
                _ => FileCategory.Documents
            };
        }
    }
}
