using System.Net;
using System.Text;
using Jellyfin.Plugin.ThemeMusicFinder;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class AnimeThemesProviderTests
{
    private sealed class StubHandler(string metadataJson, byte[] audio) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            if (request.RequestUri!.Host == "api.animethemes.moe")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(metadataJson, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audio)
            });
        }
    }

    private static byte[] Mp3()
    {
        var bytes = new byte[400_000];
        bytes[0] = (byte)'I'; bytes[1] = (byte)'D'; bytes[2] = (byte)'3';
        return bytes;
    }

    private const string ExactMatchJson = """
        {"anime":[{"name":"Solo Leveling","year":2024,"synonyms":[{"text":"Ore dake Level Up na Ken"}],"animethemes":[{"type":"OP","sequence":1,"animethemeentries":[{"nsfw":false,"spoiler":false,"videos":[{"audio":{"link":"https://a.animethemes.moe/SoloLeveling-OP1.ogg"}}]}]}]}]}
        """;

    [Fact]
    public async Task ExactTitleAndYearDownloadsSafeOp1()
    {
        var handler = new StubHandler(ExactMatchJson, [1, 2, 3]);
        var processor = new FakeThemeAudioProcessor((_, _) => Mp3());
        var provider = new AnimeThemesProvider(new HttpClient(handler), processor);
        var series = new Series { Name = "Solo Leveling", ProductionYear = 2024 };

        var result = await provider.FetchAsync(series, CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Found, result.Status);
        Assert.Equal("AnimeThemes", result.Source);
        Assert.Equal("https://a.animethemes.moe/SoloLeveling-OP1.ogg", result.SourceUri!.ToString());
        Assert.Equal(".ogg", Assert.Single(processor.Requested).Extension);
        Assert.Equal(2, handler.Requested.Count);
    }

    [Fact]
    public async Task WrongYearDoesNotDownloadCandidate()
    {
        var handler = new StubHandler(ExactMatchJson, [1, 2, 3]);
        var processor = new FakeThemeAudioProcessor((_, _) => Mp3());
        var provider = new AnimeThemesProvider(new HttpClient(handler), processor);
        var series = new Series { Name = "Solo Leveling", ProductionYear = 2026 };

        var result = await provider.FetchAsync(series, CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotFound, result.Status);
        Assert.Empty(processor.Requested);
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task FuzzyTitleDoesNotDownloadCandidate()
    {
        var handler = new StubHandler(ExactMatchJson, [1, 2, 3]);
        var processor = new FakeThemeAudioProcessor((_, _) => Mp3());
        var provider = new AnimeThemesProvider(new HttpClient(handler), processor);
        var series = new Series { Name = "Solo Level", ProductionYear = 2024 };

        var result = await provider.FetchAsync(series, CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotFound, result.Status);
        Assert.Empty(processor.Requested);
    }

    [Fact]
    public void RejectsKnownBadPlexMappingByIdAndHashOnly()
    {
        const string hash = "4e3885fd0c2662a9cffb13caacd47827f1ffbe19cbb22487487a81755d266940";
        Assert.True(PlexThemeProvider.IsKnownIncorrectMapping("433631", hash));
        Assert.False(PlexThemeProvider.IsKnownIncorrectMapping("433632", hash));
        Assert.False(PlexThemeProvider.IsKnownIncorrectMapping("433631", new string('0', 64)));
    }
}
