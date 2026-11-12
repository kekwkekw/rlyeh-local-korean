using System.Runtime.InteropServices;
using System.Text;

namespace Rlyeh.Localizer.Core;

// A dedicated worker owns this process-wide C API. No Unity object crosses this boundary.
public sealed class HdTransEngine : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init(byte[] paths);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TranslateCall(byte[] text, UIntPtr n, out IntPtr output, out UIntPtr length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DiagnosticsCall(out IntPtr output, out UIntPtr length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeCall(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EndCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr VersionCall();
    private readonly IntPtr library;
    private readonly TranslateCall translate;
    private readonly DiagnosticsCall diagnostics;
    private readonly FreeCall free;
    private readonly EndCall terminate;
    private bool disposed;
    public string Version { get; }

    public HdTransEngine(string libraryPath, string snapshotPath)
    {
        library = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        T Get<T>(string symbol) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, symbol));
        translate = Get<TranslateCall>("hdTrans_Translate");
        diagnostics = Get<DiagnosticsCall>("hdTrans_UserDictError");
        free = Get<FreeCall>("hdTrans_FreeMem");
        terminate = Get<EndCall>("hdTrans_Terminate");
        Version = Marshal.PtrToStringUTF8(Get<VersionCall>("hdTrans_Version")()) ?? "unknown";
        if (Get<Init>("hdTrans_Initialize")(Encoding.UTF8.GetBytes(Path.GetFullPath(snapshotPath) + "\0\0")) != 0)
            throw new InvalidOperationException("hdTrans initialization failed: " + Diagnostics());
        string errors = Diagnostics();
        if (!string.IsNullOrWhiteSpace(errors)) throw new InvalidOperationException("hdTrans dictionary diagnostics: " + errors);
    }

    public string Translate(string text)
    {
        if (disposed) throw new ObjectDisposedException(nameof(HdTransEngine));
        byte[] data = Encoding.UTF8.GetBytes(text);
        IntPtr output = IntPtr.Zero;
        try
        {
            int status = translate(data, (UIntPtr)data.Length, out output, out var size);
            if (status != 0) throw new InvalidOperationException("hdTrans translation failed");
            return Marshal.PtrToStringUTF8(output, checked((int)size.ToUInt64())) ?? "";
        }
        finally { if (output != IntPtr.Zero) free(output); }
    }

    private string Diagnostics()
    {
        IntPtr output = IntPtr.Zero;
        try { diagnostics(out output, out var size); return output == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(output, checked((int)size.ToUInt64())) ?? ""; }
        finally { if (output != IntPtr.Zero) free(output); }
    }
    public void Dispose() { if (!disposed) { terminate(); disposed = true; } }
    // Keep the library loaded for the process lifetime: its background file watcher can still be exiting.
}
