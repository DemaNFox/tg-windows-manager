using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TelegramTrayLauncher.Tests
{
    public class TelegramUpdateManagerTests
    {
        [Fact]
        public async Task DownloadAndReplaceAsync_WritesExeFromZip()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "tg-update-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string targetPath = Path.Combine(tempDir, "Telegram.exe");
            byte[] exeBytes = Encoding.ASCII.GetBytes("zip-exe");
            byte[] zipBytes = BuildZipWithTelegramExe(exeBytes);

            try
            {
                var handler = new StubHttpMessageHandler(_ =>
                {
                    var content = new ByteArrayContent(zipBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                });

                var manager = CreateManager(handler);
                await manager.DownloadAndReplaceAsync(targetPath, "http://example/update.zip", null);

                Assert.True(File.Exists(targetPath));
                Assert.Equal(exeBytes, File.ReadAllBytes(targetPath));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task DownloadAndReplaceAsync_WritesExeFromBinary()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "tg-update-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string targetPath = Path.Combine(tempDir, "Telegram.exe");
            byte[] exeBytes = Encoding.ASCII.GetBytes("bin-exe");

            try
            {
                var handler = new StubHttpMessageHandler(_ =>
                {
                    var content = new ByteArrayContent(exeBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                });

                var manager = CreateManager(handler);
                await manager.DownloadAndReplaceAsync(targetPath, "http://example/Telegram.exe", null);

                Assert.True(File.Exists(targetPath));
                Assert.Equal(exeBytes, File.ReadAllBytes(targetPath));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task DownloadAndReplaceAsync_RealDownload_WritesExe()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "tg-update-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string targetPath = Path.Combine(tempDir, "Telegram.exe");

            try
            {
                var manager = new TelegramUpdateManager("C:\\", _ => { }, new SynchronizationContext());
                await manager.DownloadAndReplaceAsync(targetPath, "https://telegram.org/dl/desktop/win64_portable", null);

                var info = new FileInfo(targetPath);
                Assert.True(info.Exists);
                Assert.True(info.Length > 0);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        private static TelegramUpdateManager CreateManager(HttpMessageHandler handler)
        {
            Func<HttpClient> factory = () => new HttpClient(handler, disposeHandler: false);
            return new TelegramUpdateManager("C:\\", _ => { }, new SynchronizationContext(), factory);
        }

        private static byte[] BuildZipWithTelegramExe(byte[] exeBytes)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("Telegram/Telegram.exe");
                using var entryStream = entry.Open();
                entryStream.Write(exeBytes, 0, exeBytes.Length);
            }

            return stream.ToArray();
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // ignore cleanup errors
            }
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

            public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_handler(request));
            }
        }
    }
}
