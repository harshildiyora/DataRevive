namespace DataRecovery.Core.Signatures
{
    public enum FileCategory
    {
        Images,
        Documents,
        Video,
        Audio,
        Archives,
        Other
    }

    public interface IFileSignature
    {
        string Name { get; }
        string Extension { get; }
        FileCategory Category { get; }
        byte[] Header { get; }
        byte[]? Trailer { get; }
        long MinSizeBytes { get; }
        long MaxSizeBytes { get; }

        /// <summary>
        /// Attempts to calculate or validate the exact carved file length from the buffer.
        /// Returns -1 if the file extent cannot yet be determined from the available window.
        /// </summary>
        long TryDetermineLength(ReadOnlySpan<byte> buffer);
    }
}
