using SteamRip.Core.Catalog;
using SteamRip.Core.Data;
using SteamRip.Core.Updates;

namespace SteamRip.Core.Tests;

public class CatalogTests
{
    [Theory]
    [InlineData("Mortal Shell II Free Download (v98314+1943)", "Mortal Shell II", "v98314+1943")]
    [InlineData("Hull Rupture Free Download", "Hull Rupture", null)]
    [InlineData("Victoria 3 Free Download (v1.15.0)", "Victoria 3", "v1.15.0")]
    [InlineData("Some Game Free Download (v1.2 + ALL DLCs)", "Some Game", "v1.2 + ALL DLCs")]
    [InlineData("R&#8217;s Quest Free Download (v2)", "R’s Quest", "v2")]
    public void TitleParser_splits_name_and_version(string title, string name, string? version)
    {
        var (n, v) = TitleParser.Parse(title);
        Assert.Equal(name, n);
        Assert.Equal(version, v);
    }

    [Theory]
    [InlineData("12.3 GB", 12_300_000_000L)]
    [InlineData("750 MB", 750_000_000L)]
    [InlineData("1,5 TB", 1_500_000_000_000L)]
    [InlineData("900 KB", 900_000L)]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void ParseSize_turns_the_game_size_line_into_bytes(string? text, long? bytes) =>
        Assert.Equal(bytes, CatalogParser.ParseSize(text));

    [Fact]
    public void Posts_carry_category_ids_and_categories_fixture_parses()
    {
        var posts = CatalogParser.Parse(Fixture.Read("posts_page1.json"));
        Assert.Equal([26, 27, 32, 45, 63], posts[0].CategoryIds);
        Assert.All(posts, p => Assert.Equal(CatalogParser.ParseSize(p.SizeText), p.SizeBytes));

        var cats = CatalogParser.ParseCategories(Fixture.Read("categories.json"));
        Assert.Equal(20, cats.Count);
        Assert.Contains(cats, c => c is { Id: 26, Name: "Action", Count: > 2000 });
    }

    [Fact]
    public void Top_games_page_yields_post_ids_in_rank_order()
    {
        var ids = CatalogParser.ParseTopGames(Fixture.Read("top_games_page1.html"));
        Assert.Equal(24, ids.Count);
        Assert.Equal([40820L, 1050L, 12806L, 2340L], ids.Take(4));
        Assert.Empty(CatalogParser.ParseTopGames("<html><body>nothing</body></html>"));
    }

    [Fact]
    public void Search_sorts_in_sql_and_filters_by_category()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "sr-test-" + Guid.NewGuid().ToString("N") + ".db"
        );
        try
        {
            using var db = new Database(path);
            var posts = CatalogParser.Parse(Fixture.Read("posts_page1.json"));
            db.UpsertPosts(posts);
            db.UpsertCategories(CatalogParser.ParseCategories(Fixture.Read("categories.json")));

            var bySize = db.SearchPosts(null, PostSort.SizeDesc, limit: 5);
            Assert.Equal(5, bySize.Count);
            Assert.Equal(posts.Max(p => p.SizeBytes), bySize[0].SizeBytes);
            Assert.True(
                bySize
                    .Select(p => p.SizeBytes!.Value)
                    .SequenceEqual(bySize.Select(p => p.SizeBytes!.Value).OrderDescending())
            );

            var byName = db.SearchPosts(null, PostSort.Name, limit: 100)
                .Select(p => p.Name)
                .ToList();
            Assert.Equal(byName.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), byName);

            db.SetPopularity([posts[5].Id, posts[2].Id]);
            var popular = db.SearchPosts(null, PostSort.Popular, limit: 3);
            Assert.Equal([posts[5].Id, posts[2].Id], popular.Take(2).Select(p => p.Id));
            Assert.Equal(2, db.RankedPostCount());
            db.SetPopularity([posts[7].Id]);
            Assert.Equal(posts[7].Id, db.SearchPosts(null, PostSort.Popular, limit: 1).Single().Id);
            Assert.Equal(1, db.RankedPostCount());

            var added = db.SearchPosts(null, PostSort.Added, limit: 1).Single();
            Assert.Equal(posts.Max(p => p.PublishedUtc), added.PublishedUtc);

            var horror = db.SearchPosts(null, categoryId: 32);
            Assert.Equal(posts.Count(p => p.CategoryIds.Contains(32)), horror.Count);
            Assert.All(
                horror,
                p => Assert.Contains(32, posts.Single(x => x.Id == p.Id).CategoryIds)
            );
            Assert.Empty(db.SearchPosts(null, categoryId: 999999));
            Assert.True(db.HasPostCategories());
            Assert.Equal("Action", db.GetCategories()[0].Name);

            // A re-upsert of a post without category ids keeps what is there.
            db.UpsertPosts([posts[0] with { CategoryIds = [] }]);
            Assert.Contains(db.SearchPosts(null, categoryId: 32), p => p.Id == posts[0].Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parses_rest_page_fixture()
    {
        var posts = CatalogParser.Parse(Fixture.Read("posts_page1.json"));
        Assert.Equal(40, posts.Count);

        var mortal = posts.Single(p => p.Id == 57136);
        Assert.Equal("Mortal Shell II", mortal.Name);
        Assert.Equal("v98314+1943", mortal.TitleVersion);
        Assert.Equal("Build 26315250", mortal.Build);
        Assert.Equal("Build 26315250", mortal.VersionKey);
        Assert.Equal(2584270, mortal.SteamAppId);
        Assert.Equal("66.1 GB", mortal.SizeText);
        Assert.Equal(new DateTime(2026, 11, 6, 15, 29, 26, DateTimeKind.Utc), mortal.ModifiedUtc);
        Assert.Contains(
            mortal.Mirrors,
            m => m.Host == Hoster.FileDitch && m.Url.Contains("Mortal-Shell2")
        );

        Assert.All(posts, p => Assert.NotEmpty(p.Mirrors));
        Assert.True(
            posts.Count(p => p.Mirrors.Any(m => m.Host != Hoster.Unknown)) >= 38,
            "almost every post should have a recognised hoster"
        );
    }

    [Theory]
    [InlineData("https://bzzhr.to/abc", Hoster.BuzzHeavier)]
    [InlineData("https://buzzheavier.com/abc", Hoster.BuzzHeavier)]
    [InlineData("//itsfileditchfiles.st/x/y.rar", Hoster.FileDitch)]
    [InlineData("https://fileditchfiles.me/x/y.rar", Hoster.FileDitch)]
    [InlineData("https://gofile.io/d/QavuCp", Hoster.GoFile)]
    [InlineData("https://megadb.net/235fbe4f4789793f", Hoster.MegaDb)]
    [InlineData("https://1fichier.com/?abc", Hoster.Fichier)]
    [InlineData("https://example.com/x", Hoster.Unknown)]
    public void Detects_hosters(string url, Hoster expected) =>
        Assert.Equal(expected, HosterDetector.Detect(url));

    [Fact]
    public void Steam_app_id_name_match_rejects_mafia_for_lego_batman()
    {
        Assert.False(
            SteamAppIdResolver.NamesMatch(
                "LEGO Batman: Legacy of the Dark Knight",
                "Mafia: The Old Country"
            )
        );
        Assert.True(
            SteamAppIdResolver.NamesMatch(
                "LEGO Batman: Legacy of the Dark Knight",
                "LEGO® Batman™: Legacy of the Dark Knight"
            )
        );
        Assert.Equal(
            2215200,
            SteamAppIdResolver.PickSearchMatch(
                "LEGO Batman: Legacy of the Dark Knight",
                [
                    (1941540, "Mafia: The Old Country"),
                    (2215200, "LEGO® Batman™: Legacy of the Dark Knight"),
                    (4468750, "LEGO® Batman™: Legacy of the Dark Knight Deluxe Edition Upgrade"),
                ]
            )
        );
    }

    [Fact]
    public void Steam_app_id_title_prefix_strips_subtitle()
    {
        Assert.Equal(
            "LEGO Batman",
            SteamAppIdResolver.TitlePrefix("LEGO Batman: Legacy of the Dark Knight")
        );
        Assert.Equal("Cool Game", SteamAppIdResolver.TitlePrefix("Cool Game - Ultimate Edition"));
        Assert.Null(SteamAppIdResolver.TitlePrefix("NoSubtitle"));
        Assert.Equal(
            ["LEGO Batman: Legacy of the Dark Knight", "LEGO Batman"],
            SteamAppIdResolver.SearchTerms("LEGO Batman: Legacy of the Dark Knight").ToArray()
        );
    }

    [Fact]
    public void SteamGridDb_picks_highest_score_grid_url()
    {
        var url = SteamGridDb.BestGridUrl(Fixture.Read("steamgriddb_grids.json"));
        Assert.Equal("https://cdn2.steamgriddb.com/grid/best_grid.png", url);
    }

    [Fact]
    public void SteamGridDb_prefers_png_icon_when_scores_tie()
    {
        var url = SteamGridDb.BestIconUrl(Fixture.Read("steamgriddb_icons.json"));
        Assert.Equal("https://cdn2.steamgriddb.com/icon/png_icon.png", url);
        Assert.Equal(".png", SteamGridDb.ExtensionFor(url!, ".jpg"));
        Assert.Equal(".jpg", SteamGridDb.ExtensionFor("https://cdn.example/x.jpeg", ".png"));
    }

    [Fact]
    public async Task Steam_app_id_positive_disk_cache_short_circuits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sr-steam-apps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "id-1941540.json"), "{\"id\":2215200}");
            // HttpClient is unused when a positive cache hit exists (no network in unit tests).
            var resolver = new SteamAppIdResolver(new HttpClient(), dir);
            Assert.Equal(
                2215200,
                await resolver.ResolveAsync(1941540, "LEGO Batman: Legacy of the Dark Knight")
            );
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // temp cleanup best-effort
            }
        }
    }

    [Fact]
    public void ResolvePath_uses_library_root_when_install_path_empty()
    {
        var g = new InstalledGame(
            1,
            "cool-game",
            "Cool",
            "1",
            "",
            DateTime.UtcNow,
            true,
            InstallState.Installed,
            null
        );
        Assert.Equal(
            Path.Combine("/mnt/storage2/SteamRIP", "cool-game"),
            g.ResolvePath("/mnt/storage2/SteamRIP")
        );
        Assert.Equal(
            "/elsewhere/Cool",
            (g with { InstallPath = "/elsewhere/Cool" }).ResolvePath("/mnt/storage2/SteamRIP")
        );
        Assert.Equal(
            Path.Combine("/lib", "cool-game"),
            (g with { InstallPath = "   " }).ResolvePath("/lib")
        );
    }

    [Fact]
    public void Update_detection_uses_version_key_not_modified_date()
    {
        var posts = CatalogParser.Parse(Fixture.Read("posts_page1.json"));
        var post = posts.Single(p => p.Id == 57136);
        var installed = new InstalledGame(
            post.Id,
            post.Slug,
            post.Name,
            "26315250",
            "/x",
            DateTime.UtcNow,
            true,
            InstallState.Installed,
            null
        );
        Assert.False(UpdateDetector.IsUpdate(installed, post)); // same build (normalised "Build " prefix)

        var newer = post with { Build = "Build 26400000" };
        Assert.True(UpdateDetector.IsUpdate(installed, newer));

        var noBuild = post with { Build = null, TitleVersion = null };
        Assert.False(UpdateDetector.IsUpdate(installed, noBuild)); // unknown → don't churn

        var titleOnly = post with { Build = null, TitleVersion = "v2.0" };
        Assert.True(
            UpdateDetector.IsUpdate(installed with { InstalledVersion = "v1.0" }, titleOnly)
        );
        Assert.False(
            UpdateDetector.IsUpdate(installed with { InstalledVersion = "v2.0" }, titleOnly)
        );
    }

    [Fact]
    public void Database_roundtrips_posts_and_installs()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "sr-test-" + Guid.NewGuid().ToString("N") + ".db"
        );
        try
        {
            using var db = new Database(path);
            var posts = CatalogParser.Parse(Fixture.Read("posts_page1.json"));
            db.UpsertPosts(posts);
            Assert.Equal(40, db.PostCount());
            var back = db.GetPost(57136)!;
            Assert.Equal("Mortal Shell II", back.Name);
            Assert.Equal(posts.Single(p => p.Id == 57136).Mirrors.Count, back.Mirrors.Count);
            Assert.Equal(posts.Max(p => p.ModifiedUtc), db.NewestModified());
            Assert.Contains(db.SearchPosts("mortal"), p => p.Id == 57136);

            // Upsert again with a changed build: id stable, values replaced.
            db.UpsertPosts([back with { Build = "Build 1" }]);
            Assert.Equal("Build 1", db.GetPost(57136)!.Build);
            Assert.Equal(40, db.PostCount());

            db.UpsertInstall(
                new InstalledGame(
                    57136,
                    back.Slug,
                    back.Name,
                    "26315250",
                    "/games/x",
                    DateTime.UtcNow,
                    true,
                    InstallState.Installed,
                    null
                )
            );
            Assert.Single(db.GetInstalls());
            db.SetInstallState(57136, InstallState.Failed, "boom");
            Assert.Equal("boom", db.GetInstall(57136)!.LastError);
            db.SetKv("k", "v");
            Assert.Equal("v", db.GetKv("k"));
            db.LogEvent("test", "hello");
            Assert.Contains(db.RecentEvents(), e => e.Message == "hello");
        }
        finally
        {
            foreach (
                var f in Directory.GetFiles(
                    Path.GetDirectoryName(path)!,
                    Path.GetFileName(path) + "*"
                )
            )
                File.Delete(f);
        }
    }
}
