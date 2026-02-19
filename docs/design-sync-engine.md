# Design: Sync Engine — Edge-Cloud Hybrid Synchronization

## Vision

Qvec är och förblir en **inprocess edge-databas**. Sync Engine är ett helt separat, opt-in lager som låter flera lokala Qvec-instanser synkronisera via en central server. En lokal databas fungerar fullt autonomt utan sync — man kopplar på synkronisering när man behöver det.

```
????????????     ????????????     ????????????
?  Edge A  ?     ?  Edge B  ?     ?  Edge C  ?
?  QvecDB  ?     ?  QvecDB  ?     ?  QvecDB  ?
?          ?     ?          ?     ?          ?
? SyncAgent?     ? SyncAgent?     ? SyncAgent?
????????????     ????????????     ????????????
     ?                ?                ?
     ???????????????????????????????????
             ?   Azure Web PubSub /    ?
             ?   WebSocket / SSE       ?
             ?                         ?
     ?????????????????????????????????????
     ?          Sync Server              ?
     ?  (Azure Function / Web API)       ?
     ?                                   ?
     ?  ???????????????????????????????  ?
     ?  ?  Azure Append Blob          ?  ?
     ?  ?  (Central Event Log)        ?  ?
     ?  ???????????????????????????????  ?
     ?????????????????????????????????????
```

## Principer

1. **Opt-in** — Sync läggs till utanpå en befintlig `QvecDatabase`. Ingen ändring krävs i core.
2. **Offline-first** — Klienten kan läsa, söka och skriva lokalt utan anslutning. Synk sker när anslutningen kommer tillbaka.
3. **Event-sourcing** — Alla mutationer (Add, Update, Delete) loggas som events i en append-only central log.
4. **Idempotent apply** — Events identifieras via Guid. Samma event kan appliceras flera gånger utan dubbletter.
5. **Delta-sync** — Klienter laddar bara ner data som tillkommit sedan senaste synkpunkten (offset-baserat).

## Beroenden

- [Guid som dokument-ID](design-guid-id.md) — Krävs för dedup vid synk.
- [Update & Delete](design-update-delete.md) — Krävs för att kunna applicera remote-events lokalt.

---

## Arkitektur

### Sync Event Format

Varje mutation serialiseras som ett binärt event som skrivs till Append Blob:

```
???????????????????????????????????????????????????
? EventHeader (fast storlek)                      ?
? ??????????????????????????????????????????????? ?
? ? EventType   ? Guid     ? Timestamp? DataLen ? ?
? ? (1 byte)    ? (16 B)   ? (8 B)   ? (4 B)   ? ?
? ??????????????????????????????????????????????? ?
? EventData (variabel)                            ?
? ??????????????????????????????????????????????? ?
? ? Vector (dim * 4 bytes) + Metadata (UTF-8)   ? ?
? ??????????????????????????????????????????????? ?
???????????????????????????????????????????????????
```

```csharp
public enum SyncEventType : byte
{
    Add = 1,
    UpdateVector = 2,
    UpdateMetadata = 3,
    Delete = 4
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SyncEventHeader
{
    public SyncEventType Type;
    public Guid DocumentId;
    public long TimestampTicks;
    public int DataLength;
}
```

Event-storlek per typ:

| EventType | Header | Data | Total (1536-dim) |
|---|---|---|---|
| Add | 29 bytes | 6144 (vektor) + metadata | ~6.7 KB |
| UpdateVector | 29 bytes | 6144 (vektor) | ~6.2 KB |
| UpdateMetadata | 29 bytes | metadata | ~0.5 KB |
| Delete | 29 bytes | 0 | 29 bytes |

### Klient: `QvecSyncAgent`

`QvecSyncAgent` wrapprar en `QvecDatabase` och hanterar all synk-logik. Det är den enda klassen användaren behöver interagera med.

```csharp
/// <summary>
/// Kopplar en lokal QvecDatabase till en central synkserver.
/// Opt-in: skapa databasen som vanligt och lägg till sync när du behöver det.
/// </summary>
public class QvecSyncAgent : IAsyncDisposable
{
    private readonly QvecDatabase _db;
    private readonly ISyncTransport _transport;
    private readonly SyncState _state;
    private CancellationTokenSource _cts;

    public QvecSyncAgent(QvecDatabase db, SyncOptions options)
    {
        _db = db;
        _transport = CreateTransport(options);
        _state = SyncState.Load(options.StateFilePath);
    }

    /// <summary>
    /// Startar bakgrundssynkronisering. Lyssnar på remote events
    /// och skickar lokala ändringar.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 1. Initial catch-up: hämta allt vi missat sedan senaste offset
        await PullDeltaAsync(_cts.Token);

        // 2. Starta realtidslyssning
        await _transport.SubscribeAsync(OnRemoteEvent, _cts.Token);
    }

    /// <summary>
    /// Lägger till lokalt OCH skickar till servern.
    /// Returnerar omedelbart efter lokal skrivning (offline-first).
    /// </summary>
    public async Task<Guid> AddEntryAsync(float[] vector, string metadata)
    {
        Guid id = _db.AddEntry(vector, metadata);
        await EnqueueOutboundEventAsync(SyncEventType.Add, id, vector, metadata);
        return id;
    }

    /// <summary>
    /// Uppdaterar lokalt OCH skickar till servern.
    /// </summary>
    public async Task<bool> UpdateAsync(Guid id, float[] newVector, string newMetadata)
    {
        bool ok = _db.Update(id, newVector, newMetadata);
        if (ok) await EnqueueOutboundEventAsync(SyncEventType.UpdateVector, id, newVector, newMetadata);
        return ok;
    }

    /// <summary>
    /// Tar bort lokalt OCH skickar till servern.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id)
    {
        bool ok = _db.Delete(id);
        if (ok) await EnqueueOutboundEventAsync(SyncEventType.Delete, id, null, null);
        return ok;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        await _transport.DisposeAsync();
        _state.Save();
    }
}
```

### Användning — opt-in pattern

```csharp
// 1. Skapa databasen som vanligt — fungerar fullt autonomt
using var db = new QvecDatabase("local.qvec", dim: 1536, max: 100_000);

// 2. VALFRITT: Koppla på synk
await using var sync = new QvecSyncAgent(db, new SyncOptions
{
    ServerUrl = "https://qvec-sync.azurewebsites.net",
    Transport = SyncTransportType.WebPubSub,
    ConnectionString = "Endpoint=https://...",
    StateFilePath = "local.syncstate"
});
await sync.StartAsync();

// 3. Alla skrivningar via sync-agenten synkas automatiskt
Guid id = await sync.AddEntryAsync(embedding, "{\"text\": \"hello\"}");

// 4. Sökningar sker alltid direkt mot lokal databas — ingen nätverkslatens
var results = db.Search(queryVector, topK: 5);
```

### Transport-abstraktion

Olika realtidskanaler stöds via ett gemensamt interface:

```csharp
public interface ISyncTransport : IAsyncDisposable
{
    /// <summary>Skickar ett event till servern för central lagring.</summary>
    Task SendAsync(SyncEvent evt, CancellationToken ct);

    /// <summary>Prenumererar på realtidsnotifieringar om nya events.</summary>
    Task SubscribeAsync(Func<SyncNotification, Task> onNotification, CancellationToken ct);

    /// <summary>Hämtar events från en given offset (delta-sync).</summary>
    Task<SyncDelta> PullDeltaAsync(long fromOffset, CancellationToken ct);
}

public enum SyncTransportType
{
    WebPubSub,   // Azure Web PubSub (WebSocket)
    Sse,         // Server-Sent Events
    WebSocket    // Raw WebSocket
}
```

### SyncState — lokal offset-tracking

```csharp
/// <summary>
/// Sparar klientens synk-position till disk. Överlever omstarter.
/// </summary>
public class SyncState
{
    public long LastSyncedOffset { get; set; }
    public DateTime LastSyncedUtc { get; set; }
    public string StateFilePath { get; init; }

    public static SyncState Load(string path) { /* läs från fil */ }
    public void Save() { /* skriv till fil */ }
}
```

---

## Server: Sync Relay

Servern är en tunn relay som inte behöver förstå vektordata — den lagrar och vidarebefordrar binära events.

### Ansvar

1. **Append** — Ta emot events från klienter och skriva till Append Blob i strikt ordning.
2. **Notify** — Skicka lättviktsnotifiering via Web PubSub: `{"offset": 124500}`.
3. **Serve Delta** — Hantera `GET /delta?from={offset}` med Range-headers mot Append Blob.

### Endpoints

```
POST   /events              — Tar emot SyncEvent, skriver till blob, notifierar
GET    /delta?from={offset}  — Returnerar alla events sedan offset (Range Request)
GET    /status               — Returnerar blob-storlek och antal anslutna klienter
WS     /ws                   — WebSocket-anslutning för realtidsprenumeration
```

### Implementering (Azure Function)

```csharp
public class SyncFunction
{
    private readonly AppendBlobClient _blob;
    private readonly WebPubSubServiceClient _pubsub;

    [Function("AppendEvent")]
    public async Task<IActionResult> AppendEvent(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "events")] HttpRequest req)
    {
        byte[] eventBytes = await req.Body.ReadAsByteArrayAsync();

        // Atomisk append — Append Blob garanterar ordning
        await _blob.AppendBlockAsync(new BinaryData(eventBytes));

        long newOffset = (await _blob.GetPropertiesAsync()).Value.ContentLength;

        // Push notifiering till alla klienter
        await _pubsub.SendToAllAsync(
            BinaryData.FromObjectAsJson(new { type = "NEW_DATA", offset = newOffset }),
            ContentType.ApplicationJson);

        return new OkObjectResult(new { offset = newOffset });
    }

    [Function("GetDelta")]
    public async Task<IActionResult> GetDelta(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "delta")] HttpRequest req)
    {
        long from = long.Parse(req.Query["from"]);
        long blobLength = (await _blob.GetPropertiesAsync()).Value.ContentLength;

        if (from >= blobLength)
            return new OkObjectResult(Array.Empty<byte>());

        // Range request — bara de nya byten
        var range = new HttpRange(from, blobLength - from);
        var download = await _blob.DownloadContentAsync(new BlobDownloadOptions { Range = range });

        return new FileContentResult(download.Value.Content.ToArray(), "application/octet-stream");
    }
}
```

---

## Synk-flöde: steg för steg

### Skrivning (klient ? server ? alla)

```
Edge A                     Server                    Edge B
  ?                          ?                          ?
  ?  1. db.AddEntry(v, m)   ?                          ?
  ?  (lokal, instant)       ?                          ?
  ?                          ?                          ?
  ?  2. POST /events ????????                          ?
  ?     {Add, guid, v, m}   ?                          ?
  ?                          ?  3. Append Blob.Append() ?
  ?                          ?                          ?
  ?                          ?  4. Web PubSub ???????????
  ?                          ?  {"offset": 131072}      ?
  ?                          ?                          ?
  ?                          ?  5. GET /delta?from=X ????
  ?                          ?                          ?
  ?                          ?  6. Event bytes ??????????
  ?                          ?                          ?
  ?                          ?     7. db.AddEntry(v, m, ?
  ?                          ?        externalId: guid) ?
```

### Reconnect / Catch-up

```
Edge C (var offline i 2 timmar)
  ?
  ?  StartAsync()
  ?  1. Läs _state.LastSyncedOffset (t.ex. 8192)
  ?  2. GET /delta?from=8192
  ?  3. Applicera alla events sekventiellt
  ?  4. Uppdatera _state.LastSyncedOffset
  ?  5. Prenumerera på realtid
```

---

## Konflikthantering

Guid löser ID-konflikter, men vad händer om två klienter uppdaterar samma dokument samtidigt?

| Strategi | Beskrivning |
|---|---|
| **Last-Write-Wins (LWW)** | Servern serialiserar alla events. Den sista UpdateVector som skrivs till Append Blob vinner. Enkelt och deterministiskt. |
| **Timestamp-baserad** | Varje event har `TimestampTicks`. Vid apply: skippa events äldre än lokalt timestamp. Kräver klocksynk. |
| **Application-level** | Exponera konflikter uppåt via callback. Låt applikationen bestämma. |

**Rekommendation:** Börja med **Last-Write-Wins** (LWW) — det är det enklaste och passar bra för vektordata där "senaste embedningen" normalt är den korrekta.

---

## Projektstruktur

Sync Engine implementeras som ett separat projekt för att behålla Qvec.Core utan beroenden:

```
Qvec.sln
??? Qvec.Core/                  # Ingen ändring — ren embedded DB
??? Qvec.Core.Client/           # Typade klienter
??? Qvec.Sync/                  # ? NYTT: SyncAgent, transporter, state
?   ??? QvecSyncAgent.cs
?   ??? ISyncTransport.cs
?   ??? Transports/
?   ?   ??? WebPubSubTransport.cs
?   ?   ??? SseTransport.cs
?   ?   ??? WebSocketTransport.cs
?   ??? SyncEvent.cs
?   ??? SyncState.cs
?   ??? SyncOptions.cs
??? Qvec.Sync.Server/           # ? NYTT: Azure Function relay
?   ??? SyncFunction.cs
?   ??? host.json
??? Qvec.Api/
??? Qvec.Console.Test/
```

## Påverkade befintliga filer

| Fil | Ändring |
|---|---|
| `Qvec.sln` | Lägg till `Qvec.Sync` och `Qvec.Sync.Server` |
| `Qvec.Core\QvecDatabase.cs` | **Ingen** — sync wrappar utifrån |
| `README.md` | Dokumentation och Quick Start för sync |

## Prestandabudget

| Operation | Latens |
|---|---|
| Lokal skrivning + sökning | Oförändrat (µs–ms) |
| Outbound event ? server | ~50–200 ms (nätverkslatens) |
| Remote event ? lokal apply | ~100–300 ms (notifiering + delta pull + apply) |
| Catch-up (10 000 events) | ~2–5 s (beroende på bandbredd) |
| Idle minnesoverhead | ~1 WebSocket-anslutning + SyncState (bytes) |
