using System.Text.Json;
using BookFlow.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BookFlow.Infrastructure;

public sealed class BookFlowDbContext(DbContextOptions<BookFlowDbContext> options) : DbContext(options)
{
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<Manuscript> Manuscripts => Set<Manuscript>();
    public DbSet<ManuscriptVersion> Versions => Set<ManuscriptVersion>();
    public DbSet<ManuscriptMetadata> Metadata => Set<ManuscriptMetadata>();
    public DbSet<ManuscriptHighlight> Highlights => Set<ManuscriptHighlight>();
    public DbSet<AuthorFeedback> Feedback => Set<AuthorFeedback>();
    public DbSet<StatusHistory> History => Set<StatusHistory>();
    public DbSet<Proofprint> Proofprints => Set<Proofprint>();
    public DbSet<Book> Books => Set<Book>();
    public DbSet<PrintOrder> PrintOrders => Set<PrintOrder>();
    public DbSet<StockRecord> Stock => Set<StockRecord>();
    public DbSet<InventoryLedgerEntry> Ledger => Set<InventoryLedgerEntry>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();
    public DbSet<LocalDelivery> LocalDeliveries => Set<LocalDelivery>();
    public DbSet<IdempotencyRecord> Idempotency => Set<IdempotencyRecord>();
    public DbSet<UploadReservation> Uploads => Set<UploadReservation>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    public void Enqueue(DomainEvent message)
    {
        message.Validate();
        Outbox.Add(new OutboxMessage
        {
            Id = message.Id, Payload = JsonSerializer.Serialize(message, EventJson.Options),
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Participant>().HasKey(x => new { x.TenantId, x.Id });
        model.Entity<Participant>().Property(x => x.Id).HasMaxLength(100);
        model.Entity<Participant>().Property(x => x.Role).HasMaxLength(32);
        model.Entity<Participant>().HasIndex(x => new { x.TenantId, x.Role }).IsUnique()
            .HasFilter("[Role] = 'Publisher'");
        model.Entity<Manuscript>().Property(x => x.Sequence).IsConcurrencyToken();
        model.Entity<Manuscript>().HasOne<Participant>().WithMany()
            .HasForeignKey(x => new { x.TenantId, Id = x.AuthorId }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Manuscript>().HasOne<Participant>().WithMany()
            .HasForeignKey(x => new { x.TenantId, Id = x.PublisherId }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Manuscript>().HasOne<Participant>().WithMany()
            .HasForeignKey(x => new { x.TenantId, Id = x.EditorId }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ManuscriptVersion>().HasIndex(x => new { x.ManuscriptId, x.Number }).IsUnique();
        model.Entity<ManuscriptVersion>().HasOne<Manuscript>().WithMany().HasForeignKey(x => x.ManuscriptId)
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<ManuscriptVersion>().HasOne<ManuscriptVersion>().WithMany().HasForeignKey(x => x.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<ManuscriptMetadata>().HasKey(x => x.ManuscriptId);
        model.Entity<ManuscriptMetadata>().HasOne<Manuscript>().WithOne().HasForeignKey<ManuscriptMetadata>(x => x.ManuscriptId);
        model.Entity<ManuscriptHighlight>().HasOne<ManuscriptVersion>().WithMany().HasForeignKey(x => x.VersionId);
        model.Entity<AuthorFeedback>().HasOne<ManuscriptVersion>().WithMany().HasForeignKey(x => x.VersionId);
        model.Entity<StatusHistory>().HasKey(x => x.EventId);
        model.Entity<StatusHistory>().Property(x => x.EventId).HasMaxLength(128);
        model.Entity<StatusHistory>().HasIndex(x => new { x.ManuscriptId, x.Sequence }).IsUnique();
        model.Entity<StatusHistory>().HasOne<Manuscript>().WithMany().HasForeignKey(x => x.ManuscriptId);
        model.Entity<Proofprint>().HasIndex(x => x.VersionId).IsUnique();
        model.Entity<Proofprint>().HasOne<ManuscriptVersion>().WithMany().HasForeignKey(x => x.VersionId);
        model.Entity<Book>().HasIndex(x => x.ManuscriptId).IsUnique();
        model.Entity<Book>().HasIndex(x => x.Isbn).IsUnique();
        model.Entity<Book>().Property(x => x.Isbn).HasMaxLength(17);
        model.Entity<Book>().HasOne<Manuscript>().WithOne().HasForeignKey<Book>(x => x.ManuscriptId)
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<PrintOrder>().HasOne<Book>().WithMany().HasForeignKey(x => x.BookId);
        model.Entity<StockRecord>().HasKey(x => new { x.BookId, x.DistributorId });
        model.Entity<StockRecord>().Property(x => x.DistributorId).HasMaxLength(100);
        model.Entity<StockRecord>().Property(x => x.Sequence).IsConcurrencyToken();
        model.Entity<StockRecord>().HasOne<Book>().WithMany().HasForeignKey(x => x.BookId);
        model.Entity<InventoryLedgerEntry>().HasKey(x => x.EventId);
        model.Entity<InventoryLedgerEntry>().Property(x => x.EventId).HasMaxLength(128);
        model.Entity<InventoryLedgerEntry>().Property(x => x.DistributorId).HasMaxLength(100);
        model.Entity<InventoryLedgerEntry>().HasIndex(x => new { x.BookId, x.DistributorId, x.Sequence }).IsUnique();
        model.Entity<InventoryLedgerEntry>().HasOne<Book>().WithMany().HasForeignKey(x => x.BookId);
        model.Entity<OutboxMessage>().Property(x => x.Id).HasMaxLength(128);
        model.Entity<OutboxMessage>().Property(x => x.LeaseId).HasMaxLength(36).IsConcurrencyToken();
        model.Entity<OutboxMessage>().HasIndex(x => new { x.Published, x.DeadLettered, x.LeaseUntilUtcTicks });
        model.Entity<InboxMessage>().HasKey(x => new { x.Consumer, x.EventId });
        model.Entity<InboxMessage>().Property(x => x.Consumer).HasMaxLength(64);
        model.Entity<InboxMessage>().Property(x => x.EventId).HasMaxLength(128);
        model.Entity<LocalDelivery>().HasKey(x => new { x.Consumer, x.EventId });
        model.Entity<LocalDelivery>().Property(x => x.Consumer).HasMaxLength(64);
        model.Entity<LocalDelivery>().Property(x => x.EventId).HasMaxLength(128);
        model.Entity<IdempotencyRecord>().HasKey(x => x.Key);
        model.Entity<IdempotencyRecord>().Property(x => x.Key).HasMaxLength(300);
        model.Entity<NotificationDelivery>().HasKey(x => x.Key);
        model.Entity<NotificationDelivery>().Property(x => x.Key).HasMaxLength(300);
    }
}

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BookFlowDbContext>
{
    public BookFlowDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BookFlowDbContext>()
            .UseSqlServer("Server=localhost;Database=BookFlow;Integrated Security=true;TrustServerCertificate=true")
            .Options);
}
