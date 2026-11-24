using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DataRecovery.Core.Engine;

namespace DataRecovery.Core.Storage
{
    public static class RecycleBinScanner
    {
        public static List<CarvedFileResult> ScanRecycleBinForDrive(string driveLetter)
        {
            var results = new List<CarvedFileResult>();
            try
            {
                string cleanLetter = driveLetter.TrimEnd('\\', '/', ':') + ":";
                string recycleRoot = Path.Combine(cleanLetter + "\\", "$Recycle.Bin");

                if (!Directory.Exists(recycleRoot)) return results;

                var dirInfo = new DirectoryInfo(recycleRoot);
                // Scan user SID directories
                foreach (var sidDir in dirInfo.EnumerateDirectories())
                {
                    try
                    {
                        var iFiles = sidDir.EnumerateFiles("$I*.*");
                        foreach (var iFile in iFiles)
                        {
                            try
                            {
                                string originalName = string.Empty;
                                string originalPath = string.Empty;
                                long originalSize = 0;
                                DateTime? deletionTime = null;

                                using (var fs = iFile.OpenRead())
                                using (var br = new BinaryReader(fs))
                                {
                                    if (fs.Length < 28) continue;
                                    long headerVersion = br.ReadInt64();
                                    originalSize = br.ReadInt64();
                                    long fileTime = br.ReadInt64();
                                    if (fileTime > 0)
                                    {
                                        try { deletionTime = DateTime.FromFileTimeUtc(fileTime).ToLocalTime(); } catch { }
                                    }

                                    if (headerVersion == 1 && fs.Length >= 544)
                                    {
                                        // Version 1: 520 bytes UTF-16
                                        byte[] pathBytes = br.ReadBytes(520);
                                        originalPath = Encoding.Unicode.GetString(pathBytes).TrimEnd('\0');
                                    }
                                    else if (headerVersion >= 2)
                                    {
                                        // Version 2: 4-byte character count, followed by UTF-16 chars
                                        int charCount = br.ReadInt32();
                                        if (charCount > 0 && charCount * 2 <= fs.Length - 28)
                                        {
                                            byte[] pathBytes = br.ReadBytes(charCount * 2);
                                            originalPath = Encoding.Unicode.GetString(pathBytes).TrimEnd('\0');
                                        }
                                    }
                                }

                                if (string.IsNullOrEmpty(originalPath))
                                {
                                    originalPath = iFile.Name.Substring(2); // fallback
                                }

                                originalName = Path.GetFileName(originalPath);
                                string ext = Path.GetExtension(originalName).TrimStart('.').ToLowerInvariant();

                                // Corresponding $R file
                                string rFileName = "$R" + iFile.Name.Substring(2);
                                string rFilePath = Path.Combine(sidDir.FullName, rFileName);

                                byte[]? fileData = null;
                                if (File.Exists(rFilePath))
                                {
                                    try
                                    {
                                        var rInfo = new FileInfo(rFilePath);
                                        if (originalSize <= 0) originalSize = rInfo.Length;
                                        if (originalSize > 0 && originalSize <= 20 * 1024 * 1024) // up to 20MB resident memory
                                        {
                                            fileData = File.ReadAllBytes(rFilePath);
                                        }
                                    }
                                    catch { }
                                }

                                results.Add(new CarvedFileResult
                                {
                                    FileName = originalName,
                                    OriginalFileName = originalName,
                                    OriginalPath = originalPath,
                                    Extension = ext,
                                    Category = NtfsMftParser.CategorizeExtension(ext),
                                    FileSizeBytes = originalSize,
                                    DeletedDate = deletionTime ?? iFile.LastWriteTime,
                                    Status = "Recycle Bin",
                                    IsMetadataRecovered = true,
                                    IsResident = fileData != null,
                                    ResidentData = fileData,
                                    DiscoveredTime = DateTime.Now
                                });
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return results;
        }
    }
}
