using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DataRecovery.Core.Storage
{
    /// <summary>
    /// Enforces strictly READ-ONLY unbuffered sector streaming.
    /// Transparently handles Windows Win32 sector alignment (512B/4096B) for arbitrary byte offsets.
    /// </summary>
    public class UnbufferedSectorReader : ISectorReader
    {
        private readonly StorageDevice _device;
        private readonly SafeFileHandle? _handle;
        private readonly FileStream? _fileStream;
        private readonly bool _isImageFile;
        private long _currentPosition;
        private readonly int _sectorSize;
        private readonly long _totalBytes;
        private readonly object _syncLock = new();

        public long TotalBytes => _totalBytes;
        public int SectorSize => _sectorSize;
        public long CurrentPosition
        {
            get { lock (_syncLock) return _currentPosition; }
        }

        public UnbufferedSectorReader(StorageDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _sectorSize = device.SectorSize > 0 ? device.SectorSize : 512;
            _totalBytes = device.TotalSizeBytes;
            _isImageFile = device.DeviceType == StorageDeviceType.DiskImageFile;

            if (_isImageFile)
            {
                // Open disk image file with strict Read-Only mode
                _fileStream = new FileStream(
                    device.DevicePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                _totalBytes = _fileStream.Length;
            }
            else
            {
                // Win32 raw volume/disk handle with STRICT SOFTWARE WRITE-BLOCKING (GENERIC_READ ONLY)
                _handle = NativeMethods.CreateFile(
                    device.DevicePath,
                    NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_ALL,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
                    IntPtr.Zero);

                if (_handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        $"Failed to open device {device.DevicePath} for read-only access. Win32 Error: {error}. " +
                        (error == 5 ? "Administrator privileges are required to access raw physical/logical drives." : string.Empty));
                }

                // Query disk length if not already set
                if (_totalBytes <= 0)
                {
                    IntPtr lengthPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.GET_LENGTH_INFORMATION>());
                    try
                    {
                        if (NativeMethods.DeviceIoControl(
                            _handle,
                            NativeMethods.IOCTL_DISK_GET_LENGTH_INFO,
                            IntPtr.Zero, 0,
                            lengthPtr, (uint)Marshal.SizeOf<NativeMethods.GET_LENGTH_INFORMATION>(),
                            out _, IntPtr.Zero))
                        {
                            var info = Marshal.PtrToStructure<NativeMethods.GET_LENGTH_INFORMATION>(lengthPtr);
                            _totalBytes = info.Length;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(lengthPtr);
                    }
                }

                _fileStream = new FileStream(_handle, FileAccess.Read, 64 * 1024);
            }
        }

        public void Seek(long offset)
        {
            lock (_syncLock)
            {
                if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
                _currentPosition = offset;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_syncLock)
            {
                if (_fileStream == null) throw new ObjectDisposedException(nameof(UnbufferedSectorReader));
                if (count <= 0) return 0;

                if (_isImageFile)
                {
                    // Image files don't require sector alignment
                    try
                    {
                        _fileStream.Seek(_currentPosition, SeekOrigin.Begin);
                        int bytesRead = _fileStream.Read(buffer, offset, count);
                        _currentPosition += bytesRead;
                        return bytesRead;
                    }
                    catch
                    {
                        return 0;
                    }
                }

                // Raw disk handles on Windows REQUIRE sector-aligned seeks and buffer counts
                try
                {
                    long alignedStart = (_currentPosition / _sectorSize) * _sectorSize;
                    int diff = (int)(_currentPosition - alignedStart);

                    if (diff == 0 && (count % _sectorSize == 0))
                    {
                        // Already perfectly sector-aligned
                        _fileStream.Seek(alignedStart, SeekOrigin.Begin);
                        int bytesRead = _fileStream.Read(buffer, offset, count);
                        _currentPosition += bytesRead;
                        return bytesRead;
                    }

                    // Unaligned offset or count: read aligned sector span and extract requested range
                    int needed = diff + count;
                    int alignedCount = ((needed + _sectorSize - 1) / _sectorSize) * _sectorSize;

                    byte[] tempBuffer = ArrayPool<byte>.Shared.Rent(alignedCount);
                    try
                    {
                        _fileStream.Seek(alignedStart, SeekOrigin.Begin);
                        int rawRead = _fileStream.Read(tempBuffer, 0, alignedCount);

                        if (rawRead <= diff)
                        {
                            return 0; // End of disk reached
                        }

                        int available = rawRead - diff;
                        int actualToCopy = Math.Min(available, count);

                        Array.Copy(tempBuffer, diff, buffer, offset, actualToCopy);
                        _currentPosition += actualToCopy;
                        return actualToCopy;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(tempBuffer);
                    }
                }
                catch (IOException)
                {
                    // Bad sector encountered - gracefully advance current position by 1 sector size to prevent crashing
                    _currentPosition += _sectorSize;
                    return 0;
                }
            }
        }

        public int ReadSector(byte[] sectorBuffer)
        {
            return Read(sectorBuffer, 0, _sectorSize);
        }

        public void Dispose()
        {
            _fileStream?.Dispose();
            _handle?.Dispose();
        }
    }
}
