using System.Net;
using System.Text;
using Jellyfin.Plugin.ThemeMusicFinder;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThemeMusicFinder.Tests;

public sealed class AnimeThemesProviderTests
{
    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

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

    private sealed class RoutingHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
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
        Assert.Equal("AnimeThemes OP1", result.Source);
        Assert.Equal("https://a.animethemes.moe/SoloLeveling-OP1.ogg", result.SourceUri!.ToString());
        Assert.Equal(".ogg", Assert.Single(processor.Requested).Extension);
        Assert.Equal(2, handler.Requested.Count);
    }

    [Fact]
    public async Task OfficialAnimeSynonymsFieldMatchesAnExactAlias()
    {
        const string json = """
            {"anime":[{"name":"AI no Idenshi","year":2023,"animesynonyms":[{"text":"The Gene of AI"}],"animethemes":[{"type":"OP","sequence":1,"animethemeentries":[{"nsfw":false,"spoiler":false,"videos":[{"audio":{"link":"https://a.animethemes.moe/AInoIdenshi-OP1.ogg"}}]}]}]}]}
            """;
        var handler = new StubHandler(json, [1, 2, 3]);
        var provider = new AnimeThemesProvider(
            new HttpClient(handler),
            new FakeThemeAudioProcessor((_, _) => Mp3()));

        var result = await provider.FetchAsync(
            new Series { Name = "The Gene of AI", ProductionYear = 2023 },
            CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Found, result.Status);
        Assert.Equal("https://a.animethemes.moe/AInoIdenshi-OP1.ogg", result.SourceUri!.ToString());
    }

    [Fact]
    public async Task MetadataQueryUsesYearFilterAndBoundedPage()
    {
        var handler = new StubHandler(ExactMatchJson, [1, 2, 3]);
        var provider = new AnimeThemesProvider(
            new HttpClient(handler),
            new FakeThemeAudioProcessor((_, _) => Mp3()));

        await provider.FetchAsync(
            new Series { Name = "Solo Leveling", ProductionYear = 2024 },
            CancellationToken.None);

        var query = Uri.UnescapeDataString(handler.Requested[0].Query);
        Assert.Contains("filter[year]=2024", query, StringComparison.Ordinal);
        Assert.Contains("page[size]=5", query, StringComparison.Ordinal);
        Assert.Contains("include=animesynonyms", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchingYearSuffixIsRemovedBeforeLookup()
    {
        const string json = """
            {"anime":[{"name":"Kakegurui Twin","year":2022,"animethemes":[{"type":"OP","sequence":1,"animethemeentries":[{"nsfw":false,"spoiler":false,"videos":[{"audio":{"link":"https://a.animethemes.moe/KakeguruiTwin-OP1.ogg"}}]}]}]}]}
            """;
        var handler = new StubHandler(json, [1, 2, 3]);
        var provider = new AnimeThemesProvider(
            new HttpClient(handler),
            new FakeThemeAudioProcessor((_, _) => Mp3()));

        var result = await provider.FetchAsync(
            new Series { Name = "Kakegurui Twin (2022)", ProductionYear = 2022 },
            CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Found, result.Status);
        var query = Uri.UnescapeDataString(handler.Requested[0].Query);
        Assert.Contains("q=Kakegurui Twin", query, StringComparison.Ordinal);
        Assert.DoesNotContain("(2022)", query, StringComparison.Ordinal);
    }

    [Fact]
    public void AYearSuffixIsOnlyRemovedWhenItMatchesTheMetadataYear()
    {
        Assert.Equal(
            "Angels of Death (2021)",
            AnimeThemesProvider.StripMatchingYearSuffix("Angels of Death (2021)", 2018));
        Assert.Equal(
            "Kakegurui Twin",
            AnimeThemesProvider.StripMatchingYearSuffix("Kakegurui Twin (2022)", 2022));
    }

    [Fact]
    public async Task OriginalTitleIsTriedAfterTheDisplayTitleMisses()
    {
        const string json = """
            {"anime":[{"name":"Kimi to Boku no Saigo no Senjou","year":2020,"animethemes":[{"type":"OP","sequence":1,"animethemeentries":[{"nsfw":false,"spoiler":false,"videos":[{"audio":{"link":"https://a.animethemes.moe/KimiSen-OP1.ogg"}}]}]}]}]}
            """;
        var handler = new RoutingHandler(uri =>
        {
            if (uri.Host == "a.animethemes.moe")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            var query = Uri.UnescapeDataString(uri.Query);
            var body = query.Contains("Kimi to Boku", StringComparison.Ordinal)
                ? json
                : "{\"anime\":[]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        var provider = new AnimeThemesProvider(
            new HttpClient(handler),
            new FakeThemeAudioProcessor((_, _) => Mp3()));
        var series = new Series
        {
            Name = "Our Last Crusade or the Rise of a New World",
            OriginalTitle = "Kimi to Boku no Saigo no Senjou",
            ProductionYear = 2020
        };

        var result = await provider.FetchAsync(series, CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Found, result.Status);
        Assert.Equal(3, handler.Requested.Count);
        Assert.Contains("Our Last Crusade", Uri.UnescapeDataString(handler.Requested[0].Query));
        Assert.Contains("Kimi to Boku", Uri.UnescapeDataString(handler.Requested[1].Query));
    }

    [Fact]
    public async Task SafeEd1IsUsedOnlyWhenNoSafeOp1Exists()
    {
        const string json = """
            {"anime":[{"name":"Uzumaki","year":2024,"animethemes":[{"type":"ED","sequence":1,"animethemeentries":[{"nsfw":false,"spoiler":false,"videos":[{"audio":{"link":"https://a.animethemes.moe/Uzumaki-ED1.ogg"}}]}]}]}]}
            """;
        var handler = new StubHandler(json, [1, 2, 3]);
        var provider = new AnimeThemesProvider(
            new HttpClient(handler),
            new FakeThemeAudioProcessor((_, _) => Mp3()));

        var result = await provider.FetchAsync(
            new Series { Name = "Uzumaki", ProductionYear = 2024 },
            CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Found, result.Status);
        Assert.Equal("AnimeThemes ED1", result.Source);
        Assert.EndsWith("Uzumaki-ED1.ogg", result.SourceUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Op1WinsWhenEd1AppearsFirst()
    {
        const string json = """
            {"anime":[{"name":"Example","year":2024,"animethemes":[{"type":"ED","sequence":1,"animethemeentries":[{"nsfw":false,"spoiler":false,"videos":[{"audio":{"link":"https://a.animethemes.moe/Example-ED1.ogg"}}]}]},{"type":"OP","sequence":1,"animethemeentries":[{"nsfw":false,"spoiler":false,"videos":[{"audio":{"link":"https://a.animethemes.moe/Example-OP1.ogg"}}]}]}]}]}
            """;
        var handler = new StubHandler(json, [1, 2, 3]);
        var provider = new AnimeThemesProvider(
            new HttpClient(handler),
            new FakeThemeAudioProcessor((_, _) => Mp3()));

        var result = await provider.FetchAsync(
            new Series { Name = "Example", ProductionYear = 2024 },
            CancellationToken.None);

        Assert.Equal("AnimeThemes OP1", result.Source);
        Assert.EndsWith("Example-OP1.ogg", result.SourceUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NsfwOrSpoilerEd1IsRejected()
    {
        const string json = """
            {"anime":[{"name":"Example","year":2024,"animethemes":[{"type":"ED","sequence":1,"animethemeentries":[{"nsfw":true,"spoiler":false,"videos":[{"audio":{"link":"https://a.animethemes.moe/Unsafe-ED1.ogg"}}]},{"nsfw":false,"spoiler":true,"videos":[{"audio":{"link":"https://a.animethemes.moe/Spoiler-ED1.ogg"}}]}]}]}]}
            """;
        var handler = new StubHandler(json, [1, 2, 3]);
        var processor = new FakeThemeAudioProcessor((_, _) => Mp3());
        var provider = new AnimeThemesProvider(new HttpClient(handler), processor);

        var result = await provider.FetchAsync(
            new Series { Name = "Example", ProductionYear = 2024 },
            CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.NotFound, result.Status);
        Assert.Empty(processor.Requested);
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
    public async Task NullSequenceDefaultsToOp1InsteadOfThrowing()
    {
        const string json = """
            {"anime":[{"name":"Solo Leveling","year":2024,"animethemes":[{"type":"OP","sequence":null,"animethemeentries":[{"nsfw":false,"spoiler":false,"videos":[{"audio":{"link":"https://a.animethemes.moe/SoloLeveling-OP1.ogg"}}]}]}]}]}
            """;
        var handler = new StubHandler(json, [1, 2, 3]);
        var processor = new FakeThemeAudioProcessor((_, _) => Mp3());
        var provider = new AnimeThemesProvider(new HttpClient(handler), processor);

        var result = await provider.FetchAsync(
            new Series { Name = "Solo Leveling", ProductionYear = 2024 },
            CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Found, result.Status);
        Assert.Single(processor.Requested);
    }

    [Fact]
    public async Task NullYearDoesNotMatchOrThrow()
    {
        const string json = """
            {"anime":[{"name":"Solo Leveling","year":null,"animethemes":[{"type":"OP","sequence":1,"animethemeentries":[]}]}]}
            """;
        var handler = new StubHandler(json, [1, 2, 3]);
        var processor = new FakeThemeAudioProcessor((_, _) => Mp3());
        var provider = new AnimeThemesProvider(new HttpClient(handler), processor);

        var result = await provider.FetchAsync(
            new Series { Name = "Solo Leveling", ProductionYear = 2024 },
            CancellationToken.None);

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
    public void CreateClientUsesAnimeThemesCompatibleIdentification()
    {
        using var client = AnimeThemesProvider.CreateClient();

        Assert.Equal("ThemeMusicFinder/1.2", client.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Contains(
            client.DefaultRequestHeaders.Accept,
            value => value.MediaType == "application/json");
    }

    [Fact]
    public async Task ProviderWideHttpFailureTripsCircuitBreakerForTheRun()
    {
        var handler = new StatusHandler(HttpStatusCode.Forbidden);
        var provider = new AnimeThemesProvider(
            new HttpClient(handler),
            new FakeThemeAudioProcessor((_, _) => Mp3()));

        var first = await provider.FetchAsync(
            new Series { Name = "First" }, CancellationToken.None);
        var second = await provider.FetchAsync(
            new Series { Name = "Second" }, CancellationToken.None);

        Assert.Equal(ThemeFetchStatus.Transient, first.Status);
        Assert.Equal(ThemeFetchStatus.Transient, second.Status);
        Assert.Equal("AnimeThemes returned HTTP 403", second.Reason);
        Assert.Equal(1, handler.Calls);
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
