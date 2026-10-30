using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace L22.Editor.Views;

// GlInterface in Avalonia 12.1 doesn't expose Uniform3f or ReadPixels, so we
// resolve them manually via GetProcAddress at init.
public sealed class GlExtras
{
    public const int GL_UNSIGNED_INT = 0x1405;
    public const int GL_POINTS = 0x0000;
    public const int GL_LINES = 0x0001;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Uniform3fDel(int loc, float x, float y, float z);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Uniform4fDel(int loc, float x, float y, float z, float w);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ReadPixelsDel(int x, int y, int w, int h, int format, int type, IntPtr data);

    private Uniform3fDel? _uniform3f;
    private Uniform4fDel? _uniform4f;
    private ReadPixelsDel? _readPixels;

    public void Resolve(GlInterface gl)
    {
        var u3 = gl.GetProcAddress("glUniform3f");
        if (u3 != IntPtr.Zero) _uniform3f = Marshal.GetDelegateForFunctionPointer<Uniform3fDel>(u3);
        var u4 = gl.GetProcAddress("glUniform4f");
        if (u4 != IntPtr.Zero) _uniform4f = Marshal.GetDelegateForFunctionPointer<Uniform4fDel>(u4);
        var rp = gl.GetProcAddress("glReadPixels");
        if (rp != IntPtr.Zero) _readPixels = Marshal.GetDelegateForFunctionPointer<ReadPixelsDel>(rp);
    }

    public void Uniform3f(int loc, float x, float y, float z)
    {
        if (_uniform3f is null) return;
        _uniform3f(loc, x, y, z);
    }

    public void Uniform4f(int loc, float x, float y, float z, float w)
    {
        if (_uniform4f is null) return;
        _uniform4f(loc, x, y, z, w);
    }

    public unsafe void ReadPixels(int x, int y, int w, int h, int format, int type, byte* data)
    {
        if (_readPixels is null) return;
        _readPixels(x, y, w, h, format, type, (IntPtr)data);
    }
}
