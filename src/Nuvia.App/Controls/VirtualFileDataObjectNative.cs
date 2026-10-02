using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Nuvia.App.Controls;

/// <summary>
/// The <c>IDropSource</c> the app supplies to <c>DoDragDrop</c>. It implements the standard drag policy:
/// cancel on Escape, drop when the left button is released, otherwise keep dragging with default cursors.
/// </summary>
internal sealed class DropSource : NativeMethods.IDropSource
{
    private const uint MK_LBUTTON = 0x0001;

    public int QueryContinueDrag(bool escapePressed, uint grfKeyState)
    {
        if (escapePressed)
            return NativeMethods.DRAGDROP_S_CANCEL;
        if ((grfKeyState & MK_LBUTTON) == 0)
            return NativeMethods.DRAGDROP_S_DROP;
        return NativeMethods.S_OK;
    }

    public int GiveFeedback(uint dwEffect) => NativeMethods.DRAGDROP_S_USEDEFAULTCURSORS;
}

/// <summary>A trivial <see cref="IEnumFORMATETC"/> over a fixed list of formats.</summary>
internal sealed class EnumFormatEtc : IEnumFORMATETC
{
    private readonly FORMATETC[] _formats;
    private int _index;

    public EnumFormatEtc(IEnumerable<FORMATETC> formats) => _formats = formats.ToArray();

    private EnumFormatEtc(FORMATETC[] formats, int index)
    {
        _formats = formats;
        _index = index;
    }

    public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
    {
        var fetched = 0;
        while (_index < _formats.Length && fetched < celt)
        {
            rgelt[fetched] = _formats[_index];
            _index++;
            fetched++;
        }

        if (pceltFetched is { Length: > 0 })
            pceltFetched[0] = fetched;

        return fetched == celt ? NativeMethods.S_OK : NativeMethods.S_FALSE;
    }

    public int Skip(int celt)
    {
        _index += celt;
        return _index <= _formats.Length ? NativeMethods.S_OK : NativeMethods.S_FALSE;
    }

    public int Reset()
    {
        _index = 0;
        return NativeMethods.S_OK;
    }

    public void Clone(out IEnumFORMATETC newEnum) => newEnum = new EnumFormatEtc(_formats, _index);
}

/// <summary>P/Invoke surface and native structs used by <see cref="VirtualFileDataObject"/>.</summary>
internal static class NativeMethods
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int DRAGDROP_S_DROP = 0x00040100;
    public const int DRAGDROP_S_CANCEL = 0x00040101;
    public const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
    public const int DATA_S_SAMEFORMATETC = 0x00040130;

    public static readonly int DV_E_FORMATETC = unchecked((int)0x80040064);
    public static readonly int DV_E_LINDEX = unchecked((int)0x80040068);
    public static readonly int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);

    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint GMEM_ZEROINIT = 0x0040;

    public const uint STGM_READ = 0x00000000;
    public const uint STGM_SHARE_DENY_WRITE = 0x00000020;

    public const uint FD_ATTRIBUTES = 0x0004;
    public const uint FD_FILESIZE = 0x0040;
    public const uint FD_PROGRESSUI = 0x4000;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x0080;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FILEDESCRIPTOR
    {
        public uint dwFlags;
        public Guid clsid;
        public SIZE sizel;
        public POINT pointl;
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
    }

    [ComVisible(true)]
    [Guid("00000121-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropSource
    {
        [PreserveSig]
        int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool fEscapePressed, uint grfKeyState);

        [PreserveSig]
        int GiveFeedback(uint dwEffect);
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    public static extern int DoDragDrop(
        System.Runtime.InteropServices.ComTypes.IDataObject dataObject,
        IDropSource dropSource,
        int allowedEffects,
        out int finalEffect);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    public static extern void SHCreateStreamOnFileEx(
        string fileName,
        uint grfMode,
        uint dwAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool fCreate,
        IntPtr pstmTemplate,
        out IStream stream);
}
