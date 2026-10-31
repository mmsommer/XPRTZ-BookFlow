# BookFlow HTTP API

## OpenAPI documents

All applications use ASP.NET Core's built-in OpenAPI support. In `Development`, each exposes `GET /openapi/v1.json`; normal production workloads do not expose documentation. The JSON document can be imported into an OpenAPI-compatible API client or viewer. No Swagger UI or external CDN is required.

In the local Rancher Compose environment:

- **Public application API:** <http://localhost:8080/openapi/v1.json>. This frontend document includes all 17 concrete WebApp operations, callbacks, Event Grid notification ingress, browser entry point, and health probes (22 operations). The underlying wildcard proxy is an implementation detail, not an API contract.
- **Development providers:** <http://localhost:8081/openapi/v1.json>. This document requires `Authorization: Bearer BOOKFLOW_PROVIDER_TOKEN`, just like provider requests.
- **Private services:** fetch `/openapi/v1.json` from their service URLs on the Compose network. Workers expose health probes only; they do not expose HTTP event-consumption endpoints.

The frontend document is composed from the private WebApp's generated document on each documentation request, avoiding a second manually maintained copy of application contracts. The WebApp must be available; upstream failures are reported rather than returning incomplete documentation. Production API forwarding does not depend on documentation being enabled.

Every operation has a stable operation ID, tag, summary, detailed description, and response metadata. Request/response schemas describe JSON DTOs and validation annotations. Bearer requirements are marked per operation; health probes and the browser entry point are anonymous. Enums use their string names. File endpoints document binary media types, and provider mutations document the required `Idempotency-Key` header.

## Authentication and common errors

Application JWTs use `oid` (participant ID), `tid` (publisher tenant ID) and `roles` (application role). An endpoint may require both a role and a relationship to the resource; authentication alone does not grant manuscript access. Participants must belong to the same tenant. Author, Editor, Publisher, Distributor, Admin, PrintProvider and EventGrid are distinct roles.

Use `Authorization: Bearer TOKEN` for protected operations. Local provider endpoints instead require the configured **provider credential**, not a JWT. They are test doubles and cannot run in Production.

| Status | Meaning |
| --- | --- |
| `200` | Read, download, validation handshake or provider operation succeeded |
| `201` | Resource created, or returned by an identical idempotent retry; `Location` identifies it |
| `202` | Callback/notification accepted durably; background processing or delivery is still asynchronous |
| `400` | Malformed JSON, invalid values, unsupported event or validation failure |
| `401` | Missing, invalid or expired bearer credentials |
| `403` | Wrong role, tenant, participant or resource relationship |
| `404` | Resource missing or not visible in this tenant |
| `409` | State/concurrency conflict or an idempotency/event key reused with different data |
| `413` | Body exceeds the server's request-size limit |
| `429` | Frontend limit of 100 requests per identity per one-minute window exceeded |
| `500` | Unexpected server failure |
| `503` | Dependency unavailable |

Errors use Problem Details where the application's error-handling middleware produces the response. Successful probes and accepted callback/notification responses are empty. A readiness check reporting dependency failure returns an empty `503`. Infrastructure-generated responses may differ from application Problem Details.

## Application API (through the frontend)

All paths below require a JWT. Detailed DTO properties, constraints, success schemas and operation-specific error codes are in the generated document.

| Method/path | Required caller | Purpose and contract |
| --- | --- | --- |
| `POST /api/participants` | Admin | Register an active Author, Editor, Publisher or Distributor in this tenant. Only one Publisher per tenant. Returns `201` with the registered ID. |
| `GET /api/participants/{id}` | Admin | Read tenant registration, contact details, role and active status. |
| `GET /api/manuscripts` | Authorized participant | Return at most 100 manuscripts ordered by ID, scoped to owned/assigned/published manuscripts or the Admin's tenant. Other roles receive an empty list. |
| `POST /api/manuscripts` | Registered Author | Supply title, registered `publisherId`, `idempotencyKey` and `fileBase64` decoding to 1 byte through 10 MB. Persist immutable initial version and `ManuscriptUploaded`; return `201`. |
| `GET /api/manuscripts/{id}` | Owning Author, assigned Editor, responsible Publisher or Admin | Read authoritative status and asynchronously projected progress/sequence. |
| `GET /api/manuscripts/{id}/versions` | Manuscript access | Read immutable versions in version-number order, parent IDs, highlights, feedback and optional proofprint IDs. |
| `GET /api/manuscripts/{id}/history` | Manuscript access | Read persisted lifecycle history ordered by sequence; projection may lag mutations. |
| `POST /api/manuscripts/{id}/edits` | Assigned Editor | While AssignedForEditing or AwaitingEditorReview, supply current `parentVersionId`, new file, key and optional highlights. Persist version, transition to AwaitingAuthorResponse and emit `ManuscriptEdited`; return `201`. |
| `POST /api/manuscripts/{id}/revisions` | Owning Author | While AwaitingAuthorResponse, supply current parent version, file, key and nonempty feedback; editor highlights are prohibited. Transition to AwaitingEditorReview and emit `ManuscriptRevised`; return `201`. |
| `POST /api/manuscripts/{id}/approval` | Assigned Editor | While AssignedForEditing or AwaitingEditorReview, supply unused checksum-valid ISBN-13, active distributor, quantity 1 through 1,000,000 and key. Persist orderable Book and initial order, emit `ManuscriptApproved`; return `201`. Does not credit stock. |
| `GET /api/books` | Authorized participant | List visible tenant titles. Distributors can list tenant books. Orderability is not physical inventory. |
| `GET /api/books/{id}` | Book visible in list | Read title, ISBN, manuscript link and orderability. |
| `GET /api/books/{id}/stock` | Book access; registered Distributor scoped to itself | Read available quantity, production/delivery/distribution/sales totals, alert, sequence and time by distributor. Empty array means no recorded stock. |
| `POST /api/orders` | Responsible Publisher | Request quantity 1 through 1,000,000 for an orderable book and active tenant distributor, using `idempotencyKey`. Persist order and `PrintOrderRequested`; return `201`. Printing is asynchronous. |
| `GET /api/orders/{id}` | Book access; Distributor only for its destination | Read Requested/Submitted/Printed/Delivered state and optional provider reference. |
| `GET /api/manuscripts/{id}/versions/{versionId}/file` | Manuscript access | Download that manuscript's immutable version as `application/octet-stream`, attachment `manuscript.bin`, with `Cache-Control: no-store`. |
| `GET /api/manuscripts/{id}/proofprints/{proofprintId}/file` | Manuscript access | Download that manuscript's stored PDF as `application/pdf`, attachment `proofprint.pdf`, with caching disabled. |

Idempotency keys on application mutations are JSON properties, **not** provider headers. Repeating the identical command/key returns the original resource; changed payloads under the same key produce `409`.

## API frontend-specific endpoints

| Method/path | Authentication | Contract |
| --- | --- | --- |
| `GET /` | Anonymous | Return the browser HTML client. Its application requests/downloads still require a JWT. |
| `POST /callbacks` | PrintProvider or the reporting Distributor | Accept one version-1 `DomainEvent`; return `202` only after SQL outbox persistence. PrintProvider reports BooksPrinted/BooksDelivered for an existing matching print order, with exactly the ordered quantity and in Requested-or-Submitted → Printed → Delivered order. Stable event IDs deduplicate retries; additional or out-of-order fulfillment callbacks are rejected. Distributors report only their own distribution/sales/stock/alerts/listing data. |
| `POST /events/notifications` | EventGrid role | Either validate a subscription (`200`) or durably forward 1 through 100 notification events (`202`). Does not wait for recipient resolution or external delivery. |

Callbacks include `id`, `eventType`, `subject`, `eventTime`, `dataVersion: "1"`, and `data` with tenant/book/distributor IDs, positive stream `sequence`, quantity and order ID where applicable. Quantities are 0 through 1,000,000. Sequences must increase consistently across producers for each book/distributor stream. `StockUpdated` is absolute; `BooksDelivered` credits stock; `BooksSold` and `BooksDistributed` debit it; `BooksPrinted` updates production totals only. Alerts carry the configured low-stock threshold (default 10) or zero without debiting again. `BookAvailableForSale` requires an allowlisted HTTPS listing URL.

Event Grid notification types: EditorAssigned, ProofprintCreated, ManuscriptEdited, ManuscriptRevised, ManuscriptApproved, BookAvailableForSale, LowStockAlert and StockDepleted. The validation handshake has this separate shape:

```json
[
  {
    "id": "validation",
    "eventType": "Microsoft.EventGrid.SubscriptionValidationEvent",
    "data": { "validationCode": "example-code" }
  }
]
```

Response:

```json
{ "validationResponse": "example-code" }
```

The generated webhook schema describes both validation and domain events, with separate `200` and `202` responses.

## Private notification ingress

`POST /notifications` on `BookFlow.Notifications` requires the EventGrid role. It accepts an array of 1 through 100 allowlisted domain events and returns empty `202` after persistent receipt storage. Identical event IDs are deduplicated; conflicting batch/retry payloads return `409`. Recipient resolution and sending retry independently. Malformed identifiers/events return `400` before acknowledgement.

Proofprint events require manuscript/version/proofprint IDs. Approval events require manuscript/book IDs. Availability and stock alerts require book IDs; availability also requires a listing URL. This ingress is private and is not exposed as a direct host port by Compose.

## Development providers

Except for liveness, these endpoints require `Authorization: Bearer BOOKFLOW_PROVIDER_TOKEN`. All POST operations require `Idempotency-Key` with 1 through 300 characters and reject key reuse with a changed payload.

| Method/path | Request | Response / behavior |
| --- | --- | --- |
| `GET /tokens/{role}` | Case-sensitive Admin, Author, Editor, Publisher, Distributor, PrintProvider or EventGrid | `200 { "token": "JWT" }`; lowercase-role demo identity, configured tenant, one-hour expiry |
| `POST /proofprints` | Raw bytes, `application/octet-stream` | `200 application/pdf`; synthetic PDF, **not** a rendering of the manuscript |
| `POST /orders` | `bookId`, `title`, `isbn`, `distributorId`, `quantity` JSON | `200 { "reference": "stable-provider-reference" }`; records a synthetic order, no real printing |
| `POST /emails` | `destination`, `template`, `link`, `publisherApi: false` JSON | Empty `202`; records a synthetic email, sends nothing |
| `POST /publisher-notifications` | Same shape; publisher identity as `destination`, `publisherApi: true` | Empty `202`; records a synthetic publisher notification |

## Health and background workers

All nine applications expose anonymous `GET /health/live` (empty `200`). All except LocalProviders also expose anonymous `GET /health/ready` (empty `200` if SQL is reachable, empty `503` when the connectivity check fails).

EditorAssignment, ProofprintGeneration, ProgressTracking, PrintDemandOrders and Inventory have no HTTP command endpoints. They consume their filtered Service Bus subscriptions in Azure or durable local adapter deliveries in Development. Their OpenAPI documents contain only these two probes.

## Verification and examples

Each application's `.http` file contains request examples. The [setup guide](README.md) describes Docker, credentials, and local execution.

The OpenAPI tests generate documents and check operation counts, unique IDs, descriptions, security, errors and binary schemas. [Verify-Local.ps1](Verify-Local.ps1) additionally starts all applications, fetches every generated document, checks all local `$ref` targets and per-operation metadata/security, verifies the frontend's concrete routes and both webhook request forms, then executes the normal workflow and persistence checks.
