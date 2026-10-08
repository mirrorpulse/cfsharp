using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CfSharp.Native.Tests;

public sealed class WindowsFileMetadataTests
{
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
