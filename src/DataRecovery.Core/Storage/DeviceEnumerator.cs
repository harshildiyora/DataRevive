using System;
using System.Collections.Generic;
using System.IO;

namespace DataRecovery.Core.Storage
{
    public static class DeviceEnumerator
    {
        public static List<StorageDevice> GetAvailableDevices()
        {
            var devices = new List<StorageDevice>();
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\').ToUpperInvariant() ?? "C:";

            // 1. Enumerate standard Logical Drives (C:, D:, USB Flash, etc.)
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;

                string driveLetter = drive.Name.TrimEnd('\\').ToUpperInvariant();
                bool isSys = driveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase);

                var dev = new StorageDevice
                {
                    Name = drive.Name.TrimEnd('\\'),
                    DriveLetter = driveLetter,
                    VolumeLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? (isSys ? "Local Disk" : "Volume") : drive.VolumeLabel,
                    DevicePath = $@"\\.\{driveLetter}",
                    FileSystem = string.IsNullOrEmpty(drive.DriveFormat) ? "NTFS" : drive.DriveFormat,
                    DeviceType = StorageDeviceType.LogicalDrive,
                    TotalSizeBytes = drive.TotalSize,
                    FreeSizeBytes = drive.TotalFreeSpace,
                    SectorSize = 512,
                    IsSystemDrive = isSys
                };

                // Query volume serial number for anti-overwrite matching
                if (NativeMethods.GetVolumeInformation(drive.Name, IntPtr.Zero, 0, out uint serial, out _, out _, IntPtr.Zero, 0))
                {
                    dev.VolumeSerialNumber = serial;
                }

                devices.Add(dev);
            }

            return devices;
        }

        public static StorageDevice CreateFromImageFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Disk image file not found", filePath);

            var fileInfo = new FileInfo(filePath);
            return new StorageDevice
            {
                Name = Path.GetFileName(filePath),
                DriveLetter = "IMG",
                VolumeLabel = "Disk Image",
                DevicePath = filePath,
                FileSystem = "RAW_IMAGE",
                DeviceType = StorageDeviceType.DiskImageFile,
                TotalSizeBytes = fileInfo.Length,
                FreeSizeBytes = 0,
                SectorSize = 512,
                VolumeSerialNumber = 0,
                IsSystemDrive = false
            };
        }
    }
}
