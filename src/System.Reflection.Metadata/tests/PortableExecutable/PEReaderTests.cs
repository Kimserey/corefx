// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Internal;
using System.Reflection.Internal.Tests;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.Metadata.Tests;
using System.Runtime.InteropServices;
using Xunit;

namespace System.Reflection.PortableExecutable.Tests
{
    public class PEReaderTests
    {
        [Fact]
        public void Ctor()
        {
            Assert.Throws<ArgumentNullException>(() => new PEReader(null, PEStreamOptions.Default));

            var invalid = new MemoryStream(new byte[] { 1, 2, 3, 4 });

            // the stream should not be disposed if the arguments are bad
            Assert.Throws<ArgumentOutOfRangeException>(() => new PEReader(invalid, (PEStreamOptions)int.MaxValue));
            Assert.True(invalid.CanRead);

            // no BadImageFormatException if we're prefetching the entire image:
            var peReader0 = new PEReader(invalid, PEStreamOptions.PrefetchEntireImage | PEStreamOptions.LeaveOpen);
            Assert.True(invalid.CanRead);
            Assert.Throws<BadImageFormatException>(() => peReader0.PEHeaders);
            invalid.Position = 0;

            // BadImageFormatException if we're prefetching the entire image and metadata:
            Assert.Throws<BadImageFormatException>(() => new PEReader(invalid, PEStreamOptions.PrefetchEntireImage | PEStreamOptions.PrefetchMetadata | PEStreamOptions.LeaveOpen));
            Assert.True(invalid.CanRead);
            invalid.Position = 0;

            // the stream should be disposed if the content is bad:
            Assert.Throws<BadImageFormatException>(() => new PEReader(invalid, PEStreamOptions.PrefetchMetadata));
            Assert.False(invalid.CanRead);

            // the stream should not be disposed if we specified LeaveOpen flag:
            invalid = new MemoryStream(new byte[] { 1, 2, 3, 4 });
            Assert.Throws<BadImageFormatException>(() => new PEReader(invalid, PEStreamOptions.PrefetchMetadata | PEStreamOptions.LeaveOpen));
            Assert.True(invalid.CanRead);

            // valid metadata:
            var valid = new MemoryStream(Misc.Members);
            var peReader = new PEReader(valid, PEStreamOptions.Default);
            Assert.True(valid.CanRead);
            peReader.Dispose();
            Assert.False(valid.CanRead);
        }

        [Fact]
        public void Ctor_Streams()
        {
            Assert.Throws<ArgumentException>(() => new PEReader(new CustomAccessMemoryStream(canRead: false, canSeek: false, canWrite: false)));
            Assert.Throws<ArgumentException>(() => new PEReader(new CustomAccessMemoryStream(canRead: true, canSeek: false, canWrite: false)));

            var s = new CustomAccessMemoryStream(canRead: true, canSeek: true, canWrite: false);

            new PEReader(s);
            new PEReader(s, PEStreamOptions.Default, 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => new PEReader(s, PEStreamOptions.Default, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PEReader(s, PEStreamOptions.Default, 1));
        }

        [Fact]
        public unsafe void Ctor_Loaded()
        {
            byte b = 1;
            Assert.True(new PEReader(&b, 1, isLoadedImage: true).IsLoadedImage);
            Assert.False(new PEReader(&b, 1, isLoadedImage: false).IsLoadedImage);

            Assert.True(new PEReader(new MemoryStream(), PEStreamOptions.IsLoadedImage).IsLoadedImage);
            Assert.False(new PEReader(new MemoryStream()).IsLoadedImage);
        }

        [Fact]
        public void FromEmptyStream()
        {
            Assert.Throws<BadImageFormatException>(() => new PEReader(new MemoryStream(), PEStreamOptions.PrefetchMetadata));
            Assert.Throws<BadImageFormatException>(() => new PEReader(new MemoryStream(), PEStreamOptions.PrefetchMetadata | PEStreamOptions.PrefetchEntireImage));
        }

        [Fact(Skip = "https://github.com/dotnet/corefx/issues/7996")]
        [ActiveIssue(7996)]
        public void SubStream()
        {
            var stream = new MemoryStream();
            stream.WriteByte(0xff);
            stream.Write(Misc.Members, 0, Misc.Members.Length);
            stream.WriteByte(0xff);
            stream.WriteByte(0xff);

            stream.Position = 1;
            var peReader1 = new PEReader(stream, PEStreamOptions.LeaveOpen, Misc.Members.Length);

            Assert.Equal(Misc.Members.Length, peReader1.GetEntireImage().Length);
            peReader1.GetMetadataReader();

            stream.Position = 1;
            var peReader2 = new PEReader(stream, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchMetadata, Misc.Members.Length);

            Assert.Equal(Misc.Members.Length, peReader2.GetEntireImage().Length);
            peReader2.GetMetadataReader();
            stream.Position = 1;

            var peReader3 = new PEReader(stream, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchEntireImage, Misc.Members.Length);

            Assert.Equal(Misc.Members.Length, peReader3.GetEntireImage().Length);
            peReader3.GetMetadataReader();
        }

        // TODO: Switch to small checked in native image.
        /*
        [Fact]
        public void OpenNativeImage()
        {
            using (var reader = new PEReader(File.OpenRead(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll"))))
            {
                Assert.False(reader.HasMetadata);
                Assert.True(reader.PEHeaders.IsDll);
                Assert.False(reader.PEHeaders.IsExe);
                Assert.Throws<InvalidOperationException>(() => reader.GetMetadataReader());
            }
        }
        */

        [Fact]
        public void IL_LazyLoad()
        {
            var peStream = new MemoryStream(Misc.Members);
            using (var reader = new PEReader(peStream, PEStreamOptions.LeaveOpen))
            {
                var md = reader.GetMetadataReader();
                var il = reader.GetMethodBody(md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(1)).RelativeVirtualAddress);

                Assert.Equal(new byte[] { 0, 42 }, il.GetILBytes());
                Assert.Equal(8, il.MaxStack);
            }
        }

        [Fact]
        public void IL_EagerLoad()
        {
            var peStream = new MemoryStream(Misc.Members);
            using (var reader = new PEReader(peStream, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchMetadata | PEStreamOptions.PrefetchEntireImage))
            {
                var md = reader.GetMetadataReader();
                var il = reader.GetMethodBody(md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(1)).RelativeVirtualAddress);

                Assert.Equal(new byte[] { 0, 42 }, il.GetILBytes());
                Assert.Equal(8, il.MaxStack);
            }
        }

        [Fact]
        public void Metadata_LazyLoad()
        {
            var peStream = new MemoryStream(Misc.Members);
            using (var reader = new PEReader(peStream, PEStreamOptions.LeaveOpen))
            {
                var md = reader.GetMetadataReader();
                var method = md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(1));

                Assert.Equal("MC1", md.GetString(method.Name));
            }
        }

        [Fact]
        public void Metadata_EagerLoad()
        {
            var peStream = new MemoryStream(Misc.Members);
            using (var reader = new PEReader(peStream, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchMetadata))
            {
                var md = reader.GetMetadataReader();
                var method = md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(1));
                Assert.Equal("MC1", md.GetString(method.Name));

                Assert.Throws<InvalidOperationException>(() => reader.GetEntireImage());
                Assert.Throws<InvalidOperationException>(() => reader.GetMethodBody(method.RelativeVirtualAddress));
            }
        }

        [Fact]
        public void EntireImage_LazyLoad()
        {
            var peStream = new MemoryStream(Misc.Members);
            using (var reader = new PEReader(peStream, PEStreamOptions.LeaveOpen))
            {
                Assert.Equal(4608, reader.GetEntireImage().Length);
            }
        }

        [Fact]
        public void EntireImage_EagerLoad()
        {
            var peStream = new MemoryStream(Misc.Members);
            using (var reader = new PEReader(peStream, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchMetadata | PEStreamOptions.PrefetchEntireImage))
            {
                Assert.Equal(4608, reader.GetEntireImage().Length);
            }
        }

        [Fact]
        [PlatformSpecific(PlatformID.Windows)]
        public void GetMethodBody_Loaded()
        {
            LoaderUtilities.LoadPEAndValidate(Misc.Members, reader =>
            {
                var md = reader.GetMetadataReader();
                var il = reader.GetMethodBody(md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(1)).RelativeVirtualAddress);

                Assert.Equal(new byte[] { 0, 42 }, il.GetILBytes());
                Assert.Equal(8, il.MaxStack);
            });
        }

        [Fact]
        public void GetSectionData()
        {
            var peStream = new MemoryStream(Misc.Members);
            using (var reader = new PEReader(peStream))
            {
                ValidateSectionData(reader);
            }
        }

        [Fact]
        [PlatformSpecific(PlatformID.Windows)]
        public void GetSectionData_Loaded()
        {
            LoaderUtilities.LoadPEAndValidate(Misc.Members, ValidateSectionData);
        }

        private unsafe void ValidateSectionData(PEReader reader)
        {
            var relocBlob1 = reader.GetSectionData(".reloc").GetContent();
            var relocBlob2 = reader.GetSectionData(0x6000).GetContent();

            AssertEx.Equal(new byte[] 
            {
                0x00, 0x20, 0x00, 0x00,
                0x0C, 0x00, 0x00, 0x00,
                0xD0, 0x38, 0x00, 0x00
            }, relocBlob1);

            AssertEx.Equal(relocBlob1, relocBlob2);

            var data = reader.GetSectionData(0x5fff);
            Assert.True(data.Pointer == null);
            Assert.Equal(0, data.Length);
            AssertEx.Equal(new byte[0], data.GetContent());

            data = reader.GetSectionData(0x600B);
            Assert.True(data.Pointer != null);
            Assert.Equal(1, data.Length);
            AssertEx.Equal(new byte[] { 0x00 }, data.GetContent());

            data = reader.GetSectionData(0x600C);
            Assert.True(data.Pointer == null);
            Assert.Equal(0, data.Length);
            AssertEx.Equal(new byte[0], data.GetContent());

            data = reader.GetSectionData(0x600D);
            Assert.True(data.Pointer == null);
            Assert.Equal(0, data.Length);
            AssertEx.Equal(new byte[0], data.GetContent());

            data = reader.GetSectionData(int.MaxValue);
            Assert.True(data.Pointer == null);
            Assert.Equal(0, data.Length);
            AssertEx.Equal(new byte[0], data.GetContent());

            data = reader.GetSectionData(".nonexisting");
            Assert.True(data.Pointer == null);
            Assert.Equal(0, data.Length);
            AssertEx.Equal(new byte[0], data.GetContent());

            data = reader.GetSectionData("");
            Assert.True(data.Pointer == null);
            Assert.Equal(0, data.Length);
            AssertEx.Equal(new byte[0], data.GetContent());
        }

        [Fact]
        public void GetSectionData_Errors()
        {
            var peStream = new MemoryStream(Misc.Members);
            using (var reader = new PEReader(peStream))
            {
                Assert.Throws<ArgumentNullException>(() => reader.GetSectionData(null));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetSectionData(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetSectionData(int.MinValue));
            }
        }
    }
}
