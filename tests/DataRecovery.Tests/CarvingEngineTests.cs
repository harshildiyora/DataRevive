using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DataRecovery.Core.Engine;
using DataRecovery.Core.Security;
using DataRecovery.Core.Signatures;
using DataRecovery.Core.Storage;
using Xunit;

namespace DataRecovery.Tests
{
    public class CarvingEngineTests
    {
        [Fact]
        public async Task CarvingEngine_Recovers_SimulatedDeletedFiles_WithMatchingHashes()
        {
            // 1. Arrange: Create a synthetic raw disk image in memory/disk
            string testDir = Path.Combine(Path.GetTempPath(), "DataRecoveryTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);
            string imagePath = Path.Combine(testDir, "test_disk.img");
            string exportDir = Path.Combine(testDir, "Recovered");

            try
            {
                // Create a 10 MB synthetic disk filled with random sectors
                int diskSize = 10 * 1024 * 1024;
                byte[] diskBytes = new byte[diskSize];
                new Random(42).NextBytes(diskBytes);

                // Inject a sample JPEG file at offset 65,536 (sector 128)
                int jpegOffset = 65536;
                byte[] sampleJpeg = new byte[] {
                    0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
                    0x01, 0x01, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43,
                    0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08, 0x07, 0x07, 0x07, 0x09,
                    0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
                    0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20,
                    0x24, 0x2E, 0x27, 0x20, 0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29,
                    0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27, 0x39, 0x3D, 0x38, 0x32,
                    0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xD9 // Ends with FF D9
                };
                Array.Copy(sampleJpeg, 0, diskBytes, jpegOffset, sampleJpeg.Length);
                string expectedJpegHash = ComputeSha256(sampleJpeg);

                // Inject a sample PNG file at offset 1,048,576 (sector 2048)
                int pngOffset = 1048576;
                byte[] samplePng = new byte[] {
                    0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG magic
                    0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR
                    0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                    0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89,
                    0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, // IEND
                    0xAE, 0x42, 0x60, 0x82                          // CRC
                };
                Array.Copy(samplePng, 0, diskBytes, pngOffset, samplePng.Length);
                string expectedPngHash = ComputeSha256(samplePng);

                // Inject a sample PDF file at offset 2,097,152 (sector 4096)
                int pdfOffset = 2097152;
                byte[] samplePdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\n%%EOF\n");
                Array.Copy(samplePdf, 0, diskBytes, pdfOffset, samplePdf.Length);

                await File.WriteAllBytesAsync(imagePath, diskBytes);

                // 2. Act: Run Carving Engine on the synthetic disk image
                var device = DeviceEnumerator.CreateFromImageFile(imagePath);
                using var reader = new UnbufferedSectorReader(device);
                var engine = new CarvingEngine();
                var channel = Channel.CreateUnbounded<CarvedFileResult>();

                var discovered = new System.Collections.Generic.List<CarvedFileResult>();
                var consumerTask = Task.Run(async () =>
                {
                    while (await channel.Reader.WaitToReadAsync())
                    {
                        while (channel.Reader.TryRead(out var item))
                        {
                            discovered.Add(item);
                        }
                    }
                });

                await engine.StartCarvingAsync(reader, channel.Writer, null, CancellationToken.None);
                await consumerTask;

                // 3. Assert: Files detected
                Assert.True(discovered.Count >= 3, $"Expected at least 3 files, found {discovered.Count}");

                var jpegResult = discovered.Find(f => f.Extension == "jpg" && f.SectorOffset == jpegOffset);
                Assert.NotNull(jpegResult);
                Assert.Equal(sampleJpeg.Length, jpegResult.FileSizeBytes);

                var pngResult = discovered.Find(f => f.Extension == "png" && f.SectorOffset == pngOffset);
                Assert.NotNull(pngResult);
                Assert.Equal(samplePng.Length, pngResult.FileSizeBytes);

                var pdfResult = discovered.Find(f => f.Extension == "pdf" && f.SectorOffset == pdfOffset);
                Assert.NotNull(pdfResult);

                // 4. Export and verify SHA-256 integrity
                var exporter = new FileExporter();
                string exportedJpeg = await exporter.ExportFileAsync(reader, jpegResult, exportDir, CancellationToken.None);
                Assert.True(File.Exists(exportedJpeg));
                Assert.Equal(expectedJpegHash, jpegResult.Sha256Hash);

                string exportedPng = await exporter.ExportFileAsync(reader, pngResult, exportDir, CancellationToken.None);
                Assert.True(File.Exists(exportedPng));
                Assert.Equal(expectedPngHash, pngResult.Sha256Hash);

                // 5. Verify manifest generation
                await exporter.WriteManifestAsync(exportDir, new[] { jpegResult, pngResult, pdfResult });
                string manifestPath = Path.Combine(exportDir, "recovery_manifest.json");
                Assert.True(File.Exists(manifestPath));
                string manifestContent = await File.ReadAllTextAsync(manifestPath);
                Assert.Contains(expectedJpegHash, manifestContent);
            }
            finally
            {
                if (Directory.Exists(testDir))
                {
                    try { Directory.Delete(testDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void SecurityValidator_Blocks_SelfOverwrite_OnSameSource()
        {
            var device = new StorageDevice
            {
                Name = "D: (DATA)",
                DevicePath = @"\\.\D:",
                VolumeSerialNumber = 0x12345678,
                DeviceType = StorageDeviceType.LogicalDrive
            };

            // Attempting to export to the same drive D:\Recovered
            bool safe = SecurityValidator.ValidateDestinationIsSafe(device, @"D:\Recovered", out string error);
            Assert.False(safe);
            Assert.Contains("SECURITY VIOLATION", error);
        }

        [Fact]
        public void SecurityValidator_Sanitizes_PathTraversalTokens()
        {
            string maliciousName = @"..\..\..\Windows\System32\cmd.exe";
            string clean = SecurityValidator.SanitizeFileName(maliciousName);
            Assert.DoesNotContain("..", clean);
            Assert.DoesNotContain(@"\", clean);
            Assert.DoesNotContain("/", clean);
        }

        [Fact]
        public async Task CarvingEngine_Carves_ValidZipArchive_ThatOpensWithStandardZipExtractor()
        {
            // 1. Arrange: Create a genuine ZIP archive in memory
            byte[] zipData;
            using (var mem = new MemoryStream())
            {
                using (var archive = new ZipArchive(mem, ZipArchiveMode.Create, true))
                {
                    var entry = archive.CreateEntry("test_document.txt", CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("DataRecovery Pro Forensic Zip Extraction Test Successful!");
                }
                zipData = mem.ToArray();
            }

            // Create synthetic 4 MB disk with the ZIP archive injected at sector 512
            string testDir = Path.Combine(Path.GetTempPath(), "ZipTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);
            string imagePath = Path.Combine(testDir, "zip_disk.img");
            string exportDir = Path.Combine(testDir, "ExportedZip");

            try
            {
                byte[] diskBytes = new byte[4 * 1024 * 1024];
                int zipOffset = 262144; // 256 KB offset
                Array.Copy(zipData, 0, diskBytes, zipOffset, zipData.Length);
                await File.WriteAllBytesAsync(imagePath, diskBytes);

                // 2. Act: Run Carving Engine
                var device = DeviceEnumerator.CreateFromImageFile(imagePath);
                using var reader = new UnbufferedSectorReader(device);
                var engine = new CarvingEngine();
                var channel = Channel.CreateUnbounded<CarvedFileResult>();

                var discovered = new System.Collections.Generic.List<CarvedFileResult>();
                var consumerTask = Task.Run(async () =>
                {
                    while (await channel.Reader.WaitToReadAsync())
                    {
                        while (channel.Reader.TryRead(out var item))
                        {
                            discovered.Add(item);
                        }
                    }
                });

                await engine.StartCarvingAsync(reader, channel.Writer, null, CancellationToken.None);
                await consumerTask;

                // 3. Assert: ZIP archive discovered with matching size
                var zipResult = discovered.Find(f => f.Extension == "zip" && f.SectorOffset == zipOffset);
                Assert.NotNull(zipResult);
                Assert.Equal(zipData.Length, zipResult.FileSizeBytes);

                // 4. Export and verify standard Windows ZipArchive opens it without error
                var exporter = new FileExporter();
                string exportedZip = await exporter.ExportFileAsync(reader, zipResult, exportDir, CancellationToken.None);
                Assert.True(File.Exists(exportedZip));

                // Verify standard ZipArchive opens and reads the inner entry
                using var openedArchive = ZipFile.OpenRead(exportedZip);
                var entry = openedArchive.GetEntry("test_document.txt");
                Assert.NotNull(entry);

                using var readerStream = new StreamReader(entry.Open());
                string content = await readerStream.ReadToEndAsync();
                Assert.Contains("DataRecovery Pro Forensic Zip Extraction Test Successful!", content);
            }
            finally
            {
                if (Directory.Exists(testDir))
                {
                    try { Directory.Delete(testDir, true); } catch { }
                }
            }
        }

        private static string ComputeSha256(byte[] data)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(data);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
