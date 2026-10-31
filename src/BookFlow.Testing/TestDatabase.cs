using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.Testing;

/// <summary>Provides an isolated in-memory SQLite database with registered test participants.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    /// <summary>The test database context.</summary>
    public BookFlowDbContext Db { get; }
    /// <summary>The publisher tenant identifier.</summary>
    public Guid TenantId { get; } = Guid.NewGuid();
    /// <summary>The registered author in the test tenant.</summary>
    public Actor Author => new("author", TenantId, "Author");
    /// <summary>The registered editor in the test tenant.</summary>
    public Actor Editor => new("editor", TenantId, "Editor");
    /// <summary>The registered publisher in the test tenant.</summary>
    public Actor Publisher => new("publisher", TenantId, "Publisher");
    /// <summary>The registered distributor in the test tenant.</summary>
    public Actor Distributor => new("distributor", TenantId, "Distributor");
    /// <summary>An administrator actor in the test tenant.</summary>
    public Actor Admin => new("admin", TenantId, "Admin");

    /// <summary>Creates an isolated SQLite database and registers the standard test participants.</summary>
    public TestDatabase()
    {
        connection.Open();
        Db = new BookFlowDbContext(new DbContextOptionsBuilder<BookFlowDbContext>().UseSqlite(connection).Options);
        Db.Database.EnsureCreated();
        foreach (var actor in new[] { Author, Editor, Publisher, Distributor })
            Db.Participants.Add(new Participant
            {
                Id = actor.Id, TenantId = TenantId, Role = actor.Role, Name = actor.Role,
                Email = $"{actor.Id}@example.test"
            });
        Db.SaveChanges();
    }

    /// <summary>Persists a test manuscript and its initial version with the requested workflow state.</summary>
    /// <param name="status">The initial test manuscript status.</param>
    /// <param name="sequence">The aggregate event sequence number.</param>
    public Manuscript SeedManuscript(ManuscriptStatus status = ManuscriptStatus.Submitted, long sequence = 1)
    {
        var manuscript = new Manuscript
        {
            Id = Guid.NewGuid(), TenantId = TenantId, AuthorId = Author.Id, PublisherId = Publisher.Id,
            EditorId = status == ManuscriptStatus.Submitted ? null : Editor.Id,
            Title = "Test manuscript", Status = status, Sequence = sequence, SubmittedAt = DateTimeOffset.UtcNow
        };
        Db.Manuscripts.Add(manuscript);
        Db.Versions.Add(new ManuscriptVersion
        {
            Id = Guid.NewGuid(), ManuscriptId = manuscript.Id, Number = 1,
            BlobName = "source.bin", CreatedAt = DateTimeOffset.UtcNow
        });
        Db.SaveChanges();
        return manuscript;
    }

    /// <summary>Persists an approved test manuscript and an orderable book.</summary>
    public Book SeedBook()
    {
        var manuscript = SeedManuscript(ManuscriptStatus.Approved, 3);
        var book = new Book
        {
            Id = Guid.NewGuid(), ManuscriptId = manuscript.Id, TenantId = TenantId, PublisherId = Publisher.Id,
            Title = "Test book", Isbn = "9780306406157", Orderable = true
        };
        Db.Books.Add(book);
        Db.SaveChanges();
        return book;
    }

    /// <summary>Creates a tenant-scoped test event for a manuscript and its initial version.</summary>
    /// <param name="type">The domain event type.</param>
    /// <param name="manuscript">The source manuscript.</param>
    /// <param name="sequence">The aggregate event sequence number.</param>
    public DomainEvent Event(string type, Manuscript manuscript, long? sequence = null) =>
        DomainEvent.Create(type, new EventData(TenantId, sequence ?? manuscript.Sequence, manuscript.Id,
            Db.Versions.Single(x => x.ManuscriptId == manuscript.Id).Id));

    /// <summary>Creates a test inventory event for the registered distributor.</summary>
    /// <param name="type">The domain event type.</param>
    /// <param name="book">The published book.</param>
    /// <param name="sequence">The aggregate event sequence number.</param>
    /// <param name="quantity">The number of copies in the inventory event.</param>
    public DomainEvent InventoryEvent(string type, Book book, long sequence, int quantity) =>
        DomainEvent.Create(type, new EventData(TenantId, sequence, BookId: book.Id,
            DistributorId: Distributor.Id, Quantity: quantity));

    /// <summary>Disposes the test context and its in-memory SQLite connection.</summary>
    public void Dispose()
    {
        Db.Dispose();
        connection.Dispose();
    }
}
