using System.Runtime.InteropServices;

namespace DvbHeadend.Linux;

/// <summary>Thin libc bindings. Only what the Linux DVB API needs.</summary>
internal static unsafe partial class Native
{
    public const int O_RDONLY = 0;
    public const int O_RDWR = 2;
    public const int O_NONBLOCK = 0x800;

    public const short POLLIN = 0x0001;

    public const int EINTR = 4;
    public const int EAGAIN = 11;
    public const int EOVERFLOW = 75;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    public static partial nint Read(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static partial int Ioctl(int fd, nuint request, void* arg);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static partial int IoctlValue(int fd, nuint request, nuint arg);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll(PollFd* fds, nuint count, int timeoutMs);

    public static int LastError => Marshal.GetLastPInvokeError();

    public static IOException Error(string what)
    {
        int errno = Marshal.GetLastPInvokeError();
        return new IOException($"{what}: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno})");
    }
}
