# Save API examples

Run headlessly with `dotnet run --project Smoke -p:TurianDir=/media/work/dev/Turian`. The examples use disposable temporary local storage and in-memory cloud storage; they require no credentials and leave real user saves untouched.

`LocalSlots` demonstrates atomic writes, metadata, listing and explicit backup reads. `InterruptedProcess` kills a child process during a real 96 MiB temporary-file write and verifies that the committed slot remains complete. `SchemaUpgrade` migrates a versioned game DTO. `SerializerPlugins` demonstrates a replaceable Brotli JSON encoding, retaining a legacy reader and AES-GCM payload encryption. `CloudConflict` keeps both offline branches and applies an explicit conflict choice. `ServerReplay` stores tick/RNG state, mod fingerprints and persistent entity references, then verifies a 10,000-entity save/resume replay and prints capture/queue timings.

The cloud provider is simulated; replace it with an `ISaveStorage` adapter whose conditional writes are enforced by the remote service. The server replay is headless persistence, not a network replication demo. The printed timings are observations on the current machine, not a frame-time guarantee.
