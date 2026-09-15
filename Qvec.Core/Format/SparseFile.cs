using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Qvec.Core.Format
{
    /// <summary>
    /// Creates the backing file for a new database without physically allocating every byte of it.
    /// <para>
    /// A Qvec file is laid out for <c>MaxCount</c> rows up front, so a database sized for a million
    /// 1536-dimensional vectors describes roughly 7 GiB before a single entry is inserted. On a
    /// file system with sparse-file support only the regions that are actually written consume
    /// space, which is what makes "create a database for a million vectors" cheap.
    /// </para>
    /// <para>
    /// Unix file systems (ext4, xfs, btrfs, apfs) create a hole automatically when a file is
    /// extended with <see cref="FileStream.SetLength"/>, so nothing extra is required there. NTFS
    /// allocates the clusters eagerly unless the file is explicitly marked sparse first, so on
    /// Windows the marker is applied before the length is set.
    /// </para>
    /// </summary>
    internal static partial class SparseFile
    {
        private const uint FsctlSetSparse = 0x000900C4;

        /// <summary>
        /// Creates <paramref name="path"/> with a logical length of <paramref name="length"/>
        /// bytes, sparse where the platform supports it. Does nothing if the file already exists.
        /// </summary>
        /// <remarks>
        /// Failure to mark the file sparse is never fatal: the file system may not support it
        /// (FAT32, ReFS in some configurations, network shares). In that case the file is simply
        /// allocated the ordinary way and the database still works, it just costs more disk.
        /// </remarks>
        internal static void CreateSized(string path, long length)
        {
            if (File.Exists(path)) return;

            using var stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);

            if (OperatingSystem.IsWindows())
            {
                TryMarkSparse(stream);
            }

            stream.SetLength(length);
        }

        [SupportedOSPlatform("windows")]
        private static void TryMarkSparse(FileStream stream)
        {
            try
            {
                _ = DeviceIoControl(
                    stream.SafeFileHandle,
                    FsctlSetSparse,
                    IntPtr.Zero, 0,
                    IntPtr.Zero, 0,
                    out _,
                    IntPtr.Zero);
            }
            catch (EntryPointNotFoundException)
            {
                // Not reachable on a supported Windows build, but a missing entry point must
                // never be the reason a database cannot be created.
            }
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SupportedOSPlatform("windows")]
        private static partial bool DeviceIoControl(
            Microsoft.Win32.SafeHandles.SafeFileHandle device,
            uint controlCode,
            IntPtr inBuffer,
            uint inBufferSize,
            IntPtr outBuffer,
            uint outBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);
    }
}
