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
    public const int GL_DYNAMIC_DRAW = 0x88E8;
    public const int GL_TEXTURE_2D = 0x0DE1;
    public const int GL_TEXTURE_MIN_FILTER = 0x2801;
    public const int GL_TEXTURE_MAG_FILTER = 0x2800;
    public const int GL_TEXTURE_WRAP_S = 0x2802;
    public const int GL_TEXTURE_WRAP_T = 0x2803;
    public const int GL_NEAREST = 0x2600;
    public const int GL_LINEAR = 0x2601;
    public const int GL_CLAMP_TO_EDGE = 0x812F;
    public const int GL_TEXTURE0 = 0x84C0;
    public const int GL_RGBA = 0x1908;
    public const int GL_BLEND = 0x0BE2;
    public const int GL_SRC_ALPHA = 0x0302;
    public const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Uniform2fDel(int loc, float x, float y);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Uniform3fDel(int loc, float x, float y, float z);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Uniform4fDel(int loc, float x, float y, float z, float w);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ReadPixelsDel(int x, int y, int w, int h, int format, int type, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ActiveTextureDel(int unit);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void BlendFuncDel(int src, int dst);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate void GenTexturesDel(int count, uint* textures);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void TexImage2DDel(int target, int level, int internalFormat,
        int width, int height, int border, int format, int type, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void TexParameteriDel(int target, int pname, int param);

    private Uniform2fDel? _uniform2f;
    private Uniform3fDel? _uniform3f;
    private Uniform4fDel? _uniform4f;
    private ReadPixelsDel? _readPixels;
    private ActiveTextureDel? _activeTexture;
    private BlendFuncDel? _blendFunc;
    private GenTexturesDel? _genTextures;
    private TexImage2DDel? _texImage2D;
    private TexParameteriDel? _texParameteri;

    public void Resolve(GlInterface gl)
    {
        IntPtr Addr(string n) => gl.GetProcAddress(n);
        if (Addr("glUniform2f") is { } u2 && u2 != IntPtr.Zero)
            _uniform2f = Marshal.GetDelegateForFunctionPointer<Uniform2fDel>(u2);
        if (Addr("glUniform3f") is { } u3 && u3 != IntPtr.Zero)
            _uniform3f = Marshal.GetDelegateForFunctionPointer<Uniform3fDel>(u3);
        if (Addr("glUniform4f") is { } u4 && u4 != IntPtr.Zero)
            _uniform4f = Marshal.GetDelegateForFunctionPointer<Uniform4fDel>(u4);
        if (Addr("glReadPixels") is { } rp && rp != IntPtr.Zero)
            _readPixels = Marshal.GetDelegateForFunctionPointer<ReadPixelsDel>(rp);
        if (Addr("glActiveTexture") is { } at && at != IntPtr.Zero)
            _activeTexture = Marshal.GetDelegateForFunctionPointer<ActiveTextureDel>(at);
        if (Addr("glBlendFunc") is { } bf && bf != IntPtr.Zero)
            _blendFunc = Marshal.GetDelegateForFunctionPointer<BlendFuncDel>(bf);
        if (Addr("glGenTextures") is { } gt && gt != IntPtr.Zero)
            _genTextures = Marshal.GetDelegateForFunctionPointer<GenTexturesDel>(gt);
        if (Addr("glTexImage2D") is { } ti && ti != IntPtr.Zero)
            _texImage2D = Marshal.GetDelegateForFunctionPointer<TexImage2DDel>(ti);
        if (Addr("glTexParameteri") is { } tp && tp != IntPtr.Zero)
            _texParameteri = Marshal.GetDelegateForFunctionPointer<TexParameteriDel>(tp);
    }

    public void Uniform2f(int loc, float x, float y) { if (_uniform2f is not null) _uniform2f(loc, x, y); }
    public void Uniform3f(int loc, float x, float y, float z) { if (_uniform3f is not null) _uniform3f(loc, x, y, z); }
    public void Uniform4f(int loc, float x, float y, float z, float w) { if (_uniform4f is not null) _uniform4f(loc, x, y, z, w); }
    public unsafe void ReadPixels(int x, int y, int w, int h, int format, int type, byte* data)
    {
        if (_readPixels is not null) _readPixels(x, y, w, h, format, type, (IntPtr)data);
    }
    public void ActiveTexture(int unit) { if (_activeTexture is not null) _activeTexture(unit); }
    public void BlendFunc(int src, int dst) { if (_blendFunc is not null) _blendFunc(src, dst); }

    public unsafe int GenTexture(GlInterface gl)
    {
        if (_genTextures is null) return 0;
        uint id;
        _genTextures(1, &id);
        return (int)id;
    }

    public void TexImage2D(int target, int level, int internalFormat,
                           int width, int height, int border,
                           int format, int type, IntPtr data)
    {
        if (_texImage2D is not null)
            _texImage2D(target, level, internalFormat, width, height, border, format, type, data);
    }

    public void TexParameteri(int target, int pname, int param)
    {
        if (_texParameteri is not null) _texParameteri(target, pname, param);
    }
}
