using Xunit;

namespace TelegramTrayLauncher.Tests
{
    public class TelegramUpdateParsingTests
    {
        [Fact]
        public void ParseUpdateInfoPayload_ArrayPrefersStableRelease()
        {
            const string payload = """
[
  { "tag_name": "v6.4.4", "prerelease": true, "draft": false },
  { "tag_name": "v6.4.2", "prerelease": false, "draft": false }
]
""";

            var info = TelegramUpdateManager.ParseUpdateInfoPayload(payload);

            Assert.NotNull(info);
            Assert.Equal("6.4.2", info!.Version);
        }

        [Fact]
        public void ParseUpdateInfoPayload_ObjectNormalizesTag()
        {
            const string payload = """{ "tag_name": "v1.2.3-beta+build.1" }""";

            var info = TelegramUpdateManager.ParseUpdateInfoPayload(payload);

            Assert.NotNull(info);
            Assert.Equal("1.2.3", info!.Version);
        }

        [Fact]
        public void ParseUpdateInfoPayload_PlainTextVersion()
        {
            var info = TelegramUpdateManager.ParseUpdateInfoPayload("6.4.2");

            Assert.NotNull(info);
            Assert.Equal("6.4.2", info!.Version);
        }
    }
}
