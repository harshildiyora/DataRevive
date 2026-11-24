using System;
using System.IO;
using System.Threading.Tasks;

namespace DataRecovery.Core.Security
{
    public enum ConflictChoice
    {
        KeepBothRename,
        Overwrite,
        Skip
    }

    public class ConflictResolutionRequest
    {
        public string TargetDirectory { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FullTargetFilePath { get; set; } = string.Empty;

        public long ExistingFileSize { get; set; }
        public DateTime ExistingFileDate { get; set; }

        public long IncomingFileSize { get; set; }
        public DateTime? IncomingFileDate { get; set; }
        public string IncomingStatus { get; set; } = string.Empty;

        public string ExistingSizeDisplay => Storage.StorageDevice.FormatBytes(ExistingFileSize);
        public string IncomingSizeDisplay => Storage.StorageDevice.FormatBytes(IncomingFileSize);
    }

    public delegate Task<(ConflictChoice Choice, bool ApplyToAll)> ConflictResolverDelegate(ConflictResolutionRequest request);

    public static class ConflictHelper
    {
        public static string GetUniqueRenamePath(string targetDirectory, string fileName)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int counter = 1;

            string candidate = Path.Combine(targetDirectory, $"{nameWithoutExt} ({counter}){ext}");
            while (File.Exists(candidate))
            {
                counter++;
                candidate = Path.Combine(targetDirectory, $"{nameWithoutExt} ({counter}){ext}");
            }
            return candidate;
        }
    }
}
