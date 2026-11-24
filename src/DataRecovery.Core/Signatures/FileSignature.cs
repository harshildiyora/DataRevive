using System;
using System.Buffers.Binary;

namespace DataRecovery.Core.Signatures
{
    public delegate long SpanLengthResolver(ReadOnlySpan<byte> buffer);

    public class FileSignature : IFileSignature
    {
        public string Name { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public FileCategory Category { get; set; }
        public byte[] Header { get; set; } = Array.Empty<byte>();
        public byte[]? Trailer { get; set; }
        public long MinSizeBytes { get; set; } = 32;
        public long MaxSizeBytes { get; set; } = 100 * 1024 * 1024; // Default 100 MB bound

        public SpanLengthResolver? CustomLengthResolver { get; set; }

        public virtual long TryDetermineLength(ReadOnlySpan<byte> buffer)
        {
            if (CustomLengthResolver != null)
            {
                return CustomLengthResolver(buffer);
            }

            // Fallback: search for trailer if present
            if (Trailer != null && Trailer.Length > 0)
            {
                int trailerIndex = buffer.IndexOf(Trailer);
                if (trailerIndex >= 0)
                {
                    long length = trailerIndex + Trailer.Length;
                    if (length >= MinSizeBytes && length <= MaxSizeBytes)
                    {
                        return length;
                    }
                }
            }

            return -1;
        }

        public override string ToString() => $"{Name} (*.{Extension})";
    }
}
