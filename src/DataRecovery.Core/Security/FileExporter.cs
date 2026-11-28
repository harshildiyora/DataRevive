using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataRecovery.Core.Engine;
using DataRecovery.Core.Storage;

namespace DataRecovery.Core.Security
{
    public class FileExporter
    {
        private ConflictChoice? _rememberedChoice;

        public void ResetConflictMemory()
        {
            _rememberedChoice = null;
        }

        public async Task<string?> ExportFileAsync(
            ISectorReader reader,
            CarvedFileResult fileResult,
            string destinationDirectory,
            CancellationToken cancellationToken,
            ConflictResolverDelegate? conflictResolver = null,
            bool preserveFolderStructure = true)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (fileResult == null) throw new ArgumentNullException(nameof(fileResult));

            string targetDir;
            if (preserveFolderStructure && !string.IsNullOrWhiteSpace(fileResult.OriginalPath) && fileResult.OriginalPath.Contains('\\'))
            {
                // Reconstruct exact directory structure e.g. "R:\Projects\public\assets" -> "Projects\public\assets"
                string clean = fileResult.OriginalPath.Trim();
                if (clean.Length >= 2 && clean[1] == ':') clean = clean.Substring(2);
                clean = clean.TrimStart('\\', '/');

                string? dir = Path.GetDirectoryName(clean);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    string safeDir = SecurityValidator.SanitizeRelativeDirectoryPath(dir);
                    targetDir = !string.IsNullOrWhiteSpace(safeDir)
                        ? Path.Combine(destinationDirectory, safeDir)
                        : destinationDirectory;
                }
                else
                {
                    targetDir = destinationDirectory;
                }
            }
            else
            {
                // Group by Category (Documents, Images, Video, etc.)
                targetDir = Path.Combine(destinationDirectory, fileResult.Category.ToString());
            }

            Directory.CreateDirectory(targetDir);

            string chosenName = !string.IsNullOrEmpty(fileResult.OriginalFileName) 
                ? fileResult.OriginalFileName 
                : fileResult.FileName;

            string safeFileName = SecurityValidator.SanitizeFileName(chosenName);
            string fullDestPath = Path.Combine(targetDir, safeFileName);

            // Check if file conflict occurs
            if (File.Exists(fullDestPath))
            {
                ConflictChoice choice = _rememberedChoice ?? ConflictChoice.KeepBothRename;

                if (_rememberedChoice == null && conflictResolver != null)
                {
                    var fileInfo = new FileInfo(fullDestPath);
                    var req = new ConflictResolutionRequest
                    {
                        TargetDirectory = targetDir,
                        FileName = safeFileName,
                        FullTargetFilePath = fullDestPath,
                        ExistingFileSize = fileInfo.Length,
                        ExistingFileDate = fileInfo.LastWriteTime,
                        IncomingFileSize = fileResult.FileSizeBytes,
                        IncomingFileDate = fileResult.DeletedDate ?? fileResult.DiscoveredTime,
                        IncomingStatus = fileResult.Status
                    };

                    var (userChoice, applyToAll) = await conflictResolver(req);
                    choice = userChoice;
                    if (applyToAll)
                    {
                        _rememberedChoice = choice;
                    }
                }

                switch (choice)
                {
                    case ConflictChoice.Skip:
                        return null; // Skip file export

                    case ConflictChoice.KeepBothRename:
                        fullDestPath = ConflictHelper.GetUniqueRenamePath(targetDir, safeFileName);
                        break;

                    case ConflictChoice.Overwrite:
                        // Proceed with fullDestPath, will overwrite
                        break;
                }
            }

            // Write File Contents (either resident buffer or streamed sector reads)
            using var sha256 = SHA256.Create();

            if (fileResult.IsResident && fileResult.ResidentData != null)
            {
                if (fileResult.FileSizeBytes <= 0) fileResult.FileSizeBytes = fileResult.ResidentData.Length;
                await File.WriteAllBytesAsync(fullDestPath, fileResult.ResidentData, cancellationToken);
                byte[] hashBytes = sha256.ComputeHash(fileResult.ResidentData);
                fileResult.Sha256Hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
            else
            {
                if (fileResult.SectorOffset < 0)
                {
                    throw new InvalidOperationException($"Cannot export non-resident file {fileResult.FileName} without valid sector offset.");
                }

                long bytesRemaining = fileResult.FileSizeBytes;
                if (bytesRemaining <= 0)
                {
                    bytesRemaining = 64 * 1024;
                }
                byte[] buffer = new byte[64 * 1024];

                using var destStream = new FileStream(fullDestPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

                if (fileResult.DataRuns != null && fileResult.DataRuns.Count > 1)
                {
                    // Multi-run (fragmented) file streaming export
                    foreach (var dr in fileResult.DataRuns)
                    {
                        if (bytesRemaining <= 0 || cancellationToken.IsCancellationRequested) break;
                        long runOffset = dr.StartingLcn * fileResult.BytesPerCluster;
                        long runBytes = Math.Min(bytesRemaining, dr.ClusterCount * fileResult.BytesPerCluster);
                        reader.Seek(runOffset);

                        while (runBytes > 0 && !cancellationToken.IsCancellationRequested)
                        {
                            int toRead = (int)Math.Min(buffer.Length, runBytes);
                            int read = reader.Read(buffer, 0, toRead);
                            if (read <= 0) break;

                            await destStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            sha256.TransformBlock(buffer, 0, read, null, 0);
                            runBytes -= read;
                            bytesRemaining -= read;
                        }
                    }
                }
                else
                {
                    // Single run or contiguous carved sector export
                    reader.Seek(fileResult.SectorOffset);
                    while (bytesRemaining > 0 && !cancellationToken.IsCancellationRequested)
                    {
                        int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                        int read = reader.Read(buffer, 0, toRead);
                        if (read <= 0) break;

                        await destStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        sha256.TransformBlock(buffer, 0, read, null, 0);
                        bytesRemaining -= read;
                    }
                }

                await destStream.FlushAsync(cancellationToken);
                destStream.Close();

                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                fileResult.Sha256Hash = BitConverter.ToString(sha256.Hash ?? Array.Empty<byte>()).Replace("-", "").ToLowerInvariant();
            }

            fileResult.IsExported = true;
            fileResult.ExportedPath = fullDestPath;

            return fullDestPath;
        }

        public async Task WriteManifestAsync(string destinationDirectory, System.Collections.Generic.IEnumerable<CarvedFileResult> files)
        {
            string manifestPath = Path.Combine(destinationDirectory, "recovery_manifest.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(files, options);
            await File.WriteAllTextAsync(manifestPath, json);
        }
    }
}
