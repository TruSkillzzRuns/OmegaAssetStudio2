using System.Runtime.InteropServices;

namespace OmegaAssetStudio2.App.Services;

/// <summary>
/// Asks the user for a folder using the shell's own dialog.
/// </summary>
/// <remarks>
/// This exists because <c>Windows.Storage.Pickers.FolderPicker</c> cannot be
/// relied on here. It fails with <c>E_FAIL</c> (0x80004005) the moment the
/// application runs elevated, which this one often does — the game usually
/// lives somewhere only an administrator may write to, so running it as one is
/// the normal way out of that. The failure is in the WinRT wrapper, not in the
/// dialog underneath it, so calling the dialog directly works in both cases.
/// <para>
/// Nothing else is different: this is the same folder browser the user sees
/// everywhere in Windows.
/// </para>
/// </remarks>
public static class FolderBrowser
{
    private const int Cancelled = unchecked((int)0x800704C7);

    /// <summary>Pick folders rather than files, and only real ones.</summary>
    private const uint PickFolders = 0x00000020;
    private const uint ForceFileSystem = 0x00000040;

    /// <summary>
    /// Shows the folder browser over <paramref name="owner"/>. Returns the
    /// chosen path, or null if the user backed out.
    /// </summary>
    public static string? Pick(nint owner, string title)
    {
        IFileDialog dialog = (IFileDialog)new FileOpenDialog();

        try
        {
            dialog.SetOptions(PickFolders | ForceFileSystem);
            dialog.SetTitle(title);

            int result = dialog.Show(owner);

            if (result == Cancelled) return null;
            if (result < 0) Marshal.ThrowExceptionForHR(result);

            dialog.GetResult(out IShellItem item);

            try
            {
                // SIGDN_FILESYSPATH — the path on disk, not a display name.
                item.GetDisplayName(0x80058000, out nint raw);

                try { return Marshal.PtrToStringUni(raw); }
                finally { Marshal.FreeCoTaskMem(raw); }
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    // The methods must be declared in their original order even though most go
    // unused, because they are called by position in the interface's table.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint parent);

        void SetFileTypes(uint count, nint types);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem place, int where);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(nint filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint binding, ref Guid handler, ref Guid item, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, out nint name);
        void GetAttributes(uint mask, out uint attributes);
    }
}
