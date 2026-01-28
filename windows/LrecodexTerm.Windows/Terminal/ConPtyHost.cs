using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LrecodexTerm.Terminal;

public sealed class ConPtyHost : IDisposable
{
    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _hInRead = IntPtr.Zero;
    private IntPtr _hInWrite = IntPtr.Zero;
    private IntPtr _hOutRead = IntPtr.Zero;
    private IntPtr _hOutWrite = IntPtr.Zero;
    private IntPtr _hProcess = IntPtr.Zero;
    private CancellationTokenSource? _cts;

    public event Action<string>? Output;

    public void Start(string commandLine, int cols = 120, int rows = 30)
    {
        Stop();

        CreatePipe(out _hInRead, out _hInWrite);
        CreatePipe(out _hOutRead, out _hOutWrite);

        var size = new COORD { X = (short)cols, Y = (short)rows };
        var hr = CreatePseudoConsole(size, _hInRead, _hOutWrite, 0, out _hPC);
        if (hr != 0)
        {
            throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X}");
        }

        var siEx = new STARTUPINFOEX();
        siEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        var lpSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        siEx.lpAttributeList = Marshal.AllocHGlobal(lpSize);
        InitializeProcThreadAttributeList(siEx.lpAttributeList, 1, 0, ref lpSize);
        UpdateProcThreadAttribute(siEx.lpAttributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);

        var pi = new PROCESS_INFORMATION();
        var cmd = new StringBuilder(commandLine);

        if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null, ref siEx, out pi))
        {
            throw new InvalidOperationException("CreateProcessW failed.");
        }

        _hProcess = pi.hProcess;

        DeleteProcThreadAttributeList(siEx.lpAttributeList);
        Marshal.FreeHGlobal(siEx.lpAttributeList);

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoop(_cts.Token));
    }

    public void Send(string text)
    {
        if (_hInWrite == IntPtr.Zero) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        WriteFile(_hInWrite, bytes, (uint)bytes.Length, out _, IntPtr.Zero);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;

        if (_hProcess != IntPtr.Zero)
        {
            TerminateProcess(_hProcess, 0);
            CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }

        if (_hPC != IntPtr.Zero)
        {
            ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }

        CloseHandleSafe(ref _hInRead);
        CloseHandleSafe(ref _hInWrite);
        CloseHandleSafe(ref _hOutRead);
        CloseHandleSafe(ref _hOutWrite);
    }

    private void ReadLoop(CancellationToken token)
    {
        var buffer = new byte[4096];
        while (!token.IsCancellationRequested)
        {
            if (!ReadFile(_hOutRead, buffer, (uint)buffer.Length, out var read, IntPtr.Zero) || read == 0)
            {
                Thread.Sleep(10);
                continue;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, (int)read);
            Output?.Invoke(text);
        }
    }

    public void Dispose() => Stop();

    private static void CreatePipe(out IntPtr read, out IntPtr write)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
        if (!CreatePipe(out read, out write, ref sa, 0))
        {
            throw new InvalidOperationException("CreatePipe failed.");
        }
    }

    private static void CloseHandleSafe(ref IntPtr h)
    {
        if (h != IntPtr.Zero)
        {
            CloseHandle(h);
            h = IntPtr.Zero;
        }
    }

    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, int dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, int dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
