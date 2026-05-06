// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.ClientModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using _Type.File;
using NUnit.Framework;
using TypeSpec.Http;
using SystemFile = System.IO.File;

namespace TestProjects.Spector.Tests.Http._Type._File
{
    public class FileTests : SpectorTestBase
    {
        private readonly string _samplePngPath = Path.Combine(SpectorServer.GetSpecDirectory(), "assets", "image.png");

        [SpectorTest]
        public Task UploadFileSpecificContentType() => Test(async (host) =>
        {
            await using var imageStream = SystemFile.OpenRead(_samplePngPath);
            BinaryContent content = BinaryContent.Create(imageStream);

            var response = await new FileClient(host, null).GetBodyClient()
                .UploadFileSpecificContentTypeAsync(content);

            Assert.AreEqual(204, response.GetRawResponse().Status);
        });

        [SpectorTest]
        public Task UploadFileSpecificContentType_Conv() => Test(async (host) =>
        {
            await using var imageStream = SystemFile.OpenRead(_samplePngPath);
            using FileBinaryContent content = new FileBinaryContent(imageStream);

            var response = await new FileClient(host, null).GetBodyClient()
                .UploadFileSpecificContentTypeAsync(content);

            Assert.AreEqual(204, response.GetRawResponse().Status);
        });

        [SpectorTest]
        public Task UploadFileJsonContentType() => Test(async (host) =>
        {
            BinaryData json = BinaryData.FromString("{\"message\":\"test file content\"}");
            BinaryContent content = BinaryContent.Create(json);

            var response = await new FileClient(host, null).GetBodyClient()
                .UploadFileJsonContentTypeAsync(content);

            Assert.AreEqual(204, response.GetRawResponse().Status);
        });

        [SpectorTest]
        public Task UploadFileJsonContentType_Conv() => Test(async (host) =>
        {
            using var jsonStream = new MemoryStream(Encoding.UTF8.GetBytes("{\"message\":\"test file content\"}"));
            using FileBinaryContent file = new FileBinaryContent(jsonStream);

            var response = await new FileClient(host, null).GetBodyClient()
                .UploadFileJsonContentTypeAsync(file);

            Assert.AreEqual(204, response.GetRawResponse().Status);
        });
    }
}
