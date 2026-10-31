using System.Text.Json;
using BookFlow.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BookFlow.Infrastructure;

/// <summary>Provides persistence for publishing workflows, inventory, and durable event delivery.</summary>
/// <param name="options">The database context options.</param>
public sealed class BookFlowDbContext(DbContextOptions<BookFlowDbContext> options) : DbContext(options)
{
    /// <summary>Provides access to registered participants.</summary>
    public DbSet<Participant> Participants => Set<Participant>();
    /// <summary>Provides access to submitted manuscripts.</summary>
    public DbSet<Manuscript> Manuscripts => Set<Manuscript>();
    /// <summary>Provides access to immutable manuscript versions.</summary>
    public DbSet<ManuscriptVersion> Versions => Set<ManuscriptVersion>();
    /// <summary>Provides access to manuscript metadata records.</summary>
    public DbSet<ManuscriptMetadata> Metadata => Set<ManuscriptMetadata>();
    /// <summary>Provides access to editor highlights on manuscript versions.</summary>
    public DbSet<ManuscriptHighlight> Highlights => Set<ManuscriptHighlight>();
    /// <summary>Provides access to author feedback on manuscript versions.</summary>
    public DbSet<AuthorFeedback> Feedback => Set<AuthorFeedback>();
    /// <summary>Provides access to sequenced workflow history entries.</summary>
    public DbSet<StatusHistory> History => Set<StatusHistory>();
    /// <summary>Provides access to generated proofprint records.</summary>
    public DbSet<Proofprint> Proofprints => Set<Proofprint>();
    /// <summary>Provides access to published books.</summary>
    public DbSet<Book> Books => Set<Book>();
    /// <summary>Provides access to distributor print orders.</summary>
    public DbSet<PrintOrder> PrintOrders => Set<PrintOrder>();
    /// <summary>Provides access to distributor stock projections.</summary>
    public DbSet<StockRecord> Stock => Set<StockRecord>();
    /// <summary>Provides access to inventory ledger events.</summary>
    public DbSet<InventoryLedgerEntry> Ledger => Set<InventoryLedgerEntry>();
    /// <summary>Provides access to committed events awaiting publication.</summary>
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    /// <summary>Provides access to consumer event acknowledgements.</summary>
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();
    /// <summary>Provides access to queued local event deliveries.</summary>
    public DbSet<LocalDelivery> LocalDeliveries => Set<LocalDelivery>();
    /// <summary>Provides access to request deduplication records.</summary>
    public DbSet<IdempotencyRecord> Idempotency => Set<IdempotencyRecord>();
    /// <summary>Provides access to recoverable blob upload reservations.</summary>
    public DbSet<UploadReservation> Uploads => Set<UploadReservation>();
    /// <summary>Provides access to queued notification deliveries.</summary>
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    /// <summary>Adds a serialized domain event to the outbox for the next database save.</summary>
    /// <param name="message">The domain event to process.</param>
    public void Enqueue(DomainEvent message)
    {
        message.Validate();
        Outbox.Add(new OutboxMessage
        {
            Id = message.Id, Payload = JsonSerializer.Serialize(message, EventJson.Options),
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    /// <summary>Configures entity keys, relationships, indexes, and persistence constraints.</summary>
    /// <param name="model">The builder used to configure entity mappings and constraints.</param>
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
        model.Entity<OutboxMessage>().HasIndex(x => new
            { x.Published, x.DeadLettered, x.LeaseUntilUtcTicks, x.NextAttemptAtUtcTicks, x.Id });
        model.Entity<InboxMessage>().HasKey(x => new { x.Consumer, x.EventId });
        model.Entity<InboxMessage>().Property(x => x.Consumer).HasMaxLength(64);
        model.Entity<InboxMessage>().Property(x => x.EventId).HasMaxLength(128);
        model.Entity<LocalDelivery>().HasKey(x => new { x.Consumer, x.EventId });
        model.Entity<LocalDelivery>().Property(x => x.Consumer).HasMaxLength(64);
        model.Entity<LocalDelivery>().Property(x => x.EventId).HasMaxLength(128);
        model.Entity<LocalDelivery>().HasIndex(x => new
            { x.Consumer, x.Completed, x.DeadLettered, x.NextAttemptAtUtcTicks, x.EventId });
        model.Entity<IdempotencyRecord>().HasKey(x => x.Key);
        model.Entity<IdempotencyRecord>().Property(x => x.Key).HasMaxLength(300);
        model.Entity<NotificationDelivery>().HasKey(x => x.Key);
        model.Entity<NotificationDelivery>().Property(x => x.Key).HasMaxLength(300);
        model.Entity<NotificationDelivery>().Property(x => x.LeaseId).HasMaxLength(36).IsConcurrencyToken();
        model.Entity<NotificationDelivery>().HasIndex(x => new
            { x.Sent, x.DeadLettered, x.LeaseUntilUtcTicks, x.NextAttemptAtUtcTicks, x.Key });
    }
}

/// <summary>Creates the SQL Server context used by Entity Framework design-time tools.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BookFlowDbContext>
{
    /// <summary>Creates a SQL Server context for Entity Framework design-time operations.</summary>
    /// <param name="args">The design-time command arguments.</param>
    public BookFlowDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BookFlowDbContext>()
            .UseSqlServer("Server=localhost;Database=BookFlow;Integrated Security=true;TrustServerCertificate=true")
            .Options);
}
