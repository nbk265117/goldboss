using DofusCompanion.Application;
using DofusCompanion.Application.Mechanics;
using DofusCompanion.Infrastructure.Knowledge;
using DofusCompanion.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DofusCompanion.Infrastructure.Tests;

/// <summary>A throw-away database in a temp folder with the real services wired together.</summary>
public sealed class TestHost : IDisposable
{
    public TestHost(bool initialize = true)
    {
        Directory = Path.Combine(Path.GetTempPath(), "dofus-companion-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        DatabasePath = Path.Combine(Directory, "knowledge.db");
        Repository = new SqliteKnowledgeRepository(DatabasePath, NullLogger<SqliteKnowledgeRepository>.Instance);
        if (initialize) Repository.Initialize();
        Knowledge = new KnowledgeService(Repository, new MechanicsEngine(), NullLogger<KnowledgeService>.Instance);
        Serializer = new KnowledgePackageSerializer();
        Import = new KnowledgeImportService(Repository, Knowledge, Serializer, NullLogger<KnowledgeImportService>.Instance);
    }

    public string Directory { get; }
    public string DatabasePath { get; }
    public SqliteKnowledgeRepository Repository { get; }
    public KnowledgeService Knowledge { get; }
    public KnowledgePackageSerializer Serializer { get; }
    public KnowledgeImportService Import { get; }

    public string WriteFile(string name, string content)
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        Knowledge.Dispose();
        SqliteKnowledgeRepository.ReleaseFileHandles();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
