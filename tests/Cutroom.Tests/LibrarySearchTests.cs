using Cutroom.Library;
using Microsoft.Data.Sqlite;

namespace Cutroom.Tests;

public sealed class LibrarySearchTests
{
    [Fact]
    public async Task FootageUsesPartialMatchesOnlyWhenExactSearchIsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory("cutroom-search-test");
        string database = Path.Combine(dir.FullName, "index.db");
        try
        {
            using var library = new LibraryIndex(database);

            async Task<long> Add(string name, LibraryRole role = LibraryRole.Footage)
            {
                string path = Path.Combine(dir.FullName, name + ".png");
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="));
                return (await library.AddOrUpdate(path, role))!.Value;
            }

            long one = await Add("tracer");
            long two = await Add("tracer blink");
            long asset = await Add("tracer blink pulse asset", LibraryRole.Asset);
            long offTag = await Add("tracer pulse");
            long offFavorite = await Add("pulse");
            long offRecent = await Add("blink");
            foreach (long id in new[] { one, two, asset, offFavorite, offRecent }) library.Tag(id, "ilios");
            foreach (long id in new[] { one, two, asset, offTag, offRecent }) library.SetFavorite(id, true);
            foreach (long id in new[] { one, two, asset, offTag, offFavorite }) library.MarkUsed(library.Get(id)!.Path);

            var exact = library.Search("tracer blink #ilios", out bool partial, LibraryRole.Footage);
            Assert.False(partial);
            Assert.Equal(two, Assert.Single(exact).Item.Id);

            var fallback = library.Search("tracer blink pulse tracer #ilios", out partial,
                LibraryRole.Footage, AssetCategory.Image, favoritesOnly: true, recentOnly: true);
            Assert.True(partial);
            Assert.Equal(new[] { two, one }, fallback.Select(x => x.Item.Id));

            Assert.Empty(library.Search("tracer blink pulse #ilios", out partial, LibraryRole.Footage, AssetCategory.Video));
            Assert.False(partial);
            Assert.Empty(library.Search("tracer blink #unknown", out partial, LibraryRole.Footage));
            Assert.False(partial);
            Assert.Empty(library.Search("unrelated absent", out partial, LibraryRole.Footage));
            Assert.False(partial);
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={database}");
            SqliteConnection.ClearPool(connection);
            dir.Delete(recursive: true);
        }
    }
}
