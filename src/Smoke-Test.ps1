param(
    [string]$FrontendUrl = 'http://localhost:8080',
    [string]$ProvidersUrl = 'http://localhost:8081',
    [string]$ProviderToken = $env:BOOKFLOW_PROVIDER_TOKEN
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ProviderToken)) {
    $line = Get-Content (Join-Path $PSScriptRoot '.env') | Where-Object { $_ -like 'BOOKFLOW_PROVIDER_TOKEN=*' }
    $ProviderToken = $line.Substring('BOOKFLOW_PROVIDER_TOKEN='.Length)
}
$tokens = @{}
foreach ($role in @('Admin', 'Author', 'Editor', 'Publisher', 'Distributor', 'PrintProvider', 'EventGrid')) {
    $result = Invoke-RestMethod "$ProvidersUrl/tokens/$role" -Headers @{ Authorization = "Bearer $ProviderToken" }
    $tokens[$role] = $result.token
}
function Invoke-BookFlow($role, $method, $path, $body) {
    $arguments = @{
        Uri = "$FrontendUrl$path"
        Method = $method
        Headers = @{ Authorization = "Bearer $($tokens[$role])" }
    }
    if ($null -ne $body) {
        $arguments.ContentType = 'application/json'
        $arguments.Body = ConvertTo-Json -InputObject $body -Depth 20 -Compress
    }
    $response = Invoke-RestMethod @arguments
    return $response
}
function Wait-BookFlow($check, $description) {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $value = & $check
        if ($value) { return $value }
        Start-Sleep -Seconds 1
    }
    throw "Timed out waiting for $description."
}
function Assert-BookFlow($condition, $description) {
    if (-not $condition) { throw "Smoke assertion failed: $description" }
}

foreach ($role in @('Author', 'Editor', 'Publisher', 'Distributor')) {
    $id = $role.ToLowerInvariant()
    try {
        Invoke-BookFlow Admin GET "/api/participants/$id" $null | Out-Null
    }
    catch {
        if ([int]$_.Exception.Response.StatusCode -ne 404) { throw }
        Invoke-BookFlow Admin POST '/api/participants' @{ id = $id; name = $role; role = $role; email = "$id@example.test" } | Out-Null
    }
}
$validation = @{ id = 'validation'; eventType = 'Microsoft.EventGrid.SubscriptionValidationEvent'; data = @{ validationCode = 'bookflow-validation' } }
$handshake = Invoke-BookFlow EventGrid POST '/events/notifications' @($validation)
Assert-BookFlow ($handshake.validationResponse -eq 'bookflow-validation') 'Event Grid validation response'
$upload = @{
    title = 'Smoke manuscript'
    publisherId = 'publisher'
    idempotencyKey = [Guid]::NewGuid().ToString()
    fileBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('Original manuscript'))
}
$manuscript = Invoke-BookFlow Author POST '/api/manuscripts' $upload
$replay = Invoke-BookFlow Author POST '/api/manuscripts' $upload
Assert-BookFlow ($manuscript.id -eq $replay.id) 'upload idempotency'
$id = $manuscript.id
Wait-BookFlow { $m = Invoke-BookFlow Author GET "/api/manuscripts/$id" $null; $m.editorId -eq 'editor' } 'editor assignment' | Out-Null
$initial = Wait-BookFlow {
    $versions = @(Invoke-BookFlow Author GET "/api/manuscripts/$id/versions" $null)
    if ($versions.Count -eq 1 -and $versions[0].proofprintId) { $versions[0] }
} 'initial proofprint'
$download = Invoke-WebRequest "$FrontendUrl/api/manuscripts/$id/proofprints/$($initial.proofprintId)/file" `
    -Headers @{ Authorization = "Bearer $($tokens['Author'])" } -UseBasicParsing
Assert-BookFlow ($download.StatusCode -eq 200 -and $download.Headers['Content-Type'] -like 'application/pdf*') 'authorized PDF download'
$edited = Invoke-BookFlow Editor POST "/api/manuscripts/$id/edits" @{
    parentVersionId = $initial.id
    idempotencyKey = [Guid]::NewGuid().ToString()
    fileBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('Edited manuscript'))
    highlights = @(@{ startOffset = 0; endOffset = 8; originalText = 'Original'; editedText = 'Edited' })
}
$revised = Invoke-BookFlow Author POST "/api/manuscripts/$id/revisions" @{
    parentVersionId = $edited.id
    idempotencyKey = [Guid]::NewGuid().ToString()
    fileBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('Revised manuscript'))
    feedback = 'Accepted the edits'
    highlights = @()
}
# Generate an unused ISBN-13 with a valid checksum.
$digits = '978' + (Get-Random -Minimum 100000000 -Maximum 999999999).ToString()
$sum = 0
for ($index = 0; $index -lt 12; $index++) {
    $weight = 1
    if ($index % 2 -eq 1) { $weight = 3 }
    $sum += ([int]$digits[$index] - [int][char]'0') * $weight
}
$isbn = $digits + ((10 - ($sum % 10)) % 10).ToString()
$book = Invoke-BookFlow Editor POST "/api/manuscripts/$id/approval" @{
    isbn = $isbn; distributorId = 'distributor'; quantity = 100; idempotencyKey = [Guid]::NewGuid().ToString()
}
Wait-BookFlow {
    $m = Invoke-BookFlow Publisher GET "/api/manuscripts/$id" $null
    $m.progressStatus -eq 'Approved' -and $m.progressSequence -eq 5
} 'approval progress projection' | Out-Null
Wait-BookFlow {
    $versions = @(Invoke-BookFlow Author GET "/api/manuscripts/$id/versions" $null)
    $versions.Count -eq 3 -and @($versions | Where-Object { $_.proofprintId }).Count -eq 3
} 'all three proofprints' | Out-Null
$order = Invoke-BookFlow Publisher POST '/api/orders' @{
    bookId = $book.id; distributorId = 'distributor'; quantity = 50; idempotencyKey = [Guid]::NewGuid().ToString()
}
Wait-BookFlow {
    $o = Invoke-BookFlow Publisher GET "/api/orders/$($order.id)" $null
    $o.status -eq 'Submitted' -and $o.externalReference
} 'print submission' | Out-Null
$tenant = '11111111-1111-1111-1111-111111111111'
$delivery = @{
    id = [Guid]::NewGuid().ToString('N'); eventType = 'BooksDelivered'
    subject = "tenants/$tenant/books/$($book.id)"; eventTime = [DateTimeOffset]::UtcNow.ToString('O'); dataVersion = '1'
    data = @{ tenantId = $tenant; sequence = 1; bookId = $book.id; distributorId = 'distributor'; printOrderId = $order.id; quantity = 50 }
}
Invoke-BookFlow PrintProvider POST '/callbacks' $delivery | Out-Null
Invoke-BookFlow PrintProvider POST '/callbacks' $delivery | Out-Null
Wait-BookFlow {
    $stock = @(Invoke-BookFlow Publisher GET "/api/books/$($book.id)/stock" $null)
    $stock.Count -eq 1 -and $stock[0].availableQuantity -eq 50
} 'stock credit without duplicate delivery' | Out-Null
$sale = @{
    id = [Guid]::NewGuid().ToString('N'); eventType = 'BooksSold'
    subject = "tenants/$tenant/books/$($book.id)"; eventTime = [DateTimeOffset]::UtcNow.ToString('O'); dataVersion = '1'
    data = @{ tenantId = $tenant; sequence = 2; bookId = $book.id; distributorId = 'distributor'; quantity = 40 }
}
Invoke-BookFlow Distributor POST '/callbacks' $sale | Out-Null
Invoke-BookFlow Distributor POST '/callbacks' $sale | Out-Null
Wait-BookFlow {
    $stock = @(Invoke-BookFlow Publisher GET "/api/books/$($book.id)/stock" $null)
    $stock[0].availableQuantity -eq 10 -and $stock[0].soldQuantity -eq 40
} 'sales debit without duplicate subtraction' | Out-Null
$history = @(Invoke-BookFlow Publisher GET "/api/manuscripts/$id/history" $null)
Assert-BookFlow ($history.Count -eq 5) 'five lifecycle history entries'
Write-Output "Smoke workflow passed. Manuscript: $id; book: $($book.id); order: $($order.id)."
