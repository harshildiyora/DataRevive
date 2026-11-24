using System;
using DataRecovery.Core.Storage;

namespace DataRecovery.Core.Engine
{
    public class RecoveryProgress
    {
        public long BytesScanned { get; set; }
        public long TotalBytes { get; set; }
        public double Percentage => TotalBytes > 0 ? Math.Min(100.0, Math.Max(0.0, (double)BytesScanned / TotalBytes * 100.0)) : 0.0;
        public int FilesDiscovered { get; set; }
        public double SpeedMBPerSec { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public TimeSpan EstimatedTimeRemaining { get; set; }
        public string StatusMessage { get; set; } = "Ready";

        public string DisplayProgress => $"{Percentage:0.0}% ({StorageDevice.FormatBytes(Math.Min(BytesScanned, TotalBytes))} / {StorageDevice.FormatBytes(TotalBytes)})";
        public string DisplaySpeed => $"{SpeedMBPerSec:0.0} MB/s";
    }
}
