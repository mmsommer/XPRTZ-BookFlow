using BookFlow.Core;
using BookFlow.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BookFlow.Testing;

public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    public BookFlowDbContext Db { get; }
    public Guid TenantId { get; } = Guid.NewGuid();
    public Actor Author => new("author", TenantId, "Author");
    public Actor Editor => new("editor", TenantId, "Editor");
    public Actor Publisher => new("publisher", TenantId, "Publisher");
    public Actor Distributor => new("distributor", TenantId, "Distributor");
    public Actor Admin => new("admin", TenantId, "Admin");

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

    public DomainEvent Event(string type, Manuscript manuscript, long? sequence = null) =>
        DomainEvent.Create(type, new EventData(TenantId, sequence ?? manuscript.Sequence, manuscript.Id,
            Db.Versions.Single(x => x.ManuscriptId == manuscript.Id).Id));

    public DomainEvent InventoryEvent(string type, Book book, long sequence, int quantity) =>
        DomainEvent.Create(type, new EventData(TenantId, sequence, BookId: book.Id,
            DistributorId: Distributor.Id, Quantity: quantity));

    public void Dispose()
    {
        Db.Dispose();
        connection.Dispose();
    }
}
