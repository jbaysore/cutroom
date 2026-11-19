using Cutroom.Core;

namespace Cutroom.Tests;

public sealed class ProjectCatalogTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("cutroom-catalog").FullName;

    [Fact]
    public void DuplicateNamesHaveSeparateFilesAndRenameKeepsTheOriginalPath()
    {
        var settings = new AppSettings { ProjectsFolder = folder };
        var catalog = new ProjectCatalog(settings);
        var first = catalog.Create("Same name");
        var second = catalog.Create("Same name");
        Assert.NotEqual(first.Path, second.Path);

        var recording = Path.Combine(Path.GetDirectoryName(first.Path!)!, "narration", "take.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(recording)!);
        File.WriteAllText(recording, "sample");
        first.Edit("Recording", p => p.Media.Add(new MediaRef { Path = recording }));
        var path = first.Path!;
        first.Rename("New title");

        Assert.Equal(path, first.Path);
        Assert.False(first.Dirty);
        var reopened = Document.Open(path);
        Assert.Equal("New title", reopened.Project.Name);
        Assert.Equal(recording, Assert.Single(reopened.Project.Media).Path);
        Assert.True(File.Exists(recording));
        Assert.Equal("Same name", Document.Open(second.Path!).Project.Name);
        reopened.Rename("Renamed while closed");
        Assert.Equal("Renamed while closed", Document.Open(path).Project.Name);
    }

    [Fact]
    public void CatalogIncludesLegacyAndExternalProjectsAndKeepsReadableEntriesBesideBadFiles()
    {
        var settings = new AppSettings { ProjectsFolder = Path.Combine(folder, "managed") };
        var catalog = new ProjectCatalog(settings);
        var managed = catalog.Create("Example");
        var legacy = Path.Combine(settings.ProjectsFolder, "legacy.cutroom.json");
        var broken = Path.Combine(settings.ProjectsFolder, "broken.cutroom.json");
        var external = Path.Combine(folder, "external.cutroom.json");
        File.WriteAllText(legacy, """{"Name":"Legacy"}""");
        File.WriteAllText(broken, "{");
        File.WriteAllText(external, """{"Name":"External"}""");
        settings.RecentProjects = [managed.Path!, external, external, external + ".bak1"];
        var settingsPath = Path.Combine(folder, "settings.json");
        settings.Save(settingsPath);
        var list = new ProjectCatalog(AppSettings.Load(settingsPath)).Read(out _);

        Assert.Single(list, e => e.Path == managed.Path && e.Name == "Example");
        Assert.Single(list, e => e.Path == external && e.Name == "External");
        Assert.Single(list, e => e.Path == legacy && e.Name == "Legacy");
        Assert.Single(list, e => e.Path == broken && e.Error is not null);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Document.Open(broken));
    }

    [Fact]
    public void FailedRenameKeepsCurrentNameAndUnsavedEdits()
    {
        var catalog = new ProjectCatalog(new AppSettings { ProjectsFolder = folder });
        var document = catalog.Create("Original");
        document.Edit("Recording", p => p.Media.Add(new MediaRef { Path = "take.wav" }));
        var saveBlocker = document.Path + ".partial";
        Directory.CreateDirectory(saveBlocker);

        var error = Record.Exception(() => document.Rename("Updated"));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal("Original", document.Project.Name);
        Assert.Equal("Original", Document.Open(document.Path!).Project.Name);
        Assert.True(document.Dirty);
        Assert.Equal("Recording", document.UndoLabel);
        Assert.Single(document.Project.Media);
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
