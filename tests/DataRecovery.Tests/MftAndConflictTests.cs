using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataRecovery.Core.Engine;
using DataRecovery.Core.Security;
using DataRecovery.Core.Signatures;
using DataRecovery.Core.Storage;
using Xunit;

namespace DataRecovery.Tests
{
    public class MftAndConflictTests
    {
        [Fact]
        public void NtfsMftParser_ParsesDeletedRecord_WithOriginalFileName_AndResidentData()
        {
            // Create a synthetic 1024-byte MFT record for deleted "test.txt"
            byte[] record = new byte[1024];

            // 1. Signature "FILE"
            record[0] = (byte)'F';
            record[1] = (byte)'I';
            record[2] = (byte)'L';
            record[3] = (byte)'E';

            // Update seq offset = 0x30, count = 1
            BitConverter.GetBytes((ushort)0x30).CopyTo(record, 0x04);
            BitConverter.GetBytes((ushort)1).CopyTo(record, 0x06);

            // First attribute offset = 0x38
            BitConverter.GetBytes((ushort)0x38).CopyTo(record, 0x14);

            // Flags = 0x0000 (Deleted file!)
            BitConverter.GetBytes((ushort)0x0000).CopyTo(record, 0x16);

            // Real size = 1024, Allocated = 1024
            BitConverter.GetBytes((uint)1024).CopyTo(record, 0x18);
            BitConverter.GetBytes((uint)1024).CopyTo(record, 0x1C);

            // Attribute 1: $FILE_NAME (0x30) at offset 0x38
            int fnAttrOffset = 0x38;
            BitConverter.GetBytes((uint)0x30).CopyTo(record, fnAttrOffset); // type
            uint fnLength = 0x80; // 128 bytes (sufficient room for header, timestamps, and UTF-16 name)
            BitConverter.GetBytes(fnLength).CopyTo(record, fnAttrOffset + 4);
            record[fnAttrOffset + 8] = 0; // Resident
            BitConverter.GetBytes((ushort)0x18).CopyTo(record, fnAttrOffset + 0x14); // Content offset

            int fnContent = fnAttrOffset + 0x18;
            string testName = "test.txt";
            byte nameLen = (byte)testName.Length;
            record[fnContent + 0x40] = nameLen;
            record[fnContent + 0x41] = 1; // Win32 namespace
            Encoding.Unicode.GetBytes(testName).CopyTo(record, fnContent + 0x42);

            // Attribute 2: $DATA (0x80) at offset 0x38 + fnLength
            int dataAttrOffset = fnAttrOffset + (int)fnLength;
            BitConverter.GetBytes((uint)0x80).CopyTo(record, dataAttrOffset); // type
            byte[] filePayload = Encoding.UTF8.GetBytes("DataRecovery Pro original test content!");
            uint dataAttrLength = (uint)(0x18 + filePayload.Length);
            BitConverter.GetBytes(dataAttrLength).CopyTo(record, dataAttrOffset + 4);
            record[dataAttrOffset + 8] = 0; // Resident
            BitConverter.GetBytes((uint)filePayload.Length).CopyTo(record, dataAttrOffset + 0x10); // ValueLength
            BitConverter.GetBytes((ushort)0x18).CopyTo(record, dataAttrOffset + 0x14); // ValueOffset
            filePayload.CopyTo(record, dataAttrOffset + 0x18);

            // End marker
            BitConverter.GetBytes((uint)0xFFFFFFFF).CopyTo(record, dataAttrOffset + (int)dataAttrLength);

            // 2. Act
            bool success = NtfsMftParser.TryParseRecord(record, out var mftRec);

            // 3. Assert
            Assert.True(success);
            Assert.NotNull(mftRec);
            Assert.Equal("test.txt", mftRec.FileName);
            Assert.False(mftRec.IsInUse);
            Assert.True(mftRec.IsResidentData);
            Assert.NotNull(mftRec.ResidentData);
            Assert.Equal(filePayload.Length, mftRec.ResidentData.Length);
            Assert.Equal("DataRecovery Pro original test content!", Encoding.UTF8.GetString(mftRec.ResidentData));

            var result = NtfsMftParser.ConvertToCarvedResult(mftRec);
            Assert.NotNull(result);
            Assert.Equal("test.txt", result.FileName);
            Assert.Equal("test.txt", result.OriginalFileName);
            Assert.Equal("Deleted (MFT)", result.Status);
            Assert.True(result.IsMetadataRecovered);
        }

        [Fact]
        public void ConflictHelper_Generates_UniqueRenamePath()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ConflictTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string file1 = Path.Combine(tempDir, "test.txt");
                File.WriteAllText(file1, "First version");

                string renamed1 = ConflictHelper.GetUniqueRenamePath(tempDir, "test.txt");
                Assert.Equal(Path.Combine(tempDir, "test (1).txt"), renamed1);

                File.WriteAllText(renamed1, "Second version");
                string renamed2 = ConflictHelper.GetUniqueRenamePath(tempDir, "test.txt");
                Assert.Equal(Path.Combine(tempDir, "test (2).txt"), renamed2);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task FileExporter_Handles_ConflictResolution_WithKeepBoth_AndOverwrite()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ExportConflictTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string docDir = Path.Combine(tempDir, "Documents");
            Directory.CreateDirectory(docDir);

            // Create existing file
            string existingPath = Path.Combine(docDir, "test.txt");
            await File.WriteAllTextAsync(existingPath, "Original existing text");

            var exporter = new FileExporter();
            var incomingResult = new CarvedFileResult
            {
                FileName = "test.txt",
                OriginalFileName = "test.txt",
                Extension = "txt",
                Category = FileCategory.Documents,
                IsResident = true,
                ResidentData = Encoding.UTF8.GetBytes("Recovered new text"),
                FileSizeBytes = 18,
                Status = "Deleted (MFT)"
            };

            // Test 1: Keep Both (Rename)
            string? exportedPath = await exporter.ExportFileAsync(
                new MockSectorReader(),
                incomingResult,
                tempDir,
                CancellationToken.None,
                req => Task.FromResult((ConflictChoice.KeepBothRename, false)));

            Assert.NotNull(exportedPath);
            Assert.True(File.Exists(exportedPath));
            Assert.Contains("test (1).txt", exportedPath);
            Assert.Equal("Original existing text", await File.ReadAllTextAsync(existingPath));
            Assert.Equal("Recovered new text", await File.ReadAllTextAsync(exportedPath));

            // Test 2: Overwrite
            string? overwrittenPath = await exporter.ExportFileAsync(
                new MockSectorReader(),
                incomingResult,
                tempDir,
                CancellationToken.None,
                req => Task.FromResult((ConflictChoice.Overwrite, false)));

            Assert.NotNull(overwrittenPath);
            Assert.Equal(existingPath, overwrittenPath);
            Assert.Equal("Recovered new text", await File.ReadAllTextAsync(existingPath));

            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }

        private class MockSectorReader : ISectorReader
        {
            public StorageDevice Device => new StorageDevice();
            public long TotalBytes => 1024 * 1024;
            public int SectorSize => 512;
            public long CurrentPosition => 0;
            public void Seek(long byteOffset) { }
            public int Read(byte[] buffer, int offset, int count) => 0;
            public int ReadSector(byte[] sectorBuffer) => 0;
            public void Dispose() { }
        }
    }
}
