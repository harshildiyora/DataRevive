using System;

namespace DataRecovery.Core.Storage
{
    public interface ISectorReader : IDisposable
    {
        long TotalBytes { get; }
        int SectorSize { get; }
        long CurrentPosition { get; }
        void Seek(long offset);
        int Read(byte[] buffer, int offset, int count);
        int ReadSector(byte[] sectorBuffer);
    }
}
