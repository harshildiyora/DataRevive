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
                try
                {
                    if (!drive.IsReady) continue;

                    string driveLetter = drive.Name.TrimEnd('\\').ToUpperInvariant();
                    bool isSys = driveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase);

                    string volumeLabel = isSys ? "Local Disk" : "Volume";
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
                        {
                            volumeLabel = drive.VolumeLabel;
                        }
                    }
                    catch { }

                    string fileSystem = "NTFS";
                    try
                    {
                        if (!string.IsNullOrEmpty(drive.DriveFormat))
                        {
                            fileSystem = drive.DriveFormat;
                        }
                    }
                    catch { }

                    long totalSize = 0;
                    long freeSize = 0;
                    try
                    {
                        totalSize = drive.TotalSize;
                        freeSize = drive.TotalFreeSpace;
                    }
                    catch { }

                    var dev = new StorageDevice
                    {
                        Name = drive.Name.TrimEnd('\\'),
                        DriveLetter = driveLetter,
                        VolumeLabel = volumeLabel,
                        DevicePath = $@"\\.\{driveLetter}",
                        FileSystem = fileSystem,
                        DeviceType = StorageDeviceType.LogicalDrive,
                        TotalSizeBytes = totalSize,
                        FreeSizeBytes = freeSize,
                        SectorSize = 512,
                        IsSystemDrive = isSys
                    };

                    // Query volume serial number for anti-overwrite matching
                    try
                    {
                        if (NativeMethods.GetVolumeInformation(drive.Name, IntPtr.Zero, 0, out uint serial, out _, out _, IntPtr.Zero, 0))
                        {
                            dev.VolumeSerialNumber = serial;
                        }
                    }
                    catch { }

                    devices.Add(dev);
                }
                catch
                {
                    // Ignore inaccessible or unmounted drives safely
                }
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
