using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DataRecovery.Core.Signatures;
using DataRecovery.Core.Storage;

namespace DataRecovery.Core.Engine
{
    public class CarvingEngine
    {
        private readonly List<IFileSignature> _activeSignatures;
        private const int ChunkSize = 2 * 1024 * 1024;      // 2 MB scanning chunk
        private const int OverlapSize = 64 * 1024;         // 64 KB lookahead overlap for split signatures

        private static readonly byte[] MftMagic = new byte[] { (byte)'F', (byte)'I', (byte)'L', (byte)'E' };

        public CarvingEngine(IEnumerable<IFileSignature>? signatures = null)
        {
            _activeSignatures = signatures?.ToList() ?? SignatureCatalog.AllSignatures;
        }

        public async Task StartCarvingAsync(
            ISectorReader reader,
            ChannelWriter<CarvedFileResult> resultWriter,
            IProgress<RecoveryProgress>? progressReporter,
            CancellationToken cancellationToken)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            var stopwatch = Stopwatch.StartNew();
            long totalBytes = reader.TotalBytes;
            long currentPosition = 0;
            int filesDiscovered = 0;
            var discoveredOffsets = new HashSet<long>();

            try
            {
                // =====================================================================
                // PHASE 1: Direct Master File Table (MFT) & Filesystem Metadata Scan
                // Reads MFT from boot sector directly in seconds to recover original names
                // =====================================================================
                progressReporter?.Report(new RecoveryProgress
                {
                    BytesScanned = 0,
                    TotalBytes = totalBytes,
                    FilesDiscovered = filesDiscovered,
                    SpeedMBPerSec = 0,
                    ElapsedTime = stopwatch.Elapsed,
                    EstimatedTimeRemaining = TimeSpan.Zero,
                    StatusMessage = "Analyzing NTFS Master File Table ($MFT) for original file names..."
                });

                int mftCount = await ScanNtfsMftDirectAsync(reader, resultWriter, discoveredOffsets, cancellationToken);
                filesDiscovered += mftCount;

                // =====================================================================
                // PHASE 2: Deep Sector-Aligned Carving (for unallocated orphan files)
                // =====================================================================
                byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + OverlapSize);
                long lastReportTime = 0;
                int overlapBytes = 0;

                try
                {
                    reader.Seek(0);

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (totalBytes > 0 && currentPosition >= totalBytes)
                        {
                            break; // Do not scan beyond 100%!
                        }

                        int bytesToRead = (int)Math.Min(ChunkSize, totalBytes > 0 ? totalBytes - currentPosition : ChunkSize);
                        if (bytesToRead <= 0) break;

                        int bytesRead = reader.Read(buffer, overlapBytes, bytesToRead);

                        if (bytesRead <= 0 && overlapBytes == 0)
                        {
                            break; // Reached end of device/image
                        }

                        int totalInBuffer = overlapBytes + bytesRead;
                        long windowBasePosition = currentPosition - overlapBytes;

                        // Execute sector-aligned fast scan over the buffer window
                        var discoveredInChunk = ScanChunk(buffer, totalInBuffer, windowBasePosition, discoveredOffsets, reader);

                        foreach (var result in discoveredInChunk)
                        {
                            filesDiscovered++;
                            await resultWriter.WriteAsync(result, cancellationToken);
                        }

                        currentPosition += bytesRead;

                        // Carry over the overlap tail
                        if (bytesRead > 0)
                        {
                            overlapBytes = Math.Min(OverlapSize, totalInBuffer);
                            Array.Copy(buffer, totalInBuffer - overlapBytes, buffer, 0, overlapBytes);
                        }
                        else
                        {
                            overlapBytes = 0;
                        }

                        // Throttle progress reporting cleanly
                        if (progressReporter != null && (stopwatch.ElapsedMilliseconds - lastReportTime >= 250))
                        {
                            lastReportTime = stopwatch.ElapsedMilliseconds;
                            double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                            double speed = elapsedSec > 0 ? (currentPosition / (1024.0 * 1024.0)) / elapsedSec : 0;
                            double remainingBytes = Math.Max(0, totalBytes - currentPosition);
                            TimeSpan eta = speed > 0 ? TimeSpan.FromSeconds(remainingBytes / (speed * 1024 * 1024)) : TimeSpan.Zero;

                            progressReporter.Report(new RecoveryProgress
                            {
                                BytesScanned = Math.Min(currentPosition, totalBytes),
                                TotalBytes = totalBytes,
                                FilesDiscovered = filesDiscovered,
                                SpeedMBPerSec = speed,
                                ElapsedTime = stopwatch.Elapsed,
                                EstimatedTimeRemaining = eta,
                                StatusMessage = $"Deep scanning sector 0x{currentPosition:X10}..."
                            });
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            finally
            {
                resultWriter.Complete();

                progressReporter?.Report(new RecoveryProgress
                {
                    BytesScanned = totalBytes,
                    TotalBytes = totalBytes,
                    FilesDiscovered = filesDiscovered,
                    SpeedMBPerSec = 0,
                    ElapsedTime = stopwatch.Elapsed,
                    EstimatedTimeRemaining = TimeSpan.Zero,
                    StatusMessage = cancellationToken.IsCancellationRequested ? "Scan Stopped" : "Scan Completed (100%)"
                });
            }
        }

        /// <summary>
        /// Reads NTFS Volume Boot Record (Sector 0), locates $MFT cluster, and directly parses MFT records.
        /// </summary>
        private async Task<int> ScanNtfsMftDirectAsync(
            ISectorReader reader,
            ChannelWriter<CarvedFileResult> resultWriter,
            HashSet<long> discoveredOffsets,
            CancellationToken cancellationToken)
        {
            int discovered = 0;
            try
            {
                byte[] bootSector = new byte[512];
                reader.Seek(0);
                int bootRead = reader.Read(bootSector, 0, 512);

                if (bootRead < 512) return 0;

                // Check for NTFS signature
                if (bootSector[3] != (byte)'N' || bootSector[4] != (byte)'T' ||
                    bootSector[5] != (byte)'F' || bootSector[6] != (byte)'S')
                {
                    return 0; // Not an NTFS volume
                }

                ushort bytesPerSector = BitConverter.ToUInt16(bootSector, 0x0B);
                if (bytesPerSector == 0) bytesPerSector = 512;
                byte sectorsPerCluster = bootSector[0x0D];
                if (sectorsPerCluster == 0) sectorsPerCluster = 8;
                int bytesPerCluster = bytesPerSector * sectorsPerCluster;

                long mftStartLcn = BitConverter.ToInt64(bootSector, 0x30);
                long mftByteOffset = mftStartLcn * bytesPerCluster;

                if (mftByteOffset <= 0 || (reader.TotalBytes > 0 && mftByteOffset >= reader.TotalBytes))
                {
                    return 0;
                }

                // Read MFT table in 256 KB blocks (256 records of 1024 bytes per block)
                int blockSize = 256 * 1024;
                byte[] mftBlock = new byte[blockSize];
                long currentMftOffset = mftByteOffset;
                int consecutiveEmptyBlocks = 0;

                // Scan up to 64 MB of MFT table (up to 65,536 files)
                long maxMftScanBytes = 64 * 1024 * 1024;
                long totalMftBytesRead = 0;
                var directoryMap = new Dictionary<ulong, (string Name, ulong ParentRef)>();

                while (!cancellationToken.IsCancellationRequested && totalMftBytesRead < maxMftScanBytes)
                {
                    reader.Seek(currentMftOffset);
                    int read = reader.Read(mftBlock, 0, blockSize);
                    if (read < 1024) break;

                    totalMftBytesRead += read;
                    currentMftOffset += read;

                    var blockResults = ParseMftRecordsInBlock(mftBlock, read, bytesPerCluster, discoveredOffsets, directoryMap, out int validRecordsInBlock);
                    foreach (var result in blockResults)
                    {
                        discovered++;
                        await resultWriter.WriteAsync(result, cancellationToken);
                    }

                    if (validRecordsInBlock == 0)
                    {
                        consecutiveEmptyBlocks++;
                        if (consecutiveEmptyBlocks >= 4) break; // Reached end of initial MFT table
                    }
                    else
                    {
                        consecutiveEmptyBlocks = 0;
                    }
                }
            }
            catch
            {
                // Graceful fallback to raw sector carving
            }

            return discovered;
        }

        private static List<CarvedFileResult> ParseMftRecordsInBlock(
            byte[] mftBlock, 
            int read, 
            int bytesPerCluster, 
            HashSet<long> discoveredOffsets, 
            Dictionary<ulong, (string Name, ulong ParentRef)> directoryMap,
            out int validRecordsInBlock)
        {
            var results = new List<CarvedFileResult>();
            int recordsInBlock = read / 1024;
            validRecordsInBlock = 0;

            for (int r = 0; r < recordsInBlock; r++)
            {
                ReadOnlySpan<byte> recordBytes = mftBlock.AsSpan(r * 1024, 1024);
                if (recordBytes[0] == (byte)'F' && recordBytes[1] == (byte)'I' &&
                    recordBytes[2] == (byte)'L' && recordBytes[3] == (byte)'E')
                {
                    validRecordsInBlock++;
                    if (NtfsMftParser.TryParseRecord(recordBytes, out var mftRec, 0, bytesPerCluster))
                    {
                        if (mftRec != null && !string.IsNullOrEmpty(mftRec.FileName))
                        {
                            ulong parentRef = mftRec.ParentDirectoryRef & 0x0000FFFFFFFFFFFF;
                            if (mftRec.IsDirectory)
                            {
                                directoryMap[mftRec.RecordNumber] = (mftRec.FileName, parentRef);
                            }

                            var result = NtfsMftParser.ConvertToCarvedResult(mftRec, 0, bytesPerCluster);
                            if (result != null)
                            {
                                string dirPath = ResolveDirectoryPath(parentRef, directoryMap);
                                result.OriginalPath = Path.Combine(dirPath, result.FileName);

                                if (result.SectorOffset > 0)
                                {
                                    discoveredOffsets.Add(result.SectorOffset);
                                }
                                results.Add(result);
                            }
                        }
                    }
                }
            }

            return results;
        }

        public static string ResolveDirectoryPath(ulong parentRecordNum, Dictionary<ulong, (string Name, ulong ParentRef)> dirMap)
        {
            if (parentRecordNum == 5 || parentRecordNum == 0) return @"\";
            var parts = new List<string>();
            ulong current = parentRecordNum;
            int depth = 0;
            while (current != 5 && current != 0 && depth < 20 && dirMap.TryGetValue(current, out var entry))
            {
                parts.Add(entry.Name);
                current = entry.ParentRef;
                depth++;
            }
            parts.Reverse();
            return @"\" + string.Join(@"\", parts);
        }

        /// <summary>
        /// Sector-aligned fast signature carver. Scans only on 512-byte sector boundaries for maximum accuracy and speed.
        /// </summary>
        private List<CarvedFileResult> ScanChunk(
            byte[] buffer, 
            int totalInBuffer, 
            long windowBasePosition, 
            HashSet<long> discoveredOffsets, 
            ISectorReader? reader = null)
        {
            var results = new List<CarvedFileResult>();
            ReadOnlySpan<byte> window = buffer.AsSpan(0, totalInBuffer);

            for (int i = 0; i < totalInBuffer - 16; i++)
            {
                long fileStartOffset = windowBasePosition + i;

                // Sector alignment: files on disk start on 512-byte sector boundaries
                long remainder = fileStartOffset % 512;
                if (remainder != 0)
                {
                    i += (int)(512 - remainder) - 1;
                    continue;
                }

                ReadOnlySpan<byte> slice = window.Slice(i);

                // Check for File Signature Carving (JPEG, PNG, PDF, ZIP/DOCX/XLSX, MP4, etc.)
                foreach (var sig in _activeSignatures)
                {
                    if (MatchesHeader(slice, sig.Header))
                    {
                        if (!discoveredOffsets.Contains(fileStartOffset))
                        {
                            long estimatedLength = sig.TryDetermineLength(slice);
                            string fileExt = sig.Extension;
                            var fileCategory = sig.Category;

                            // Deep multi-megabyte ZIP / Office document resolution
                            if (sig.Extension == "zip")
                            {
                                var (zipLen, resolvedExt, resolvedCat) = ResolveZipDetails(reader, fileStartOffset, slice);
                                if (zipLen > 0)
                                {
                                    estimatedLength = zipLen;
                                    fileExt = resolvedExt;
                                    fileCategory = resolvedCat;
                                }
                            }

                            if (estimatedLength > 0)
                            {
                                discoveredOffsets.Add(fileStartOffset);

                                results.Add(new CarvedFileResult
                                {
                                    FileName = $"recovered_0x{fileStartOffset:X10}.{fileExt}",
                                    OriginalFileName = $"recovered_0x{fileStartOffset:X10}.{fileExt}",
                                    OriginalPath = $@"Raw Carved Files\{fileCategory}\{fileExt.ToUpperInvariant()}\recovered_0x{fileStartOffset:X10}.{fileExt}",
                                    Extension = fileExt,
                                    Category = fileCategory,
                                    SectorOffset = fileStartOffset,
                                    FileSizeBytes = estimatedLength,
                                    Status = "Carved (Signature)",
                                    DiscoveredTime = DateTime.Now
                                });

                                // Fast-forward past minimum file size
                                i += (int)Math.Min(sig.MinSizeBytes, slice.Length - 1);
                                break;
                            }
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Validates ZIP and Office OpenXML archives and finds the End of Central Directory (EOCD).
        /// Scans ahead in the physical sector stream to support multi-megabyte archives (> 2 MB)
        /// ensuring full compatibility with Windows Explorer's built-in Compressed Folders extractor.
        /// </summary>
        private static (long Length, string Extension, FileCategory Category) ResolveZipDetails(
            ISectorReader? reader, 
            long fileStartOffset, 
            ReadOnlySpan<byte> slice)
        {
            string ext = "zip";
            var cat = FileCategory.Documents;

            // 1. Inspect first entry to detect Office OpenXML documents (.docx, .xlsx, .pptx)
            if (slice.Length >= 30)
            {
                ushort fnLen = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(26, 2));
                if (fnLen > 0 && 30 + fnLen <= slice.Length)
                {
                    string firstEntry = Encoding.UTF8.GetString(slice.Slice(30, fnLen));
                    if (firstEntry.StartsWith("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                    {
                        if (slice.IndexOf(Encoding.UTF8.GetBytes("word/")) >= 0) ext = "docx";
                        else if (slice.IndexOf(Encoding.UTF8.GetBytes("xl/")) >= 0) ext = "xlsx";
                        else if (slice.IndexOf(Encoding.UTF8.GetBytes("ppt/")) >= 0) ext = "pptx";
                    }
                }
            }

            byte[] eocdMarker = new byte[] { 0x50, 0x4B, 0x05, 0x06 };

            // 2. Check for EOCD within current buffer window
            int eocdIdx = slice.LastIndexOf(eocdMarker);
            if (eocdIdx >= 0 && eocdIdx + 22 <= slice.Length)
            {
                uint cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(eocdIdx + 16, 4));
                uint cdSize = BinaryPrimitives.ReadUInt32LittleEndian(slice.Slice(eocdIdx + 12, 4));
                ushort commentLen = BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(eocdIdx + 20, 2));

                if (cdOffset + cdSize == eocdIdx)
                {
                    long zipLen = eocdIdx + 22 + commentLen;
                    return (zipLen, ext, cat);
                }
            }

            // 3. Search ahead in reader for multi-megabyte archives (> 2 MB)
            if (reader != null)
            {
                long savedPos = reader.CurrentPosition;
                try
                {
                    long searchStart = fileStartOffset + Math.Max(0, slice.Length - 32);
                    long maxSearchBytes = 500 * 1024 * 1024; // Search up to 500 MB
                    long totalSearched = 0;
                    byte[] searchBlock = new byte[1024 * 1024]; // 1 MB block

                    while (totalSearched < maxSearchBytes)
                    {
                        long currentPos = searchStart + totalSearched;
                        if (reader.TotalBytes > 0 && currentPos >= reader.TotalBytes) break;

                        reader.Seek(currentPos);
                        int read = reader.Read(searchBlock, 0, searchBlock.Length);
                        if (read < 22) break;

                        var span = searchBlock.AsSpan(0, read);
                        int blockEocdIdx = span.LastIndexOf(eocdMarker);
                        if (blockEocdIdx >= 0 && blockEocdIdx + 22 <= read)
                        {
                            long absoluteEocdPos = currentPos + blockEocdIdx;
                            long relativeEocdPos = absoluteEocdPos - fileStartOffset;

                            uint cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(blockEocdIdx + 16, 4));
                            uint cdSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(blockEocdIdx + 12, 4));
                            ushort commentLen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(blockEocdIdx + 20, 2));

                            if (cdOffset + cdSize == relativeEocdPos)
                            {
                                long totalZipLen = relativeEocdPos + 22 + commentLen;
                                return (totalZipLen, ext, cat);
                            }
                        }

                        totalSearched += read - 32;
                    }
                }
                finally
                {
                    reader.Seek(savedPos);
                }
            }

            return (-1, ext, cat);
        }

        private static bool MatchesHeader(ReadOnlySpan<byte> data, byte[] header)
        {
            if (data.Length < header.Length) return false;
            return data.Slice(0, header.Length).SequenceEqual(header);
        }
    }
}
