using System;
using System.IO;
using System.Text.RegularExpressions;
using DataRecovery.Core.Storage;

namespace DataRecovery.Core.Security
{
    public static class SecurityValidator
    {
        private static readonly Regex InvalidCharRegex = new(@"[\\/:*?""<>|]", RegexOptions.Compiled);

        /// <summary>
        /// Anti-Self-Overwrite Guard:
        /// Ensures that the user never selects an export directory residing on the drive being scanned.
        /// </summary>
        public static bool ValidateDestinationIsSafe(StorageDevice sourceDevice, string destinationPath, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                errorMessage = "Destination path cannot be empty.";
                return false;
            }

            try
            {
                string destFull = Path.GetFullPath(destinationPath);
                string destRoot = Path.GetPathRoot(destFull) ?? string.Empty;

                if (sourceDevice.DeviceType == StorageDeviceType.DiskImageFile)
                {
                    string imageDir = Path.GetDirectoryName(Path.GetFullPath(sourceDevice.DevicePath)) ?? string.Empty;
                    if (destFull.Equals(imageDir, StringComparison.OrdinalIgnoreCase) ||
                        destFull.StartsWith(imageDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        errorMessage = "Warning: The destination folder is inside the same folder as the raw disk image. Please choose an external or isolated destination folder.";
                        return true; // Soft warning for image files, but allowed
                    }
                }
                else
                {
                    // Physical or logical volume check
                    string sourceDriveLetter = sourceDevice.Name.Split(' ')[0].TrimEnd('\\');
                    if (destRoot.StartsWith(sourceDriveLetter, StringComparison.OrdinalIgnoreCase))
                    {
                        errorMessage = $"SECURITY VIOLATION: Destination path '{destinationPath}' is located on the source drive '{sourceDriveLetter}' being recovered! Writing to this drive will overwrite lost data.";
                        return false; // Hard block!
                    }

                    // Check Volume Serial Numbers
                    if (NativeMethods.GetVolumeInformation(destRoot, IntPtr.Zero, 0, out uint destSerial, out _, out _, IntPtr.Zero, 0))
                    {
                        if (sourceDevice.VolumeSerialNumber != 0 && sourceDevice.VolumeSerialNumber == destSerial)
                        {
                            errorMessage = $"SECURITY VIOLATION: Destination drive has identical Volume Serial Number (0x{destSerial:X8}) to the source device. Writing to it is prohibited to prevent data destruction.";
                            return false;
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Failed to validate destination safety: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Path Traversal & Filename Sanitizer:
        /// Removes relative traversal characters and invalid Windows file tokens.
        /// </summary>
        public static string SanitizeFileName(string rawFileName)
        {
            if (string.IsNullOrWhiteSpace(rawFileName)) return "recovered_file.bin";

            string cleaned = Path.GetFileName(rawFileName);
            cleaned = InvalidCharRegex.Replace(cleaned, "_");
            cleaned = cleaned.Replace("..", "_");

            string[] reservedNames = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };
            string nameWithoutExt = Path.GetFileNameWithoutExtension(cleaned).ToUpperInvariant();

            foreach (var reserved in reservedNames)
            {
                if (nameWithoutExt == reserved)
                {
                    cleaned = "_" + cleaned;
                    break;
                }
            }

            return cleaned;
        }
    }
}
