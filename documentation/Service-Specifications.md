# BookFlow service implementation specifications

These specifications translate the workflows in [Readme](./Readme) and the resources in [main.bicep](../infrastructure/main.bicep) into implementation requirements. They describe application services; they do not imply that application projects, API contracts, database migrations, or external integrations already exist.

## 1. Runtime and deployment boundary

- Implement the application as containerized .NET services deployed to the private AKS cluster in the `bookflow` namespace. The Bicep template creates infrastructure only; it does not build or deploy these services.
- Use the `bookflow` Kubernetes service account and the federated workload identity configured by the template. Use Microsoft Entra authentication for Azure dependencies; do not use storage account keys, Service Bus connection strings, or SQL passwords in deployed workloads.
- Use the `manuscripts` and `proofprints` private blob containers, the `BookFlow` SQL database, the `bookflow-events` Service Bus topic, and the subscription names below.
- The template creates an Event Grid custom topic and a separate dead-letter blob container. It does **not** create a Blob Storage event subscription, the API frontend, email integration, PrintOnDemand integration, Kubernetes application resources, or SQL schema/roles. Implementations must provision or configure these separately.
- Publish `ManuscriptUploaded` as a domain event from the application after the upload and its metadata have been persisted. Do not rely on Blob Storage publishing this custom event unless a corresponding storage event subscription is added.
- The notification Event Grid subscription is created only when `notificationWebhookUrl` is configured. Its HTTPS endpoint must implement Event Grid subscription validation.
- The AKS API server and the data services use private networking. Event Grid and the container registry are public in the current template; do not assume that every Azure resource has private-only ingress.

## 2. Domain model and persistence

Implement a relational schema and versioned migrations for the domain represented in the README class diagram:

- **Author, Editor, Publisher, Distributor**: identities and contact/integration details needed by workflows. Authentication and organization membership are managed by the application/API boundary.
- **Manuscript**: author, title, current status, submission time, optional assigned editor, and optional resulting book.
- **ManuscriptVersion**: manuscript, monotonically increasing version number, parent version where applicable, private blob location, and creation time. Never overwrite an existing version.
- **ManuscriptStatusHistory**: prior/current status, source event ID, and change time. A source event ID must be unique for idempotent processing.
- **ManuscriptMetadata, ManuscriptHighlight, AuthorFeedback**: version-linked edits, offsets and text, and author responses.
- **Proofprint**: source version, private blob location, external generation reference where available, and creation time.
- **Book**: approved manuscript, ISBN, title, publisher, and publication/availability state.
- **PrintOrder**: book, selected distributor, quantity, idempotency key, external order reference, and order/delivery status.
- **StockRecord, InventoryLedgerEntry**: book and distributor stock, source event ID, event type, quantity/delta, and recorded time. Enforce uniqueness on source event ID and appropriate book/distributor keys.

Persist blob identifiers (container and blob name) as the durable reference; generate short-lived authorized links when a user needs access. Do not persist public blob URLs or expose private storage credentials. Use database constraints and transactions for related relational updates. Because SQL and Blob Storage do not share a transaction, define recoverable upload states and cleanup/repair behavior for partial failures.

## 3. Event contract and delivery

Use the Event Grid event schema for events published to the custom topic. Every event must include `id`, `eventType`, `subject`, `eventTime`, `dataVersion`, and a typed `data` object. `data` must contain the relevant aggregate identifiers (for example `manuscriptId`, `versionId`, `bookId`, `distributorId`, or `printOrderId`), and a stable event/aggregate sequence for ordering-sensitive updates. Do not include manuscript contents, credentials, or other unnecessary sensitive data in event payloads.

The Service Bus delivery in Bicep maps the top-level `eventType` to the `eventType` application property and the Event Grid `id` to `MessageId`. Preserve these names and the event body shape because the subscription SQL rules depend on `eventType`. Consumers must validate event type and payload before applying changes.

| Event type | Producer | Purpose |
| --- | --- | --- |
| `ManuscriptUploaded` | Web App/API | A new manuscript and initial version are persisted |
| `EditorAssigned` | Editor assignment service | An editor has been assigned |
| `ManuscriptEdited` | Web App/API | An editor version and edits are persisted |
| `ManuscriptRevised` | Web App/API | An author revision and feedback are persisted |
| `ManuscriptApproved` | Web App/API | Approval is persisted and publication work can begin |
| `ProofprintCreated` | Proofprint generation service | A generated PDF is stored and linked to its source version |
| `PrintOrderRequested` | Web App/API | A publisher's print order is persisted |
| `BooksPrinted`, `BooksDelivered` | PrintOnDemand callback/API | Production or delivery has been confirmed |
| `BooksDistributed`, `BooksSold`, `StockUpdated` | Distributor callback/API | Distribution or stock has changed |
| `LowStockAlert`, `StockDepleted` | Distributor callback/API | Stock has reached a configured alert threshold |
| `BookAvailableForSale` | Distributor callback/API | A distributor listing is open for sale |

`LowStockAlert` currently means stock reaches 10; `StockDepleted` means zero. Make the threshold configurable if business rules later require it. A publisher's print order is not proof of physical stock or availability.

### Routing provisioned by Bicep

Event Grid's worker subscription allowlist and the Service Bus subscription rules are both part of routing. The worker allowlist admits the union of work-event types; the subscription rules then filter each consumer:

| Service Bus subscription | Event types |
| --- | --- |
| `editor-assignment` | `ManuscriptUploaded` |
| `proofprint-generation` | `ManuscriptUploaded`, `ManuscriptEdited`, `ManuscriptRevised` |
| `print-demand-orders` | `ManuscriptApproved`, `PrintOrderRequested` |
| `progress-tracking` | `ManuscriptUploaded`, `EditorAssigned`, `ManuscriptEdited`, `ManuscriptRevised`, `ManuscriptApproved` |
| `inventory` | `BooksPrinted`, `BooksDelivered`, `BooksDistributed`, `BooksSold`, `StockUpdated`, `LowStockAlert`, `StockDepleted` |

Notification events are delivered to the optional HTTPS webhook, not through the Service Bus topic: `EditorAssigned`, `ProofprintCreated`, `ManuscriptEdited`, `ManuscriptRevised`, `ManuscriptApproved`, `BookAvailableForSale`, `LowStockAlert`, and `StockDepleted`. A single event may be routed to both work and notification destinations, such as `ManuscriptApproved` and `LowStockAlert`.

Event Grid and Service Bus provide at-least-once delivery. Consumers must be idempotent, deduplicate using the event ID, tolerate redelivery, and avoid moving an aggregate backwards when events arrive out of order. Use bounded retries with exponential backoff and jitter for transient external failures; do not retry permanent client errors indefinitely. Persist failures with enough context for investigation and replay. The template configures Event Grid for 10 attempts/24 hours, a blob dead-letter destination, Service Bus duplicate detection for 10 minutes, 10 deliveries, and a 14-day message TTL; consumer-level deduplication must outlive these transport windows.

## 4. Service specifications

### 4.1 Web App / application API

**Responsibilities**

- Provide authorized author, editor, publisher, and distributor workflows for manuscript submission, editing, revision, approval, progress, orders, and inventory views.
- Persist manuscript metadata and each immutable file version; store files in the private `manuscripts` container.
- Persist edits, highlights, feedback, approval, print orders, and other domain changes before publishing their corresponding events.
- Enforce role- and relationship-based access: authors access their own manuscripts; editors access assigned work; publishers access their titles and orders; distributor data is scoped to the distributor.
- Display lifecycle history and inventory from SQL rather than deriving current state from message delivery.

**Acceptance criteria**

- An upload creates one manuscript and initial version; a retry with the same client idempotency key does not create duplicates.
- Editing and revision create new versions linked to their parent and persist highlights/feedback before emitting events.
- Approval is rejected unless the caller is an authorized editor and the manuscript is in an approvable state.
- A persisted mutation whose event publication fails is retried or recovered through a transactional outbox (or an explicitly equivalent durable mechanism); it must not silently appear successful while required downstream work is lost.
- Private file access is authorized and time-limited. Unauthorized cross-role or cross-tenant access is rejected.

### 4.2 Secured API frontend and callback ingress

**Responsibilities**

- Be the only public application ingress for external users and integrations. Authenticate callers, authorize the requested operation, validate payloads and signatures/credentials, enforce request-size limits and rate limits, and publish accepted domain events to Event Grid.
- Expose a separately secured Event Grid notification webhook. Complete the Event Grid validation handshake and forward only allowlisted notification types to the appropriate private notification handler.
- Support PrintOnDemand and distributor callbacks for production, delivery, stock, and listing events. Reject invalid or unauthenticated callbacks; do not trust a caller-supplied event type or aggregate identifier without validating it.
- Return clear success/failure status codes. For callbacks, acknowledge only after the event is durably accepted for publication; use a stable callback/event ID for deduplication.

**Acceptance criteria**

- Event Grid validation succeeds and the webhook rejects unsupported event types or malformed payloads.
- Replayed callback IDs do not create duplicate domain effects.
- Invalid authentication, invalid payloads, throttling, and dependency failures are observable and do not return success-shaped responses.
- The frontend does not expose AKS services or private data services directly.

### 4.3 Editor assignment service

**Input:** `ManuscriptUploaded` from `editor-assignment`.

**Responsibilities**

- Select an eligible editor using a replaceable assignment policy. The assignment policy, workload balancing rules, and editor eligibility criteria are business decisions not defined by the README or Bicep and must be confirmed before production.
- Persist exactly one assignment per manuscript, update its assignment state, and publish `EditorAssigned` only after persistence succeeds.
- If no eligible editor exists, record a visible, actionable failure state; do not repeatedly assign or silently discard the manuscript.

**Acceptance criteria**

- Reprocessing the same event returns the existing assignment and emits no duplicate assignment effect.
- Concurrent processing cannot assign multiple editors to one manuscript.
- Assignment failures are retried only when transient; permanent no-capacity cases are surfaced for operator action.

### 4.4 Proofprint generation service

**Input:** `ManuscriptUploaded`, `ManuscriptEdited`, and `ManuscriptRevised` from `proofprint-generation`.

**Responsibilities**

- Resolve the exact immutable manuscript version referenced by the event and submit it to the PrintOnDemand proofprint API.
- Apply timeouts, bounded transient retries, and a stable idempotency key so a timeout does not create duplicate external jobs.
- Validate the returned PDF, store it in the private `proofprints` container, persist its blob reference and source-version link, then publish `ProofprintCreated`.
- Record external request/reference IDs and failure details without storing manuscript contents in logs.

**Acceptance criteria**

- A duplicate event does not create a second proofprint for the same version unless an explicit regeneration request is supported.
- `ProofprintCreated` is published only after both the PDF and its database link are durable.
- Missing source versions, invalid PDF responses, and permanent external errors become visible failures; transient failures use bounded retries.
- Notification links point to the correct version and are authorized/short-lived.

### 4.5 Publication progress service

**Input:** lifecycle events from `progress-tracking`.

**Responsibilities**

- Map `ManuscriptUploaded` → `Submitted`, `EditorAssigned` → `AssignedForEditing`, `ManuscriptEdited` → `AwaitingAuthorResponse`, `ManuscriptRevised` → `AwaitingEditorReview`, and `ManuscriptApproved` → `Approved`.
- Append one status-history entry per event and update the current status atomically.
- Check valid transitions and event/aggregate sequence before updating current status. Retain out-of-order history for diagnosis without regressing the current state.

**Acceptance criteria**

- Repeated event IDs do not create repeated history entries.
- A later sequence cannot be overwritten by an older event.
- Status and history are committed together; the Web App can read both consistently.
- Invalid transitions are logged and surfaced for correction rather than silently coerced.

### 4.6 Print demand and order service

**Input:** `ManuscriptApproved` and `PrintOrderRequested` from `print-demand-orders`.

**Responsibilities**

- For approval, resolve the approved book/title and submit the production request to PrintOnDemand.
- For a publisher print order, validate the book, distributor, and positive quantity, then submit the order with a durable idempotency key.
- Persist external request/reference IDs and order states. Treat `BooksPrinted` and `BooksDelivered` callbacks as separate confirmations consumed by the inventory service; a submitted order does not itself increase stock.

**Acceptance criteria**

- Duplicate events and retries after a timeout cannot submit duplicate production orders.
- Invalid order quantities, unavailable titles, or unauthorized publishers are rejected before the external call.
- External order status is traceable from the BookFlow order record.
- Permanent provider failures are visible and do not cause infinite retries.

### 4.7 Inventory service

**Input:** inventory events from `inventory`.

**Responsibilities**

- Maintain stock by book and distributor and append ledger entries for `BooksPrinted`, `BooksDelivered`, `BooksDistributed`, `BooksSold`, and `StockUpdated`.
- Apply each source event at most once using a unique event ID. Validate quantities and prevent invalid negative stock unless the domain explicitly permits it.
- Store `LowStockAlert` and `StockDepleted` as alerts/state changes; do not interpret an alert callback as an inventory delta unless its contract explicitly carries one.
- Make current stock, totals, and sales data available to authorized Web App queries.

**Acceptance criteria**

- Replaying an event does not change totals twice.
- Ledger and current stock update atomically.
- A distributor's absolute `StockUpdated` report is distinguishable from quantity deltas such as `BooksSold`; applying it twice does not double-subtract.
- Conflicting or stale stock reports are detected using source sequence/time and reported for reconciliation.

### 4.8 Notification handlers

**Input:** notification events delivered through the secured API frontend.

**Responsibilities**

- Handle editor assignment, proofprint-ready, author edit/revision/approval, publisher availability, and publisher stock alerts.
- Resolve recipient and relevant links from authorized domain data; send through a replaceable email provider for email notifications and the configured publisher API for availability notifications.
- Keep notification delivery separate from domain state changes. Use an outbox or equivalent durable retry mechanism, and deduplicate by event ID/recipient/template.
- Avoid including manuscript or proofprint content in email; send a protected application link.

**Acceptance criteria**

- Each notification type has a defined recipient, template, and deep link.
- Duplicate webhook delivery does not create duplicate notification side effects where provider idempotency is available.
- Provider failures are retried within bounded limits and made observable.
- `BookAvailableForSale` notifies the author with the distributor listing; `LowStockAlert`/`StockDepleted` notify the responsible publisher. Approval availability is not represented as physical distributor stock.

The README diagrams describe these handlers as separate services; they may initially be implemented as modules in one notification worker, provided each event type has isolated routing, failure handling, and tests.

## 5. Cross-cutting implementation requirements

- **Configuration:** use environment-specific configuration for resource names, API endpoints, retry limits, and stock threshold. Keep secrets in Key Vault or an approved secret store; never commit them.
- **Identity:** use managed identity for Azure Storage, Event Grid, Service Bus, and Key Vault. The template currently grants a shared workload identity broad service roles; production deployments should verify the least privilege required by each workload. SQL database users/roles must be provisioned separately as noted in the README.
- **Observability:** emit structured logs, metrics, and distributed trace IDs including event ID, aggregate ID, consumer, attempt, and external correlation ID. Never log access tokens, manuscript contents, or secret values. Alert on dead-letter growth and sustained consumer failures.
- **Health and shutdown:** provide liveness/readiness checks; stop accepting new messages during shutdown and complete or abandon in-flight messages safely.
- **Scalability:** make workers stateless apart from SQL/Blob/transport state. Scale consumers based on backlog and set replica/resource limits so downstream APIs are not overwhelmed.
- **External contracts:** define and version the PrintOnDemand, email, publisher, and distributor contracts before implementation. Authenticate callbacks, validate schema and replay protection, and use test doubles in local development.
- **Testing:** unit-test domain rules and transition logic; integration-test SQL, Blob Storage, and Service Bus adapters; test duplicate, out-of-order, malformed, transient-failure, and dead-letter cases; exercise each end-to-end workflow with external test doubles. Validate cloud identity, private networking, Event Grid delivery, and SQL permissions in an Azure test environment because local emulators do not reproduce them.

## 6. Implementation completion criteria

The service implementation is ready for an end-to-end acceptance run when:

1. Database migrations create the domain schema and SQL permissions are provisioned for the runtime identity.
2. All services are containerized, deployed to the `bookflow` namespace, and use the provisioned workload identity.
3. Event Grid notification endpoint configuration, Service Bus consumers, dead-letter inspection/replay, and monitoring are operational.
4. Each workflow in the README has an automated integration test that verifies persisted state and emitted events, including redelivery and failure recovery.
5. External API contracts, authentication, rate limits, idempotency, and test doubles are documented and exercised.
