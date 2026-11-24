using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DataRecovery.Core.Storage
{
    public enum StorageDeviceType
    {
        LogicalDrive,
        PhysicalDisk,
        DiskImageFile
    }

    public class StorageDevice : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Name { get; set; } = string.Empty;
        public string DriveLetter { get; set; } = string.Empty;
        public string VolumeLabel { get; set; } = string.Empty;
        public string DevicePath { get; set; } = string.Empty;
        public string FileSystem { get; set; } = string.Empty;
        public StorageDeviceType DeviceType { get; set; }
        public long TotalSizeBytes { get; set; }
        public long FreeSizeBytes { get; set; }
        public int SectorSize { get; set; } = 512;
        public uint VolumeSerialNumber { get; set; }
        public bool IsSystemDrive { get; set; }

        public long UsedSizeBytes => Math.Max(0, TotalSizeBytes - FreeSizeBytes);
        public double UsedPercentage
        {
            get => TotalSizeBytes > 0 ? (double)UsedSizeBytes / TotalSizeBytes * 100.0 : 0;
            set { }
        }
        public string UsedVsTotalDisplay => $"{FormatBytes(UsedSizeBytes)} / {FormatBytes(TotalSizeBytes)}";
        public string CapacitySummary => $"{FileSystem} • {FormatBytes(TotalSizeBytes)}";

        public string DisplayTotalSize => FormatBytes(TotalSizeBytes);
        public string DisplayFreeSize => FormatBytes(FreeSizeBytes);
        public string DisplayUsedPercentage => $"{UsedPercentage:F0}%";

        public string CardTitle => !string.IsNullOrEmpty(VolumeLabel) ? $"{VolumeLabel} ({DriveLetter})" : (!string.IsNullOrEmpty(DriveLetter) ? $"Local Disk ({DriveLetter})" : Name);

        public string DisplayName =>
            DeviceType == StorageDeviceType.DiskImageFile
                ? $"[Image] {Name} ({FormatBytes(TotalSizeBytes)})"
                : $"[{DeviceType}] {Name} - {FileSystem} ({FormatBytes(TotalSizeBytes)})";

        public static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        public override string ToString() => DisplayName;
    }
}
