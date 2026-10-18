using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CfSharp.Native.Tests;

public sealed class WindowsFileMetadataTests
{
    [Fact]
    public void DeleteGuardRejectsReadersEvenWhenTheyShareDelete()
    {
        string area = Path.Combine(Path.GetTempPath(), "CfSharp-delete-reader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        string path = Path.Combine(area, "original");
        File.WriteAllText(path, "original");
        try
        {
            using (FileStream reader = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                Win32Exception failure = Assert.Throws<Win32Exception>(() => WindowsFileMetadata.OpenForDelete(path));
                Assert.Equal(32, failure.NativeErrorCode);
                Assert.Equal("original", File.ReadAllText(path));
            }

            using var guard = WindowsFileMetadata.OpenForDelete(path);
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        }
        finally
        {
            Directory.Delete(area, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.16299")]
    public void DeleteUsesTheGuardedObjectAndCompletesAfterHandleClose(bool directory)
    {
        Assert.Equal(1, Marshal.SizeOf<WindowsFileMetadata.FileDispositionInformation>());
        string area = Path.Combine(Path.GetTempPath(), "CfSharp-handle-delete", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        string path = Path.Combine(area, "original");
        try
        {
            if (directory) { Directory.CreateDirectory(path); }
            else { File.WriteAllText(path, "original"); }
            using (var handle = WindowsFileMetadata.OpenForDelete(path))
            {
                Assert.Throws<IOException>(() => { if (directory) { Directory.Move(path, path + ".moved"); } else { File.Move(path, path + ".moved"); } });
                WindowsFileMetadata.Delete(handle.DangerousGetHandle());
                Assert.True(WindowsFileMetadata.Read(handle.DangerousGetHandle()).DeletePending);
            }

            Assert.False(File.Exists(path) || Directory.Exists(path));
            File.WriteAllText(path, "replacement");
            Assert.Equal("replacement", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(area, recursive: true);
        }
    }

    [Fact]
    public void DirectoryGuardPreventsAncestorReplacementUntilReleased()
    {
        string area = Path.Combine(Path.GetTempPath(), "CfSharp-parent-guard", Guid.NewGuid().ToString("N"));
        string parent = Path.Combine(area, "parent");
        Directory.CreateDirectory(parent);
        try
        {
            using (var handle = WindowsFileMetadata.OpenDirectoryGuard(parent))
            {
                Assert.Throws<IOException>(() => Directory.Move(parent, Path.Combine(area, "replaced")));
                Assert.True(Directory.Exists(parent));
            }

            Directory.Move(parent, Path.Combine(area, "replaced"));
        }
        finally
        {
            Directory.Delete(area, recursive: true);
        }
    }

    [Fact]
    public unsafe void RenameInformationUsesTheNativePointerAlignmentAndUtf16TrailingField()
    {
        WindowsFileMetadata.FileRenameInformation value = default;
        byte* start = (byte*)&value;
        Assert.Equal(0, (byte*)&value.Flags - start);
        Assert.Equal(IntPtr.Size, (byte*)&value.RootDirectory - start);
        Assert.Equal(2 * IntPtr.Size, (byte*)&value.FileNameLength - start);
        Assert.Equal((2 * IntPtr.Size) + 4, (byte*)&value.FileName - start);
        MethodInfo method = typeof(WindowsFileMetadata).GetMethod("SetFileInformationByHandle", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True(method.GetCustomAttribute<LibraryImportAttribute>()!.SetLastError);
        Assert.Equal(new[] { typeof(CallConvStdcall) }, method.GetCustomAttribute<UnmanagedCallConvAttribute>()!.CallConvs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenameUsesTheOpenedObjectAndNeverReplacesAnOccupant(bool caseOnly)
    {
        string area = Path.Combine(Path.GetTempPath(), "CfSharp-handle-rename", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        string source = Path.Combine(area, "source.txt");
        string target = Path.Combine(area, caseOnly ? "SOURCE.txt" : "target.txt");
        File.WriteAllText(source, "original");
        try
        {
            using var handle = WindowsFileMetadata.OpenForRename(source);
            Guid id = WindowsFileMetadata.ReadIdentity(handle.DangerousGetHandle()).FileId;
            Assert.Throws<IOException>(() => File.Move(source, Path.Combine(area, "competing.txt")));
            WindowsFileMetadata.Rename(handle.DangerousGetHandle(), target);
            Assert.Equal(target, WindowsFileMetadata.ReadFinalPath(handle.DangerousGetHandle()));
            Assert.Equal(id, WindowsFileMetadata.ReadIdentity(handle.DangerousGetHandle()).FileId);
            string occupied = Path.Combine(area, "occupied.txt");
            File.WriteAllText(occupied, "retain");
            Assert.Throws<Win32Exception>(() => WindowsFileMetadata.Rename(handle.DangerousGetHandle(), occupied));
            handle.Dispose();
            Assert.Equal("retain", File.ReadAllText(occupied));
            Assert.Equal("original", File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(area, recursive: true);
        }
    }

    [Fact]
    public void FinalPathImportHasExplicitUtf16PointerAndWindowsCallingConvention()
    {
        MethodInfo method = typeof(WindowsFileMetadata).GetMethod("GetFinalPathNameByHandle",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        LibraryImportAttribute import = method.GetCustomAttribute<LibraryImportAttribute>()!;
        Assert.Equal("kernel32.dll", import.LibraryName);
        Assert.Equal("GetFinalPathNameByHandleW", import.EntryPoint);
        Assert.True(import.SetLastError);
        Assert.Equal(typeof(uint), method.ReturnType);
        Assert.Equal(new[] { typeof(nint), typeof(char).MakePointerType(), typeof(uint), typeof(uint) },
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(new[] { typeof(CallConvStdcall) }, method.GetCustomAttribute<UnmanagedCallConvAttribute>()!.CallConvs);
    }

    [Fact]
    public void FinalPathObservesLongDirectoryRenameAndRetainsBorrowedHandleIdentity()
    {
        string area = Path.Combine(Path.GetTempPath(), "CfSharp-final-path", Guid.NewGuid().ToString("N"));
        string parent = Path.Combine(area, new string('a', 64), new string('b', 64), new string('c', 64));
        string source = Path.Combine(parent, "OriginalCase");
        string destination = Path.Combine(parent, "originalcase");
        Directory.CreateDirectory(source);
        try
        {
            using var handle = WindowsFileMetadata.Open(@"\\?\" + source);
            WindowsFileMetadata.FileIdentity identity = WindowsFileMetadata.ReadIdentity(handle.DangerousGetHandle());
            Assert.True(source.Length > 260);
            Assert.Equal(source, WindowsFileMetadata.ReadFinalPath(handle.DangerousGetHandle()));
            Directory.Move(@"\\?\" + source, @"\\?\" + destination);
            Assert.Equal(destination, WindowsFileMetadata.ReadFinalPath(handle.DangerousGetHandle()));
            Assert.Equal(identity.FileId, WindowsFileMetadata.ReadIdentity(handle.DangerousGetHandle()).FileId);
            Assert.False(handle.IsClosed);
        }
        finally
        {
            Directory.Delete(area, recursive: true);
        }
    }

    [Fact]
    public void FinalPathPreservesNativeFailure()
    {
        Win32Exception failure = Assert.Throws<Win32Exception>(() => WindowsFileMetadata.ReadFinalPath(0));
        Assert.Equal(6, failure.NativeErrorCode);
    }
}
