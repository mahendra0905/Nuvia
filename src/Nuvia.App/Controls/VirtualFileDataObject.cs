using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Nuvia.App.Controls;

/// <summary>
/// A minimal COM <see cref="IDataObject"/> that lets the app hand "virtual" files to Explorer (or any
/// drop target) via drag-and-drop, without those files existing on disk beforehand. The bytes for each
/// file are produced on demand by a caller-supplied delegate at the moment the target asks for them
/// (i.e. when the user drops), so a large download only runs if the user actually completes the drop.
///
/// This is the well-known FileGroupDescriptorW + deferred FileContents pattern. It is deliberately
/// isolated: it performs no Telegram, index or UI work itself — the content delegate does the download.
/// Each file's contents are materialised to a private temp file first, then handed to the target as a
/// read-only OS stream; the temp files are deleted after the drag loop ends.
/// </summary>
internal sealed class VirtualFileDataObject : ComIDataObject
{
    /// <summary>One virtual file offered to the drop target.</summary>
    public sealed class FileItem
    {
        /// <summary>Safe file name (a single path component) shown to the target.</summary>
        public required string Name { get; init; }

        /// <summary>Known size in bytes if available; lets Explorer show an accurate progress bar.</summary>
        public long? Length { get; init; }

        /// <summary>Writes the file's bytes into the given stream. Runs when the target requests contents.</summary>
        public required Action<Stream> WriteContents { get; init; }
    }

    private readonly List<FileItem> _items;
    private readonly List<string> _tempFiles = new();
    private readonly string _tempDir;

    private static readonly short CF_FILEDESCRIPTORW = (short)NativeMethods.RegisterClipboardFormat("FileGroupDescriptorW");
    private static readonly short CF_FILECONTENTS = (short)NativeMethods.RegisterClipboardFormat("FileContents");

    private VirtualFileDataObject(List<FileItem> items)
    {
        _items = items;
        _tempDir = Path.Combine(Path.GetTempPath(), "Nuvia", "dragout", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Runs a blocking OLE drag with the given virtual files as the payload. Returns the effect the target
    /// applied (Copy on a completed drop, None on cancel). Any temp files created for the transfer are
    /// removed before returning. Must be called on the UI (STA) thread.
    /// </summary>
    public static DragDropEffects StartDrag(IReadOnlyList<FileItem> items, DragDropEffects allowed)
    {
        if (items.Count == 0)
            return DragDropEffects.None;

        var data = new VirtualFileDataObject(items.ToList());
        try
        {
            var dropSource = new DropSource();
            NativeMethods.DoDragDrop(data, dropSource, (int)allowed, out var effect);
            return (DragDropEffects)effect;
        }
        finally
        {
            data.Cleanup();
        }
    }

    private void Cleanup()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
        }

        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // ---------------- IDataObject (COM) ----------------

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;

        if (format.cfFormat == CF_FILEDESCRIPTORW && format.tymed.HasFlag(TYMED.TYMED_HGLOBAL))
        {
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = BuildFileGroupDescriptor();
            medium.pUnkForRelease = null;
            return;
        }

        if (format.cfFormat == CF_FILECONTENTS && format.tymed.HasFlag(TYMED.TYMED_ISTREAM))
        {
            var index = format.lindex;
            if (index < 0 || index >= _items.Count)
                Marshal.ThrowExceptionForHR(NativeMethods.DV_E_LINDEX);

            medium.tymed = TYMED.TYMED_ISTREAM;
            medium.unionmember = BuildFileContents(index);
            medium.pUnkForRelease = null;
            return;
        }

        Marshal.ThrowExceptionForHR(NativeMethods.DV_E_FORMATETC);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
        Marshal.ThrowExceptionForHR(NativeMethods.DV_E_FORMATETC);

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat == CF_FILEDESCRIPTORW && format.tymed.HasFlag(TYMED.TYMED_HGLOBAL))
            return NativeMethods.S_OK;
        if (format.cfFormat == CF_FILECONTENTS && format.tymed.HasFlag(TYMED.TYMED_ISTREAM))
            return NativeMethods.S_OK;
        return NativeMethods.DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return NativeMethods.DATA_S_SAMEFORMATETC;
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        // Drop targets sometimes push optional formats back at the source (e.g. "Preferred DropEffect").
        // We do not consume them, but must release the medium when asked so nothing leaks.
        if (release)
            NativeMethods.ReleaseStgMedium(ref medium);
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
            throw new NotImplementedException(); // maps to E_NOTIMPL for the caller

        var formats = new List<FORMATETC>
        {
            new()
            {
                cfFormat = CF_FILEDESCRIPTORW, ptd = IntPtr.Zero, dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1, tymed = TYMED.TYMED_HGLOBAL,
            },
        };

        for (var i = 0; i < _items.Count; i++)
        {
            formats.Add(new FORMATETC
            {
                cfFormat = CF_FILECONTENTS, ptd = IntPtr.Zero, dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = i, tymed = TYMED.TYMED_ISTREAM,
            });
        }

        return new EnumFormatEtc(formats);
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return NativeMethods.OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) =>
        Marshal.ThrowExceptionForHR(NativeMethods.OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return NativeMethods.OLE_E_ADVISENOTSUPPORTED;
    }

    // ---------------- payload builders ----------------

    private IntPtr BuildFileGroupDescriptor()
    {
        var count = _items.Count;
        var descriptorSize = Marshal.SizeOf<NativeMethods.FILEDESCRIPTOR>();
        var totalSize = sizeof(uint) + count * descriptorSize;

        var handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT, (UIntPtr)totalSize);
        if (handle == IntPtr.Zero)
            throw new OutOfMemoryException();

        var ptr = NativeMethods.GlobalLock(handle);
        try
        {
            Marshal.WriteInt32(ptr, count);
            var cursor = ptr + sizeof(uint);

            foreach (var item in _items)
            {
                var descriptor = new NativeMethods.FILEDESCRIPTOR
                {
                    dwFlags = NativeMethods.FD_PROGRESSUI | NativeMethods.FD_ATTRIBUTES
                              | (item.Length.HasValue ? NativeMethods.FD_FILESIZE : 0u),
                    dwFileAttributes = NativeMethods.FILE_ATTRIBUTE_NORMAL,
                    cFileName = item.Name,
                };

                if (item.Length is { } len)
                {
                    descriptor.nFileSizeHigh = (uint)(len >> 32);
                    descriptor.nFileSizeLow = (uint)(len & 0xFFFFFFFF);
                }

                Marshal.StructureToPtr(descriptor, cursor, false);
                cursor += descriptorSize;
            }
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }

        return handle;
    }

    private IntPtr BuildFileContents(int index)
    {
        var item = _items[index];
        Directory.CreateDirectory(_tempDir);

        var tempPath = Path.Combine(_tempDir, index.ToString() + "_" + item.Name);
        using (var fs = File.Create(tempPath))
        {
            item.WriteContents(fs); // may throw → surfaced to the target as a failed FileContents
        }

        _tempFiles.Add(tempPath);

        NativeMethods.SHCreateStreamOnFileEx(
            tempPath,
            NativeMethods.STGM_READ | NativeMethods.STGM_SHARE_DENY_WRITE,
            0, false, IntPtr.Zero, out var stream);

        // Hand the target an AddRef'd interface pointer it will release via ReleaseStgMedium.
        return Marshal.GetComInterfaceForObject(stream, typeof(IStream));
    }
}
