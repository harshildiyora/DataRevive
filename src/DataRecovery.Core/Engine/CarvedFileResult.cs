using System;
using DataRecovery.Core.Signatures;
using DataRecovery.Core.Storage;

namespace DataRecovery.Core.Engine
{
    public class CarvedFileResult
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        
        /// <summary>
        /// Display or recovered file name (e.g. "test.txt" or "recovered_0x0000022FAF.jpg")
        /// </summary>
        public string FileName { get; set; } = string.Empty;
        
        /// <summary>
        /// Original recovered file name from MFT/RecycleBin metadata
        /// </summary>
        public string OriginalFileName { get; set; } = string.Empty;

        /// <summary>
        /// Original path/folder if known (e.g. "C:\Users\harsh\Desktop")
        /// </summary>
        public string OriginalPath { get; set; } = string.Empty;

        public string Extension { get; set; } = string.Empty;
        public FileCategory Category { get; set; }
        public long SectorOffset { get; set; }
        public long FileSizeBytes { get; set; }
        public DateTime DiscoveredTime { get; set; } = DateTime.Now;
        public DateTime? DeletedDate { get; set; }
        public DateTime? CreatedDate { get; set; }

        public string Status { get; set; } = "Carved"; // "Deleted (MFT)", "Recycle Bin", "Carved"
        public bool IsMetadataRecovered { get; set; }
        public bool IsResident { get; set; }
        public byte[]? ResidentData { get; set; }

        public string Sha256Hash { get; set; } = string.Empty;
        public bool IsExported { get; set; }
        public string ExportedPath { get; set; } = string.Empty;
        public bool IsSelected { get; set; } = true;
        public bool IsPreviewValid { get; set; }

        public System.Collections.Generic.List<DataRun> DataRuns { get; set; } = new();
        public int BytesPerCluster { get; set; } = 4096;

        public string DisplaySize => StorageDevice.FormatBytes(FileSizeBytes);
        public string DisplayOffset => SectorOffset >= 0 ? $"0x{SectorOffset:X10}" : "Resident";
        public string DisplayDate => DeletedDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? DiscoveredTime.ToString("yyyy-MM-dd HH:mm:ss");
        public string EffectiveFileName => !string.IsNullOrEmpty(OriginalFileName) ? OriginalFileName : FileName;
        public string FileLocation => !string.IsNullOrEmpty(OriginalPath) ? (System.IO.Path.GetDirectoryName(OriginalPath) ?? OriginalPath) : "Discovered in Drive Scan";

        public string DisplayFileType
        {
            get
            {
                string ext = (Extension ?? "").ToLowerInvariant().TrimStart('.');
                return ext switch
                {
                    "png" => "PNG Image",
                    "jpg" or "jpeg" => "JPEG Image",
                    "bmp" => "Bitmap Image",
                    "gif" => "GIF Image",
                    "pdf" => "Adobe PDF Document",
                    "docx" or "doc" => "Microsoft Word Document",
                    "xlsx" or "xls" => "Microsoft Excel Worksheet",
                    "pptx" or "ppt" => "Microsoft PowerPoint Presentation",
                    "txt" => "Text Document",
                    "log" => "Text Log File",
                    "csv" => "CSV Spreadsheet File",
                    "json" => "JSON Document",
                    "zip" => "Compressed (zipped) Folder",
                    "rar" => "WinRAR Archive",
                    "7z" => "7-Zip Archive",
                    "mp3" => "MP3 Audio File",
                    "wav" => "WAV Audio File",
                    "mp4" => "MP4 Video File",
                    _ => string.IsNullOrEmpty(ext) ? "File" : $"{ext.ToUpperInvariant()} File"
                };
            }
        }

        public string DisplayIcon
        {
            get
            {
                return Category switch
                {
                    FileCategory.Images => "🖼️",
                    FileCategory.Documents => "📄",
                    FileCategory.Archives => "📦",
                    FileCategory.Audio => "🎵",
                    FileCategory.Video => "🎬",
                    _ => "📄"
                };
            }
        }

        public override string ToString() => $"{(string.IsNullOrEmpty(OriginalFileName) ? FileName : OriginalFileName)} ({DisplaySize}) [{Status}]";
    }
}
