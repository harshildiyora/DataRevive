using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace DataRecovery.Core.Signatures
{
    public static class SignatureCatalog
    {
        public static readonly List<IFileSignature> AllSignatures = new();

        static SignatureCatalog()
        {
            // 1. JPEG (SOI: FF D8 FF, EOI: FF D9)
            AllSignatures.Add(new FileSignature
            {
                Name = "JPEG Image",
                Extension = "jpg",
                Category = FileCategory.Images,
                Header = new byte[] { 0xFF, 0xD8, 0xFF },
                Trailer = new byte[] { 0xFF, 0xD9 },
                MinSizeBytes = 64,
                MaxSizeBytes = 50 * 1024 * 1024,
                CustomLengthResolver = buffer =>
                {
                    if (buffer.Length < 4) return -1;
                    byte marker = buffer[3];
                    // Validate 4th byte is a legitimate JPEG marker:
                    // APP0..APP15 (0xE0..0xEF), DQT (0xDB), SOF0..SOF3 (0xC0..0xC3), COM (0xFE)
                    bool isValidMarker = (marker >= 0xE0 && marker <= 0xEF) ||
                                         marker == 0xDB ||
                                         (marker >= 0xC0 && marker <= 0xC3) ||
                                         marker == 0xFE;
                    if (!isValidMarker) return -1;

                    byte[] eoi = new byte[] { 0xFF, 0xD9 };
                    // Find first EOI marker beyond minimum header/metadata threshold
                    int idx = buffer.Slice(4).IndexOf(eoi);
                    if (idx >= 0)
                    {
                        return 4 + idx + 2;
                    }
                    return -1;
                }
            });

            // 2. PNG
            AllSignatures.Add(new FileSignature
            {
                Name = "PNG Image",
                Extension = "png",
                Category = FileCategory.Images,
                Header = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                Trailer = new byte[] { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 }, // IEND + CRC
                MinSizeBytes = 32,
                MaxSizeBytes = 100 * 1024 * 1024,
                CustomLengthResolver = buffer =>
                {
                    // Validate first chunk is IHDR (bytes 12..15 must be 'I', 'H', 'D', 'R')
                    if (buffer.Length >= 16)
                    {
                        if (buffer[12] != (byte)'I' || buffer[13] != (byte)'H' ||
                            buffer[14] != (byte)'D' || buffer[15] != (byte)'R')
                        {
                            return -1;
                        }
                    }

                    // Search for IEND marker: 49 45 4E 44 followed by 4 bytes CRC
                    byte[] iend = new byte[] { 0x49, 0x45, 0x4E, 0x44 };
                    int idx = buffer.IndexOf(iend);
                    if (idx >= 0 && idx + 4 + 4 <= buffer.Length)
                    {
                        return idx + 4 + 4; // IEND chunk + 4 bytes CRC
                    }
                    return -1;
                }
            });

            // 3. GIF
            AllSignatures.Add(new FileSignature
            {
                Name = "GIF Image (89a)",
                Extension = "gif",
                Category = FileCategory.Images,
                Header = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 },
                Trailer = new byte[] { 0x00, 0x3B },
                MinSizeBytes = 32,
                MaxSizeBytes = 50 * 1024 * 1024
            });

            // 4. BMP
            AllSignatures.Add(new FileSignature
            {
                Name = "BMP Image",
                Extension = "bmp",
                Category = FileCategory.Images,
                Header = new byte[] { 0x42, 0x4D }, // "BM"
                MinSizeBytes = 54,
                MaxSizeBytes = 50 * 1024 * 1024,
                CustomLengthResolver = buffer =>
                {
                    if (buffer.Length >= 18)
                    {
                        // Verify reserved bytes are 0
                        ushort res1 = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(6, 2));
                        ushort res2 = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(8, 2));
                        if (res1 != 0 || res2 != 0) return -1;

                        // Offset to pixel array (must be >= 54 and <= 10000)
                        uint offBits = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(10, 4));
                        if (offBits < 54 || offBits > 10000) return -1;

                        // DIB header size (must be standard: 12, 40, 52, 56, 64, 108, 124)
                        uint dibSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(14, 4));
                        if (dibSize != 12 && dibSize != 40 && dibSize != 52 && dibSize != 56 && dibSize != 64 && dibSize != 108 && dibSize != 124)
                            return -1;

                        uint size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(2, 4));
                        if (size >= 54 && size <= 50 * 1024 * 1024)
                            return (long)size;
                    }
                    return -1;
                }
            });

            // 5. PDF Document
            AllSignatures.Add(new FileSignature
            {
                Name = "PDF Document",
                Extension = "pdf",
                Category = FileCategory.Documents,
                Header = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, // "%PDF-"
                Trailer = new byte[] { 0x25, 0x25, 0x45, 0x4F, 0x46 }, // "%%EOF"
                MinSizeBytes = 32,
                MaxSizeBytes = 500 * 1024 * 1024,
                CustomLengthResolver = buffer =>
                {
                    byte[] eof = new byte[] { 0x25, 0x25, 0x45, 0x4F, 0x46 };
                    int idx = buffer.IndexOf(eof);
                    if (idx >= 0)
                    {
                        long len = idx + 5;
                        while (len < buffer.Length && (buffer[(int)len] == 0x0D || buffer[(int)len] == 0x0A || buffer[(int)len] == 0x20))
                        {
                            len++;
                        }
                        return len;
                    }
                    return -1;
                }
            });

            // 6. ZIP / Office Document (.docx, .xlsx, .pptx, .zip)
            AllSignatures.Add(new FileSignature
            {
                Name = "ZIP / Office Document",
                Extension = "zip",
                Category = FileCategory.Documents,
                Header = new byte[] { 0x50, 0x4B, 0x03, 0x04 }, // PK\x03\x04
                MinSizeBytes = 64,
                MaxSizeBytes = 1024 * 1024 * 1024,
                CustomLengthResolver = buffer =>
                {
                    byte[] eocd = new byte[] { 0x50, 0x4B, 0x05, 0x06 };
                    int idx = buffer.LastIndexOf(eocd);
                    if (idx >= 0 && idx + 22 <= buffer.Length)
                    {
                        uint cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(idx + 16, 4));
                        uint cdSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(idx + 12, 4));
                        ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(idx + 20, 2));

                        // Validate that Central Directory aligns with EOCD position
                        if (cdOffset + cdSize == idx)
                        {
                            return idx + 22 + commentLength;
                        }
                    }
                    return -1;
                }
            });

            // 7. MP4 / MOV Video (starts with 4-byte box size + "ftyp")
            AllSignatures.Add(new FileSignature
            {
                Name = "MP4 / MOV Video",
                Extension = "mp4",
                Category = FileCategory.Video,
                Header = new byte[] { 0x00, 0x00, 0x00 },
                MinSizeBytes = 256,
                MaxSizeBytes = 10L * 1024 * 1024 * 1024,
                CustomLengthResolver = buffer =>
                {
                    if (buffer.Length >= 12 && buffer[4] == 0x66 && buffer[5] == 0x74 && buffer[6] == 0x79 && buffer[7] == 0x70)
                    {
                        uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(0, 4));
                        if (boxSize >= 8 && boxSize <= 1024)
                        {
                            return Math.Min(buffer.Length, 500 * 1024 * 1024);
                        }
                    }
                    return -1;
                }
            });

            // 8. RIFF (WAV / AVI / WEBP)
            AllSignatures.Add(new FileSignature
            {
                Name = "RIFF Media (WAV/AVI/WEBP)",
                Extension = "wav",
                Category = FileCategory.Audio,
                Header = new byte[] { 0x52, 0x49, 0x46, 0x46 }, // "RIFF"
                MinSizeBytes = 64,
                MaxSizeBytes = 4L * 1024 * 1024 * 1024,
                CustomLengthResolver = buffer =>
                {
                    if (buffer.Length >= 12)
                    {
                        uint size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4, 4));
                        long totalLen = size + 8;
                        if (totalLen >= 64 && totalLen <= 4L * 1024 * 1024 * 1024)
                            return totalLen;
                    }
                    return -1;
                }
            });

            // 9. MP3 Audio (ID3v2)
            AllSignatures.Add(new FileSignature
            {
                Name = "MP3 Audio (ID3)",
                Extension = "mp3",
                Category = FileCategory.Audio,
                Header = new byte[] { 0x49, 0x44, 0x33 }, // "ID3"
                MinSizeBytes = 128,
                MaxSizeBytes = 200 * 1024 * 1024,
                CustomLengthResolver = buffer =>
                {
                    if (buffer.Length >= 10)
                    {
                        byte majorVer = buffer[3];
                        if (majorVer > 4) return -1;
                        byte s0 = buffer[6], s1 = buffer[7], s2 = buffer[8], s3 = buffer[9];
                        if ((s0 & 0x80) != 0 || (s1 & 0x80) != 0 || (s2 & 0x80) != 0 || (s3 & 0x80) != 0)
                            return -1;
                        int tagSize = (s0 << 21) | (s1 << 14) | (s2 << 7) | s3;
                        long total = 10 + tagSize;
                        if (total >= 128 && total <= 200 * 1024 * 1024)
                            return total;
                    }
                    return -1;
                }
            });

            // 10. 7-Zip Archive
            AllSignatures.Add(new FileSignature
            {
                Name = "7-Zip Archive",
                Extension = "7z",
                Category = FileCategory.Archives,
                Header = new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },
                MinSizeBytes = 32,
                MaxSizeBytes = 5L * 1024 * 1024 * 1024
            });
        }
    }
}
