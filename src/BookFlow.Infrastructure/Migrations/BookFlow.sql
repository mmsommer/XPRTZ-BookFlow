IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Idempotency] (
        [Key] nvarchar(300) NOT NULL,
        [RequestHash] nvarchar(max) NOT NULL,
        [ResourceId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Idempotency] PRIMARY KEY ([Key])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Inbox] (
        [Consumer] nvarchar(64) NOT NULL,
        [EventId] nvarchar(128) NOT NULL,
        [ProcessedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Inbox] PRIMARY KEY ([Consumer], [EventId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [LocalDeliveries] (
        [Consumer] nvarchar(64) NOT NULL,
        [EventId] nvarchar(128) NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [Attempts] int NOT NULL,
        [Completed] bit NOT NULL,
        [DeadLettered] bit NOT NULL,
        [FailureCode] nvarchar(max) NULL,
        [NextAttemptAt] datetimeoffset NULL,
        CONSTRAINT [PK_LocalDeliveries] PRIMARY KEY ([Consumer], [EventId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [NotificationDeliveries] (
        [Key] nvarchar(300) NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [Sent] bit NOT NULL,
        [Attempts] int NOT NULL,
        [DeadLettered] bit NOT NULL,
        [FailureCode] nvarchar(max) NULL,
        [NextAttemptAt] datetimeoffset NULL,
        CONSTRAINT [PK_NotificationDeliveries] PRIMARY KEY ([Key])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Outbox] (
        [Id] nvarchar(128) NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [Published] bit NOT NULL,
        [Attempts] int NOT NULL,
        [NextAttemptAt] datetimeoffset NULL,
        [FailureCode] nvarchar(max) NULL,
        [DeadLettered] bit NOT NULL,
        CONSTRAINT [PK_Outbox] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Participants] (
        [Id] nvarchar(100) NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [Name] nvarchar(max) NOT NULL,
        [Email] nvarchar(max) NOT NULL,
        [Role] nvarchar(32) NOT NULL,
        [Active] bit NOT NULL,
        CONSTRAINT [PK_Participants] PRIMARY KEY ([TenantId], [Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Uploads] (
        [Id] uniqueidentifier NOT NULL,
        [BlobName] nvarchar(max) NOT NULL,
        [State] nvarchar(max) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Uploads] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Manuscripts] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [AuthorId] nvarchar(100) NOT NULL,
        [PublisherId] nvarchar(100) NOT NULL,
        [EditorId] nvarchar(100) NULL,
        [Title] nvarchar(max) NOT NULL,
        [Status] int NOT NULL,
        [ProgressStatus] int NOT NULL,
        [Sequence] bigint NOT NULL,
        [ProgressSequence] bigint NOT NULL,
        [SubmittedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Manuscripts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Manuscripts_Participants_TenantId_AuthorId] FOREIGN KEY ([TenantId], [AuthorId]) REFERENCES [Participants] ([TenantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Manuscripts_Participants_TenantId_EditorId] FOREIGN KEY ([TenantId], [EditorId]) REFERENCES [Participants] ([TenantId], [Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Manuscripts_Participants_TenantId_PublisherId] FOREIGN KEY ([TenantId], [PublisherId]) REFERENCES [Participants] ([TenantId], [Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Books] (
        [Id] uniqueidentifier NOT NULL,
        [ManuscriptId] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [PublisherId] nvarchar(max) NOT NULL,
        [Title] nvarchar(max) NOT NULL,
        [Isbn] nvarchar(17) NOT NULL,
        [Orderable] bit NOT NULL,
        CONSTRAINT [PK_Books] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Books_Manuscripts_ManuscriptId] FOREIGN KEY ([ManuscriptId]) REFERENCES [Manuscripts] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [History] (
        [EventId] nvarchar(128) NOT NULL,
        [ManuscriptId] uniqueidentifier NOT NULL,
        [Sequence] bigint NOT NULL,
        [Status] int NOT NULL,
        [ChangedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_History] PRIMARY KEY ([EventId]),
        CONSTRAINT [FK_History_Manuscripts_ManuscriptId] FOREIGN KEY ([ManuscriptId]) REFERENCES [Manuscripts] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Metadata] (
        [ManuscriptId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_Metadata] PRIMARY KEY ([ManuscriptId]),
        CONSTRAINT [FK_Metadata_Manuscripts_ManuscriptId] FOREIGN KEY ([ManuscriptId]) REFERENCES [Manuscripts] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Versions] (
        [Id] uniqueidentifier NOT NULL,
        [ManuscriptId] uniqueidentifier NOT NULL,
        [Number] int NOT NULL,
        [ParentId] uniqueidentifier NULL,
        [BlobName] nvarchar(max) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Versions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Versions_Manuscripts_ManuscriptId] FOREIGN KEY ([ManuscriptId]) REFERENCES [Manuscripts] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Versions_Versions_ParentId] FOREIGN KEY ([ParentId]) REFERENCES [Versions] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Ledger] (
        [EventId] nvarchar(128) NOT NULL,
        [BookId] uniqueidentifier NOT NULL,
        [DistributorId] nvarchar(100) NOT NULL,
        [EventType] nvarchar(max) NOT NULL,
        [Sequence] bigint NOT NULL,
        [Quantity] int NOT NULL,
        [RecordedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Ledger] PRIMARY KEY ([EventId]),
        CONSTRAINT [FK_Ledger_Books_BookId] FOREIGN KEY ([BookId]) REFERENCES [Books] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [PrintOrders] (
        [Id] uniqueidentifier NOT NULL,
        [BookId] uniqueidentifier NOT NULL,
        [DistributorId] nvarchar(max) NOT NULL,
        [Quantity] int NOT NULL,
        [Status] int NOT NULL,
        [ExternalReference] nvarchar(max) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_PrintOrders] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PrintOrders_Books_BookId] FOREIGN KEY ([BookId]) REFERENCES [Books] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Stock] (
        [BookId] uniqueidentifier NOT NULL,
        [DistributorId] nvarchar(100) NOT NULL,
        [AvailableQuantity] int NOT NULL,
        [PrintedQuantity] int NOT NULL,
        [DeliveredQuantity] int NOT NULL,
        [DistributedQuantity] int NOT NULL,
        [SoldQuantity] int NOT NULL,
        [Sequence] bigint NOT NULL,
        [Alert] nvarchar(max) NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Stock] PRIMARY KEY ([BookId], [DistributorId]),
        CONSTRAINT [FK_Stock_Books_BookId] FOREIGN KEY ([BookId]) REFERENCES [Books] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Feedback] (
        [Id] uniqueidentifier NOT NULL,
        [VersionId] uniqueidentifier NOT NULL,
        [AuthorId] nvarchar(max) NOT NULL,
        [Comment] nvarchar(max) NOT NULL,
        [SubmittedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Feedback] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Feedback_Versions_VersionId] FOREIGN KEY ([VersionId]) REFERENCES [Versions] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Highlights] (
        [Id] uniqueidentifier NOT NULL,
        [VersionId] uniqueidentifier NOT NULL,
        [StartOffset] int NOT NULL,
        [EndOffset] int NOT NULL,
        [OriginalText] nvarchar(max) NOT NULL,
        [EditedText] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_Highlights] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Highlights_Versions_VersionId] FOREIGN KEY ([VersionId]) REFERENCES [Versions] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE TABLE [Proofprints] (
        [Id] uniqueidentifier NOT NULL,
        [VersionId] uniqueidentifier NOT NULL,
        [BlobName] nvarchar(max) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Proofprints] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Proofprints_Versions_VersionId] FOREIGN KEY ([VersionId]) REFERENCES [Versions] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Books_Isbn] ON [Books] ([Isbn]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Books_ManuscriptId] ON [Books] ([ManuscriptId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE INDEX [IX_Feedback_VersionId] ON [Feedback] ([VersionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE INDEX [IX_Highlights_VersionId] ON [Highlights] ([VersionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE UNIQUE INDEX [IX_History_ManuscriptId_Sequence] ON [History] ([ManuscriptId], [Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Ledger_BookId_DistributorId_Sequence] ON [Ledger] ([BookId], [DistributorId], [Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE INDEX [IX_Manuscripts_TenantId_AuthorId] ON [Manuscripts] ([TenantId], [AuthorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE INDEX [IX_Manuscripts_TenantId_EditorId] ON [Manuscripts] ([TenantId], [EditorId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE INDEX [IX_Manuscripts_TenantId_PublisherId] ON [Manuscripts] ([TenantId], [PublisherId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE INDEX [IX_PrintOrders_BookId] ON [PrintOrders] ([BookId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Proofprints_VersionId] ON [Proofprints] ([VersionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Versions_ManuscriptId_Number] ON [Versions] ([ManuscriptId], [Number]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    CREATE INDEX [IX_Versions_ParentId] ON [Versions] ([ParentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031140429_InitialBookFlow'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261031140429_InitialBookFlow', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031141341_AddOutboxLeases'
)
BEGIN
    ALTER TABLE [Outbox] ADD [LeaseId] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031141341_AddOutboxLeases'
)
BEGIN
    ALTER TABLE [Outbox] ADD [LeaseUntilUtcTicks] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031141341_AddOutboxLeases'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261031141341_AddOutboxLeases', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031142606_EnforcePublisherAndOutboxConstraints'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Outbox]') AND [c].[name] = N'LeaseId');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Outbox] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [Outbox] ALTER COLUMN [LeaseId] nvarchar(36) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031142606_EnforcePublisherAndOutboxConstraints'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Participants_TenantId_Role] ON [Participants] ([TenantId], [Role]) WHERE [Role] = ''Publisher''');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031142606_EnforcePublisherAndOutboxConstraints'
)
BEGIN
    CREATE INDEX [IX_Outbox_Published_DeadLettered_LeaseUntilUtcTicks] ON [Outbox] ([Published], [DeadLettered], [LeaseUntilUtcTicks]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031142606_EnforcePublisherAndOutboxConstraints'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261031142606_EnforcePublisherAndOutboxConstraints', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031162638_AddQueueRetryTicks'
)
BEGIN
    DROP INDEX [IX_Outbox_Published_DeadLettered_LeaseUntilUtcTicks] ON [Outbox];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031162638_AddQueueRetryTicks'
)
BEGIN
    ALTER TABLE [Outbox] ADD [NextAttemptAtUtcTicks] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031162638_AddQueueRetryTicks'
)
BEGIN
    ALTER TABLE [NotificationDeliveries] ADD [NextAttemptAtUtcTicks] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031162638_AddQueueRetryTicks'
)
BEGIN
    ALTER TABLE [LocalDeliveries] ADD [NextAttemptAtUtcTicks] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031162638_AddQueueRetryTicks'
)
BEGIN
    UPDATE [Outbox]
                      SET [NextAttemptAtUtcTicks] =
                          DATEDIFF_BIG(MILLISECOND, CONVERT(datetime2, '0001-01-01T00:00:00'),
                              SWITCHOFFSET([NextAttemptAt], '+00:00')) * 10000
                          + (DATEPART(NANOSECOND, SWITCHOFFSET([NextAttemptAt], '+00:00')) / 100) % 10000
                      WHERE [NextAttemptAt] IS NOT NULL;

                      UPDATE [LocalDeliveries]
                      SET [NextAttemptAtUtcTicks] =
                          DATEDIFF_BIG(MILLISECOND, CONVERT(datetime2, '0001-01-01T00:00:00'),
                              SWITCHOFFSET([NextAttemptAt], '+00:00')) * 10000
                          + (DATEPART(NANOSECOND, SWITCHOFFSET([NextAttemptAt], '+00:00')) / 100) % 10000
                      WHERE [NextAttemptAt] IS NOT NULL;

                      UPDATE [NotificationDeliveries]
                      SET [NextAttemptAtUtcTicks] =
                          DATEDIFF_BIG(MILLISECOND, CONVERT(datetime2, '0001-01-01T00:00:00'),
                              SWITCHOFFSET([NextAttemptAt], '+00:00')) * 10000
                          + (DATEPART(NANOSECOND, SWITCHOFFSET([NextAttemptAt], '+00:00')) / 100) % 10000
                      WHERE [NextAttemptAt] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031162638_AddQueueRetryTicks'
)
BEGIN
    CREATE INDEX [IX_Outbox_Published_DeadLettered_LeaseUntilUtcTicks_NextAttemptAtUtcTicks_Id] ON [Outbox] ([Published], [DeadLettered], [LeaseUntilUtcTicks], [NextAttemptAtUtcTicks], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031162638_AddQueueRetryTicks'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_Sent_DeadLettered_NextAttemptAtUtcTicks_Key] ON [NotificationDeliveries] ([Sent], [DeadLettered], [NextAttemptAtUtcTicks], [Key]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031162638_AddQueueRetryTicks'
)
BEGIN
    CREATE INDEX [IX_LocalDeliveries_Consumer_Completed_DeadLettered_NextAttemptAtUtcTicks_EventId] ON [LocalDeliveries] ([Consumer], [Completed], [DeadLettered], [NextAttemptAtUtcTicks], [EventId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031162638_AddQueueRetryTicks'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261031162638_AddQueueRetryTicks', N'10.0.14');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164405_LeaseNotificationDelivery'
)
BEGIN
    DROP INDEX [IX_NotificationDeliveries_Sent_DeadLettered_NextAttemptAtUtcTicks_Key] ON [NotificationDeliveries];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164405_LeaseNotificationDelivery'
)
BEGIN
    ALTER TABLE [NotificationDeliveries] ADD [LeaseId] nvarchar(36) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164405_LeaseNotificationDelivery'
)
BEGIN
    ALTER TABLE [NotificationDeliveries] ADD [LeaseUntilUtcTicks] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164405_LeaseNotificationDelivery'
)
BEGIN
    CREATE INDEX [IX_NotificationDeliveries_Sent_DeadLettered_LeaseUntilUtcTicks_NextAttemptAtUtcTicks_Key] ON [NotificationDeliveries] ([Sent], [DeadLettered], [LeaseUntilUtcTicks], [NextAttemptAtUtcTicks], [Key]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261031164405_LeaseNotificationDelivery'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261031164405_LeaseNotificationDelivery', N'10.0.14');
END;

COMMIT;
GO

